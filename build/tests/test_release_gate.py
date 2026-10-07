import importlib.util
import io
import subprocess
import sys
import tempfile
import unittest
import unittest.mock as mock
import urllib.error
import zipfile
from pathlib import Path


MODULE_PATH = Path(__file__).resolve().parents[1] / "release-gate.py"
MODULE_SPEC = importlib.util.spec_from_file_location("release_gate", MODULE_PATH)
release_gate = importlib.util.module_from_spec(MODULE_SPEC)
sys.modules[MODULE_SPEC.name] = release_gate
MODULE_SPEC.loader.exec_module(release_gate)


MASTER_SHA = "a" * 40
MAINTENANCE_SHA = "b" * 40
OLDER_MAINTENANCE_SHA = "c" * 40


class FakeApi:
    def __init__(self):
        self.branches = {"master": MASTER_SHA, "branch-3.22": MAINTENANCE_SHA}
        self.milestones = [
            {"title": "v3.22.0.5", "state": "open", "number": 1},
            {"title": "v4.0.0.0", "state": "open", "number": 2},
        ]
        self.blockers = []
        self.issue_queries = []
        self.runs = [
            {
                "head_sha": MAINTENANCE_SHA,
                "head_branch": "branch-3.22",
                "event": "push",
                "conclusion": "success",
            },
            {
                "head_sha": MASTER_SHA,
                "head_branch": "master",
                "event": "push",
                "conclusion": "success",
            },
        ]
        self.rulesets = [
            {"id": 7, "target": "tag", "enforcement": "active"}
        ]
        self.ruleset_details = {
            7: {
                "conditions": {
                    "ref_name": {
                        "include": [release_gate.RELEASE_TAG_RULESET_PATTERN],
                        "exclude": [],
                    }
                },
                "rules": [
                    {"type": "update"},
                    {"type": "deletion"},
                ],
                "bypass_actors": [],
            }
        }
        self.tags = {}
        self.releases = {}
        self.commit_messages = {}
        self.merge_base = OLDER_MAINTENANCE_SHA
        self.posts = []
        self.post_error = None

    def get(self, path, *, query=None, missing_ok=False):
        if path.startswith("/git/ref/heads/"):
            branch = path.rsplit("/", 1)[1]
            return {"object": {"type": "commit", "sha": self.branches[branch]}}
        if path.startswith("/git/commits/"):
            sha = path.rsplit("/", 1)[1]
            version = "4.0.0.0" if sha == MASTER_SHA else "3.22.0.5"
            return {
                "message": self.commit_messages.get(sha, f"Release v{version}"),
                "parents": [{"sha": OLDER_MAINTENANCE_SHA}],
                "committer": {"email": release_gate.RELEASE_SIGNER_EMAIL},
                "verification": {
                    "verified": True,
                    "reason": "valid",
                    "payload": "signed commit payload",
                    "signature": "signed commit signature",
                },
            }
        if path.startswith("/git/ref/tags/"):
            tag = path.rsplit("/", 1)[1]
            if tag not in self.tags:
                if missing_ok:
                    return None
                raise AssertionError(f"missing tag {tag}")
            return self.tags[tag]
        if path.startswith("/compare/"):
            return {"merge_base_commit": {"sha": self.merge_base}}
        if path.startswith("/rulesets/"):
            return self.ruleset_details[int(path.rsplit("/", 1)[1])]
        if path.startswith("/releases/tags/"):
            tag = path.rsplit("/", 1)[1]
            if tag not in self.releases:
                if missing_ok:
                    return None
                raise AssertionError(f"missing release {tag}")
            return self.releases[tag]
        raise AssertionError(f"unexpected GET {path}")

    def post(self, path, payload):
        self.posts.append((path, payload))
        if self.post_error is not None:
            error = self.post_error
            self.post_error = None
            raise error
        if path == "/git/refs":
            tag = payload["ref"].removeprefix("refs/tags/")
            self.tags[tag] = {
                "ref": payload["ref"],
                "object": {"type": "commit", "sha": payload["sha"]},
            }
        elif path == "/releases":
            payload = dict(payload)
            payload["published_at"] = "2026-10-02T00:00:00Z"
            self.releases[payload["tag_name"]] = payload
        return payload

    def paginate(self, path, *, query=None):
        if path == "/milestones":
            return self.milestones
        if path == "/issues":
            self.issue_queries.append(query)
            return self.blockers
        if path == "/rulesets":
            return self.rulesets
        raise AssertionError(f"unexpected pagination {path}")

    def paginate_key(self, path, key, *, query=None):
        self.last_run_query = query
        self.last_run_key = key
        return self.runs


class SignatureVerificationTests(unittest.TestCase):
    def test_signature_requires_good_status_and_primary_fingerprint(self):
        primary = release_gate.RELEASE_SIGNER_FINGERPRINT
        signing_subkey = "A" * 40
        valid = (
            f"[GNUPG:] GOODSIG {signing_subkey} Publisher\n"
            f"[GNUPG:] VALIDSIG {signing_subkey} 2026-10-06 1791320000 0 4 0 1 8 00 {primary}\n"
        )
        bad_statuses = ("BADSIG", "EXPSIG", "EXPKEYSIG", "REVKEYSIG", "ERRSIG")
        cases = [(valid, True), (valid.replace("GOODSIG", "TRUST_UNDEFINED"), False)]
        cases.extend((valid + f"[GNUPG:] {status} key\n", False) for status in bad_statuses)

        for output, accepted in cases:
            with self.subTest(output=output):
                results = [
                    subprocess.CompletedProcess(["gpg"], 0, stdout="", stderr=""),
                    subprocess.CompletedProcess(["gpg"], 0, stdout=output, stderr=""),
                ]
                with mock.patch.object(release_gate.subprocess, "run", side_effect=results):
                    if accepted:
                        self.assertEqual(
                            primary, release_gate.signature_fingerprint("payload", "signature")
                        )
                    else:
                        with self.assertRaises(release_gate.ReleaseError):
                            release_gate.signature_fingerprint("payload", "signature")


class ReleaseGateTests(unittest.TestCase):
    def setUp(self):
        signature = mock.patch.object(
            release_gate,
            "signature_fingerprint",
            return_value=release_gate.RELEASE_SIGNER_FINGERPRINT,
        )
        signature.start()
        self.addCleanup(signature.stop)

    def test_version_mapping_and_nuget_normalization(self):
        self.assertEqual("branch-3.22", release_gate.branch_for_version("3.22.0.5"))
        self.assertEqual("master", release_gate.branch_for_version("4.0.0.0"))
        self.assertEqual("4.0.0", release_gate.normalized_package_version("4.0.0.0"))
        self.assertEqual(
            "3.22.0.5", release_gate.normalized_package_version("3.22.0.5")
        )
        for invalid in ("v3.22.0.5", "3.22.5", "03.22.0.5", "5.0.0.0"):
            with self.subTest(invalid=invalid):
                with self.assertRaises(release_gate.ReleaseError):
                    release_gate.branch_for_version(invalid)

    def test_preflight_accepts_exact_protected_tip_and_selected_milestone(self):
        api = FakeApi()

        context = release_gate.preflight(
            api,
            version="3.22.0.5",
            target_commit=MAINTENANCE_SHA,
            workflow_ref="refs/heads/branch-3.22",
            workflow_sha=MAINTENANCE_SHA,
            allow_blockers=False,
            recovery=False,
        )

        self.assertEqual("branch-3.22", context.branch)
        self.assertEqual("v3.22.0.5", context.tag)
        self.assertEqual(1, context.milestone_number)
        self.assertEqual(1, api.issue_queries[-1]["milestone"])
        self.assertEqual(MAINTENANCE_SHA, api.last_run_query["head_sha"])

    def test_preflight_accepts_master_dispatch_for_v4(self):
        api = FakeApi()
        api.milestones = [{"title": "v4.0.0.0", "state": "open", "number": 2}]

        context = release_gate.preflight(
            api,
            version="4.0.0.0",
            target_commit=MASTER_SHA,
            workflow_ref="refs/heads/master",
            workflow_sha=MASTER_SHA,
            allow_blockers=False,
            recovery=False,
        )

        self.assertEqual("master", context.branch)
        self.assertEqual(MASTER_SHA, api.last_run_query["head_sha"])

    def test_prepare_accepts_current_ci_tip_but_rejects_repeat_or_existing_tag(self):
        api = FakeApi()
        api.commit_messages[MAINTENANCE_SHA] = "Validated release source"
        api.blockers = [
            {"number": 326, "title": "artifact checks", "html_url": "https://example/326"}
        ]
        with mock.patch("sys.stderr", new_callable=io.StringIO):
            context = release_gate.prepare_release_commit(
                api,
                version="3.22.0.5",
                target_commit=MAINTENANCE_SHA,
                workflow_ref="refs/heads/branch-3.22",
                workflow_sha=MAINTENANCE_SHA,
            )
        self.assertEqual("branch-3.22", context.branch)

        api.commit_messages[MAINTENANCE_SHA] = "Release v3.22.0.5"
        with mock.patch("sys.stderr", new_callable=io.StringIO):
            with self.assertRaisesRegex(release_gate.ReleaseError, "already exists"):
                release_gate.prepare_release_commit(
                    api,
                    version="3.22.0.5",
                    target_commit=MAINTENANCE_SHA,
                    workflow_ref="refs/heads/branch-3.22",
                    workflow_sha=MAINTENANCE_SHA,
                )

        api.tags["v3.22.0.5"] = {"object": {"type": "commit", "sha": MAINTENANCE_SHA}}
        with mock.patch("sys.stderr", new_callable=io.StringIO):
            with self.assertRaisesRegex(release_gate.ReleaseError, "Tag.*already exists"):
                release_gate.prepare_release_commit(
                    api,
                    version="3.22.0.5",
                    target_commit=MAINTENANCE_SHA,
                    workflow_ref="refs/heads/branch-3.22",
                    workflow_sha=MAINTENANCE_SHA,
                )

    def test_dry_run_reports_but_allows_issue_and_pull_request_blockers(self):
        api = FakeApi()
        api.blockers = [
            {"number": 1, "title": "issue", "html_url": "https://example/1"},
            {
                "number": 2,
                "title": "pull request",
                "html_url": "https://example/2",
                "pull_request": {"url": "https://api.example/2"},
            },
        ]

        with mock.patch("sys.stderr", new_callable=io.StringIO) as stderr:
            release_gate.preflight(
                api,
                version="3.22.0.5",
                target_commit=MAINTENANCE_SHA,
                workflow_ref="refs/heads/branch-3.22",
                workflow_sha=MAINTENANCE_SHA,
                allow_blockers=True,
                recovery=False,
            )

        output = stderr.getvalue()
        self.assertIn("release-blocker issue #1", output)
        self.assertIn("release-blocker pull request #2", output)

    def test_production_rejects_blockers(self):
        api = FakeApi()
        api.blockers = [
            {"number": 294, "title": "gate", "html_url": "https://example/294"}
        ]

        with self.assertRaisesRegex(release_gate.ReleaseError, "open release blocker"):
            release_gate.preflight(
                api,
                version="3.22.0.5",
                target_commit=MAINTENANCE_SHA,
                workflow_ref="refs/heads/branch-3.22",
                workflow_sha=MAINTENANCE_SHA,
                allow_blockers=False,
                recovery=False,
            )

    def test_missing_duplicate_and_closed_milestones_fail_closed(self):
        cases = (
            [],
            [
                {"title": "v3.22.0.5", "state": "open", "number": 1},
                {"title": "v3.22.0.5", "state": "open", "number": 3},
            ],
            [{"title": "v3.22.0.5", "state": "closed", "number": 1}],
        )
        for milestones in cases:
            with self.subTest(milestones=milestones):
                api = FakeApi()
                api.milestones = milestones
                with self.assertRaises(release_gate.ReleaseError):
                    release_gate.resolve_milestone(api, "v3.22.0.5")

    def test_preflight_rejects_wrong_workflow_or_target_sha(self):
        api = FakeApi()
        for workflow_ref, workflow_sha, target_commit in (
            ("refs/heads/topic", MAINTENANCE_SHA, MAINTENANCE_SHA),
            ("refs/heads/3.22", MAINTENANCE_SHA, MAINTENANCE_SHA),
            ("refs/heads/branch-3.22", "d" * 40, MAINTENANCE_SHA),
            ("refs/heads/branch-3.22", MAINTENANCE_SHA, "d" * 40),
        ):
            with self.subTest(
                workflow_ref=workflow_ref,
                workflow_sha=workflow_sha,
                target_commit=target_commit,
            ):
                with self.assertRaises(release_gate.ReleaseError):
                    release_gate.preflight(
                        api,
                        version="3.22.0.5",
                        target_commit=target_commit,
                        workflow_ref=workflow_ref,
                        workflow_sha=workflow_sha,
                        allow_blockers=False,
                        recovery=False,
                    )

    def test_preflight_requires_successful_push_ci_for_exact_sha(self):
        api = FakeApi()
        api.runs = [
            {
                "head_sha": MAINTENANCE_SHA,
                "head_branch": "branch-3.22",
                "event": "pull_request",
                "conclusion": "success",
            }
        ]

        with self.assertRaisesRegex(release_gate.ReleaseError, "successful push CI"):
            release_gate.preflight(
                api,
                version="3.22.0.5",
                target_commit=MAINTENANCE_SHA,
                workflow_ref="refs/heads/branch-3.22",
                workflow_sha=MAINTENANCE_SHA,
                allow_blockers=True,
                recovery=False,
            )

    def test_production_requires_active_tag_ruleset(self):
        api = FakeApi()
        api.rulesets = []

        with self.assertRaisesRegex(release_gate.ReleaseError, "update/delete"):
            release_gate.preflight(
                api,
                version="3.22.0.5",
                target_commit=MAINTENANCE_SHA,
                workflow_ref="refs/heads/branch-3.22",
                workflow_sha=MAINTENANCE_SHA,
                allow_blockers=False,
                recovery=False,
            )

    def test_production_rejects_unsigned_or_wrongly_signed_release_commit(self):
        api = FakeApi()
        original_get = api.get

        for change, expected in (
            ({"message": "ordinary commit"}, "not the release commit"),
            ({"verification": {"verified": False}}, "not verified"),
            ({"committer": {"email": "other@example.com"}}, "wrong committer"),
        ):
            with self.subTest(change=change):
                def changed_get(path, **kwargs):
                    result = original_get(path, **kwargs)
                    if path.startswith("/git/commits/"):
                        result.update(change)
                    return result

                with mock.patch.object(api, "get", side_effect=changed_get):
                    with self.assertRaisesRegex(release_gate.ReleaseError, expected):
                        release_gate.require_signed_release_commit(
                            api, "3.22.0.5", MAINTENANCE_SHA
                        )

        with mock.patch.object(
            release_gate, "signature_fingerprint", return_value="0" * 40
        ):
            with self.assertRaisesRegex(release_gate.ReleaseError, "publisher key"):
                release_gate.require_signed_release_commit(
                    api, "3.22.0.5", MAINTENANCE_SHA
                )

    def test_tag_ruleset_rejects_matching_exclusion(self):
        api = FakeApi()
        detail = api.ruleset_details[7]
        detail["conditions"]["ref_name"]["exclude"] = ["refs/tags/v3.22.*"]
        with self.assertRaises(release_gate.ReleaseError):
            release_gate.require_release_tag_ruleset(api)

    def test_tag_ruleset_must_allow_creation_by_actions_token(self):
        api = FakeApi()
        api.ruleset_details[7]["rules"].append({"type": "creation"})
        with self.assertRaisesRegex(release_gate.ReleaseError, "update/delete"):
            release_gate.require_release_tag_ruleset(api)

    def test_admin_ruleset_audit_rejects_bypass_actors(self):
        api = FakeApi()
        release_gate.audit_release_tag_ruleset_bypass(api)
        api.ruleset_details[7]["bypass_actors"].append(
            {
                "actor_id": 5,
                "actor_type": "RepositoryRole",
                "bypass_mode": "always",
            }
        )
        with self.assertRaisesRegex(release_gate.ReleaseError, "must not have bypass actors"):
            release_gate.audit_release_tag_ruleset_bypass(api)

    def test_recovery_accepts_exact_tagged_ancestor_after_branch_advances(self):
        api = FakeApi()
        api.branches["branch-3.22"] = MAINTENANCE_SHA
        api.tags["v3.22.0.5"] = {
            "object": {"type": "commit", "sha": OLDER_MAINTENANCE_SHA}
        }
        api.merge_base = OLDER_MAINTENANCE_SHA
        api.runs[0]["head_sha"] = OLDER_MAINTENANCE_SHA

        context = release_gate.preflight(
            api,
            version="3.22.0.5",
            target_commit=OLDER_MAINTENANCE_SHA,
            workflow_ref="refs/heads/branch-3.22",
            workflow_sha=MAINTENANCE_SHA,
            allow_blockers=False,
            recovery=True,
        )

        self.assertEqual(OLDER_MAINTENANCE_SHA, context.target_commit)

    def test_recovery_rejects_mismatched_tag(self):
        api = FakeApi()
        api.tags["v3.22.0.5"] = {
            "object": {"type": "commit", "sha": "d" * 40}
        }
        with self.assertRaisesRegex(release_gate.ReleaseError, "Recovery tag"):
            release_gate.require_release_target(
                api,
                branch="branch-3.22",
                tag="v3.22.0.5",
                target_commit=OLDER_MAINTENANCE_SHA,
                recovery=True,
            )

    def test_github_api_paginates_until_short_page(self):
        api = release_gate.GitHubApi("owner/repo", "token")
        pages = [[{"number": index} for index in range(100)], [{"number": 101}]]
        with mock.patch.object(api, "get", side_effect=pages) as get:
            result = api.paginate("/issues", query={"state": "open"})

        self.assertEqual(101, len(result))
        self.assertEqual(1, get.call_args_list[0].kwargs["query"]["page"])
        self.assertEqual(2, get.call_args_list[1].kwargs["query"]["page"])

    def test_github_api_fails_closed_on_http_and_invalid_json(self):
        http_error = urllib.error.HTTPError(
            "https://api.example.test", 500, "server error", {}, io.BytesIO(b"{}")
        )
        for response in (http_error, self._Response(b"not json")):
            with self.subTest(response=response):
                api = release_gate.GitHubApi(
                    "owner/repo", "secret-token", opener=mock.Mock(side_effect=[response])
                    if isinstance(response, Exception)
                    else mock.Mock(return_value=response)
                )
                with self.assertRaises(release_gate.GitHubApiError) as raised:
                    api.get("/milestones")
                self.assertNotIn("secret-token", str(raised.exception))

    def test_verify_source_requires_manual_release_workflow(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            source = Path(temporary_directory)
            self._write_projects(source, "3.22.0.5")
            completed = subprocess.CompletedProcess(
                ["git", "rev-parse"], 0, stdout=f"{MAINTENANCE_SHA}\n", stderr=""
            )
            with mock.patch.object(release_gate.subprocess, "run", return_value=completed):
                with self.assertRaisesRegex(release_gate.ReleaseError, "workflow is missing"):
                    release_gate.verify_source(
                        source,
                        version="3.22.0.5",
                        branch="branch-3.22",
                        target_commit=MAINTENANCE_SHA,
                    )
            workflow = source / ".github/workflows/publish.yml"
            workflow.parent.mkdir(parents=True)
            workflow.write_text("on:\n  workflow_dispatch:\n", encoding="utf-8")
            with mock.patch.object(release_gate.subprocess, "run", return_value=completed):
                release_gate.verify_source(
                    source,
                    version="3.22.0.5",
                    branch="branch-3.22",
                    target_commit=MAINTENANCE_SHA,
                )
            workflow.write_text(
                "on:\n  workflow_dispatch:\n\njobs:\n  release:\n    runs-on: ubuntu-latest\n",
                encoding="utf-8",
            )
            with mock.patch.object(release_gate.subprocess, "run", return_value=completed):
                release_gate.verify_source(
                    source,
                    version="3.22.0.5",
                    branch="branch-3.22",
                    target_commit=MAINTENANCE_SHA,
                )
            workflow.write_text("on:\n  workflow_dispatch:\n  push:\n", encoding="utf-8")
            with mock.patch.object(release_gate.subprocess, "run", return_value=completed):
                with self.assertRaisesRegex(release_gate.ReleaseError, "automatic"):
                    release_gate.verify_source(
                        source,
                        version="3.22.0.5",
                        branch="branch-3.22",
                        target_commit=MAINTENANCE_SHA,
                    )

    def test_verify_packages_accepts_v4_normalization_and_maintenance_version(self):
        for version, packed_version in (("4.0.0.0", "4.0.0"), ("3.22.0.5", "3.22.0.5")):
            with self.subTest(version=version):
                with tempfile.TemporaryDirectory() as temporary_directory:
                    package_directory = Path(temporary_directory)
                    self._write_packages(package_directory, packed_version)
                    packages = release_gate.verify_packages(package_directory, version)
                    self.assertEqual(
                        {project.package_id for project in release_gate.PROJECTS},
                        set(packages),
                    )

    def test_verify_packages_rejects_extra_or_wrong_version(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            package_directory = Path(temporary_directory)
            self._write_packages(package_directory, "4.0.0")
            (package_directory / "unexpected.1.0.0.nupkg").write_bytes(b"not a zip")
            with self.assertRaisesRegex(release_gate.ReleaseError, "Expected three"):
                release_gate.verify_packages(package_directory, "4.0.0.0")

        with tempfile.TemporaryDirectory() as temporary_directory:
            package_directory = Path(temporary_directory)
            self._write_packages(package_directory, "4.0.1")
            with self.assertRaisesRegex(release_gate.ReleaseError, "Unexpected version"):
                release_gate.verify_packages(package_directory, "4.0.0.0")

    def test_recovery_accepts_only_matching_published_package(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            package_directory = Path(temporary_directory)
            self._write_packages(package_directory, "3.22.0.5")
            package = package_directory / "ScyllaDBCSharpDriver.3.22.0.5.nupkg"
            published = io.BytesIO(package.read_bytes())
            with zipfile.ZipFile(published, "a") as archive:
                archive.writestr(".signature.p7s", b"repository signature")

            present = release_gate.published_package_state(
                package,
                package_id="ScyllaDBCSharpDriver",
                version="3.22.0.5",
                recovery=True,
                opener=mock.Mock(return_value=self._Response(published.getvalue())),
            )
            self.assertTrue(present)

            mismatched = io.BytesIO(published.getvalue())
            with zipfile.ZipFile(mismatched, "a") as archive:
                archive.writestr("tampered.txt", b"different")
            with self.assertRaisesRegex(release_gate.ReleaseError, "does not match"):
                release_gate.published_package_state(
                    package,
                    package_id="ScyllaDBCSharpDriver",
                    version="3.22.0.5",
                    recovery=True,
                    opener=mock.Mock(
                        return_value=self._Response(mismatched.getvalue())
                    ),
                )

    def test_published_package_requires_recovery_and_allows_absent(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            package_directory = Path(temporary_directory)
            self._write_packages(package_directory, "3.22.0.5")
            package = package_directory / "ScyllaDBCSharpDriver.3.22.0.5.nupkg"
            response = self._Response(package.read_bytes())
            with self.assertRaisesRegex(release_gate.ReleaseError, "recovery mode"):
                release_gate.published_package_state(
                    package,
                    package_id="ScyllaDBCSharpDriver",
                    version="3.22.0.5",
                    recovery=False,
                    opener=mock.Mock(return_value=response),
                )

            missing = urllib.error.HTTPError(
                "https://api.nuget.org", 404, "missing", {}, io.BytesIO(b"{}")
            )
            self.assertFalse(
                release_gate.published_package_state(
                    package,
                    package_id="ScyllaDBCSharpDriver",
                    version="3.22.0.5",
                    recovery=True,
                    opener=mock.Mock(side_effect=missing),
                )
            )

    def test_published_package_classifies_only_transient_query_errors_for_retry(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            package_directory = Path(temporary_directory)
            self._write_packages(package_directory, "3.22.0.5")
            package = package_directory / "ScyllaDBCSharpDriver.3.22.0.5.nupkg"
            transient_errors = [
                urllib.error.HTTPError(
                    "https://api.nuget.org", status, "transient", {}, io.BytesIO()
                )
                for status in (408, 425, 429, 500, 599)
            ]
            transient_errors.append(urllib.error.URLError("connection reset"))

            for error in transient_errors:
                with self.subTest(error=error):
                    with self.assertRaises(
                        release_gate.TransientPackageQueryError
                    ):
                        release_gate.published_package_state(
                            package,
                            package_id="ScyllaDBCSharpDriver",
                            version="3.22.0.5",
                            recovery=True,
                            opener=mock.Mock(side_effect=error),
                        )

            permanent = urllib.error.HTTPError(
                "https://api.nuget.org", 403, "forbidden", {}, io.BytesIO()
            )
            with self.assertRaises(release_gate.ReleaseError) as raised:
                release_gate.published_package_state(
                    package,
                    package_id="ScyllaDBCSharpDriver",
                    version="3.22.0.5",
                    recovery=True,
                    opener=mock.Mock(side_effect=permanent),
                )
            self.assertNotIsInstance(
                raised.exception, release_gate.TransientPackageQueryError
            )

    def test_published_package_cli_converts_transient_query_errors_to_retry(self):
        arguments = [
            "published-package",
            "--package",
            "package.nupkg",
            "--package-id",
            "ScyllaDBCSharpDriver",
            "--version",
            "3.22.0.5",
            "--recovery",
            "--transient-errors-as-retry",
        ]
        for detail in ("HTTP 500", "connection reset"):
            with self.subTest(detail=detail):
                with mock.patch.object(
                    release_gate,
                    "published_package_state",
                    side_effect=release_gate.TransientPackageQueryError(detail),
                ), mock.patch("sys.stdout", new_callable=io.StringIO) as output:
                    release_gate.main(arguments)
                self.assertEqual("retry\n", output.getvalue())

    def test_published_package_cli_without_retry_option_fails_closed(self):
        arguments = [
            "published-package",
            "--package",
            "package.nupkg",
            "--package-id",
            "ScyllaDBCSharpDriver",
            "--version",
            "3.22.0.5",
            "--recovery",
        ]
        with mock.patch.object(
            release_gate,
            "published_package_state",
            side_effect=release_gate.TransientPackageQueryError("HTTP 500"),
        ):
            with self.assertRaises(release_gate.TransientPackageQueryError):
                release_gate.main(arguments)

    def test_published_package_cli_does_not_convert_permanent_failure(self):
        arguments = [
            "published-package",
            "--package",
            "package.nupkg",
            "--package-id",
            "ScyllaDBCSharpDriver",
            "--version",
            "3.22.0.5",
            "--recovery",
            "--transient-errors-as-retry",
        ]
        with mock.patch.object(
            release_gate,
            "published_package_state",
            side_effect=release_gate.ReleaseError("HTTP 403"),
        ):
            with self.assertRaisesRegex(release_gate.ReleaseError, "HTTP 403"):
                release_gate.main(arguments)

    def test_ensure_tag_creates_initial_and_reuses_only_explicit_recovery(self):
        api = FakeApi()
        release_gate.ensure_tag(
            api, version="3.22.0.5", target_commit=MAINTENANCE_SHA, recovery=False
        )
        self.assertEqual(MAINTENANCE_SHA, api.tags["v3.22.0.5"]["object"]["sha"])
        with self.assertRaisesRegex(release_gate.ReleaseError, "recovery mode"):
            release_gate.ensure_tag(
                api,
                version="3.22.0.5",
                target_commit=MAINTENANCE_SHA,
                recovery=False,
            )
        release_gate.ensure_tag(
            api, version="3.22.0.5", target_commit=MAINTENANCE_SHA, recovery=True
        )

    def test_ensure_tag_rejects_missing_recovery_and_mismatched_target(self):
        api = FakeApi()
        with self.assertRaisesRegex(release_gate.ReleaseError, "does not exist"):
            release_gate.ensure_tag(
                api,
                version="3.22.0.5",
                target_commit=MAINTENANCE_SHA,
                recovery=True,
            )
        api.tags["v3.22.0.5"] = {
            "object": {"type": "commit", "sha": "d" * 40}
        }
        with self.assertRaisesRegex(release_gate.ReleaseError, "different commit"):
            release_gate.ensure_tag(
                api,
                version="3.22.0.5",
                target_commit=MAINTENANCE_SHA,
                recovery=True,
            )

    def test_ensure_tag_handles_creation_race_only_for_exact_target(self):
        api = FakeApi()
        api.post_error = release_gate.GitHubApiError("POST", "/git/refs", 422, "exists")
        original_get = api.get
        calls = 0

        def raced_get(path, **kwargs):
            nonlocal calls
            calls += 1
            if calls == 1:
                return None
            api.tags["v3.22.0.5"] = {
                "object": {"type": "commit", "sha": MAINTENANCE_SHA}
            }
            return original_get(path, **kwargs)

        with mock.patch.object(api, "get", side_effect=raced_get):
            release_gate.ensure_tag(
                api,
                version="3.22.0.5",
                target_commit=MAINTENANCE_SHA,
                recovery=False,
            )

    def test_ensure_release_is_idempotent_only_in_recovery(self):
        api = FakeApi()
        api.tags["v3.22.0.5"] = {
            "object": {"type": "commit", "sha": MAINTENANCE_SHA}
        }
        release_gate.ensure_release(
            api,
            version="3.22.0.5",
            target_commit=MAINTENANCE_SHA,
            recovery=False,
        )
        self.assertIn("v3.22.0.5", api.releases)
        self.assertNotIn("target_commitish", api.posts[-1][1])
        with self.assertRaisesRegex(release_gate.ReleaseError, "recovery mode"):
            release_gate.ensure_release(
                api,
                version="3.22.0.5",
                target_commit=MAINTENANCE_SHA,
                recovery=False,
            )
        release_gate.ensure_release(
            api,
            version="3.22.0.5",
            target_commit=MAINTENANCE_SHA,
            recovery=True,
        )

    def test_existing_release_must_be_final(self):
        api = FakeApi()
        api.tags["v3.22.0.5"] = {
            "object": {"type": "commit", "sha": MAINTENANCE_SHA}
        }
        api.releases["v3.22.0.5"] = {
            "tag_name": "v3.22.0.5",
            "draft": True,
            "prerelease": False,
            "published_at": None,
        }
        with self.assertRaisesRegex(release_gate.ReleaseError, "still a draft"):
            release_gate.ensure_release(
                api,
                version="3.22.0.5",
                target_commit=MAINTENANCE_SHA,
                recovery=True,
            )

    @staticmethod
    def _write_projects(root: Path, version: str) -> None:
        for project in release_gate.PROJECTS:
            path = root / project.path
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(
                "<Project><PropertyGroup>"
                f"<PackageId>{project.package_id}</PackageId>"
                f"<Version>{version}</Version>"
                f"<FileVersion>{version}</FileVersion>"
                "</PropertyGroup></Project>",
                encoding="utf-8",
            )

    @staticmethod
    def _write_packages(directory: Path, version: str) -> None:
        directory.mkdir(parents=True, exist_ok=True)
        for project in release_gate.PROJECTS:
            package = directory / f"{project.package_id}.{version}.nupkg"
            nuspec = (
                "<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\">"
                "<metadata>"
                f"<id>{project.package_id}</id><version>{version}</version>"
                "</metadata></package>"
            )
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr(f"{project.package_id}.nuspec", nuspec)

    class _Response:
        def __init__(self, body: bytes):
            self._body = body

        def __enter__(self):
            return self

        def __exit__(self, exception_type, exception, traceback):
            return False

        def read(self):
            return self._body


class ReleaseSignatureTests(unittest.TestCase):
    def test_invalid_gpg_signature_fails_closed(self):
        with self.assertRaisesRegex(release_gate.ReleaseError, "GPG signature is invalid"):
            release_gate.signature_fingerprint("payload", "not a PGP signature")


if __name__ == "__main__":
    unittest.main()
