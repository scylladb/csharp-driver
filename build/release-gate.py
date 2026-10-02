#!/usr/bin/env python3

import argparse
import io
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ElementTree
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any


API_VERSION = "2022-11-28"
RELEASE_TAG_RULESET_PATTERN = "refs/tags/v*.*.*.*"
VERSION_PATTERN = re.compile(
    r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\."
    r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"
)
SHA_PATTERN = re.compile(r"^[0-9a-f]{40}$")


class ReleaseError(RuntimeError):
    pass


class TransientPackageQueryError(ReleaseError):
    pass


class GitHubApiError(ReleaseError):
    def __init__(self, method: str, path: str, status: int | None, detail: str):
        status_text = str(status) if status is not None else "transport error"
        super().__init__(f"GitHub API {method} {path} failed ({status_text}): {detail}")
        self.status = status


@dataclass(frozen=True)
class ProjectContract:
    path: str
    package_id: str


@dataclass(frozen=True)
class ReleaseContext:
    version: str
    branch: str
    tag: str
    milestone_number: int
    target_commit: str


PROJECTS = (
    ProjectContract("src/Cassandra/Cassandra.csproj", "ScyllaDBCSharpDriver"),
    ProjectContract(
        "src/Extensions/Cassandra.AppMetrics/Cassandra.AppMetrics.csproj",
        "ScyllaDBCSharpDriver.AppMetrics",
    ),
    ProjectContract(
        "src/Extensions/Cassandra.OpenTelemetry/Cassandra.OpenTelemetry.csproj",
        "ScyllaDBCSharpDriver.OpenTelemetry",
    ),
)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ReleaseError(message)


def require_string(value: Any, description: str) -> str:
    require(isinstance(value, str) and value, f"Missing or invalid {description}")
    return value


def require_integer(value: Any, description: str) -> int:
    require(isinstance(value, int), f"Missing or invalid {description}")
    return value


class GitHubApi:
    def __init__(
        self,
        repository: str,
        token: str,
        *,
        api_url: str = "https://api.github.com",
        opener=urllib.request.urlopen,
    ) -> None:
        repository_parts = repository.split("/")
        require(
            len(repository_parts) == 2 and all(repository_parts),
            f"Invalid repository name {repository!r}; expected OWNER/REPO",
        )
        require(token, "GITHUB_TOKEN is required")
        encoded_repository = "/".join(
            urllib.parse.quote(part, safe="") for part in repository_parts
        )
        self._base_url = f"{api_url.rstrip('/')}/repos/{encoded_repository}"
        self._token = token
        self._opener = opener

    def _request(
        self,
        method: str,
        path: str,
        *,
        query: dict[str, str | int] | None = None,
        payload: dict[str, Any] | None = None,
        missing_ok: bool = False,
    ) -> Any:
        require(path.startswith("/"), f"GitHub API path must start with '/': {path}")
        url = f"{self._base_url}{path}"
        if query:
            url = f"{url}?{urllib.parse.urlencode(query)}"
        body = None
        if payload is not None:
            body = json.dumps(payload).encode("utf-8")
        request = urllib.request.Request(
            url,
            data=body,
            method=method,
            headers={
                "Accept": "application/vnd.github+json",
                "Authorization": f"Bearer {self._token}",
                "Content-Type": "application/json",
                "User-Agent": "scylladb-csharp-driver-release-gate",
                "X-GitHub-Api-Version": API_VERSION,
            },
        )
        try:
            with self._opener(request) as response:
                response_body = response.read()
        except urllib.error.HTTPError as error:
            if missing_ok and error.code == 404:
                return None
            detail = error.reason or "HTTP error"
            try:
                error_payload = json.loads(error.read().decode("utf-8"))
                if isinstance(error_payload, dict) and error_payload.get("message"):
                    detail = str(error_payload["message"])
            except (UnicodeDecodeError, json.JSONDecodeError):
                # Error-body parsing is best effort; retain the HTTP fallback detail.
                pass
            raise GitHubApiError(method, path, error.code, str(detail)) from error
        except urllib.error.URLError as error:
            raise GitHubApiError(method, path, None, str(error.reason)) from error

        try:
            return json.loads(response_body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise GitHubApiError(method, path, None, "response was not valid JSON") from error

    def get(
        self,
        path: str,
        *,
        query: dict[str, str | int] | None = None,
        missing_ok: bool = False,
    ) -> Any:
        return self._request("GET", path, query=query, missing_ok=missing_ok)

    def post(self, path: str, payload: dict[str, Any]) -> Any:
        return self._request("POST", path, payload=payload)

    def paginate(
        self, path: str, *, query: dict[str, str | int] | None = None
    ) -> list[Any]:
        result: list[Any] = []
        page = 1
        while True:
            page_query = dict(query or {})
            page_query.update({"page": page, "per_page": 100})
            payload = self.get(path, query=page_query)
            require(isinstance(payload, list), f"GitHub API {path} did not return a list")
            result.extend(payload)
            if len(payload) < 100:
                return result
            page += 1
            require(page <= 1000, f"GitHub API pagination did not terminate for {path}")

    def paginate_key(
        self,
        path: str,
        key: str,
        *,
        query: dict[str, str | int] | None = None,
    ) -> list[Any]:
        result: list[Any] = []
        page = 1
        while True:
            page_query = dict(query or {})
            page_query.update({"page": page, "per_page": 100})
            payload = self.get(path, query=page_query)
            require(isinstance(payload, dict), f"GitHub API {path} did not return an object")
            entries = payload.get(key)
            require(isinstance(entries, list), f"GitHub API {path} omitted {key!r}")
            result.extend(entries)
            if len(entries) < 100:
                return result
            page += 1
            require(page <= 1000, f"GitHub API pagination did not terminate for {path}")


def parse_version(version: str) -> tuple[int, int, int, int]:
    match = VERSION_PATTERN.fullmatch(version)
    require(match is not None, "Version must contain exactly four numeric parts")
    return tuple(int(part) for part in match.groups())  # type: ignore[return-value]


def normalized_package_version(version: str) -> str:
    parts = list(parse_version(version))
    while len(parts) > 3 and parts[-1] == 0:
        parts.pop()
    return ".".join(str(part) for part in parts)


def branch_for_version(version: str) -> str:
    major, minor, _, _ = parse_version(version)
    if (major, minor) == (3, 22):
        return "3.22"
    if major == 4:
        return "master"
    raise ReleaseError(f"Version {version} does not belong to a releasable branch")


def validate_commit(commit: str, description: str = "target commit") -> str:
    require(
        SHA_PATTERN.fullmatch(commit) is not None,
        f"{description.capitalize()} must be a full lowercase 40-character SHA",
    )
    return commit


def branch_tip(api: Any, branch: str) -> str:
    encoded_branch = urllib.parse.quote(branch, safe="")
    payload = api.get(f"/git/ref/heads/{encoded_branch}")
    require(isinstance(payload, dict), f"Branch {branch} response was not an object")
    ref_object = payload.get("object")
    require(isinstance(ref_object, dict), f"Branch {branch} response omitted object")
    require(ref_object.get("type") == "commit", f"Branch {branch} did not resolve to a commit")
    return validate_commit(
        require_string(ref_object.get("sha"), f"SHA for branch {branch}"),
        f"SHA for branch {branch}",
    )


def tag_target(api: Any, tag: str, *, missing_ok: bool = False) -> str | None:
    encoded_tag = urllib.parse.quote(tag, safe="")
    payload = api.get(f"/git/ref/tags/{encoded_tag}", missing_ok=missing_ok)
    if payload is None:
        return None
    require(isinstance(payload, dict), f"Tag {tag} response was not an object")
    ref_object = payload.get("object")
    require(isinstance(ref_object, dict), f"Tag {tag} response omitted object")
    require(ref_object.get("type") == "commit", f"Tag {tag} is not lightweight")
    return validate_commit(
        require_string(ref_object.get("sha"), f"SHA for tag {tag}"),
        f"SHA for tag {tag}",
    )


def require_release_target(
    api: Any,
    *,
    branch: str,
    tag: str,
    target_commit: str,
    recovery: bool,
) -> None:
    current_tip = branch_tip(api, branch)
    if not recovery:
        require(
            current_tip == target_commit,
            f"Target commit must be the current protected {branch} tip",
        )
        return
    require(
        tag_target(api, tag, missing_ok=True) == target_commit,
        f"Recovery tag {tag} must be lightweight and point at the target commit",
    )
    if current_tip == target_commit:
        return
    encoded_range = urllib.parse.quote(
        f"{target_commit}...{current_tip}", safe="."
    )
    comparison = api.get(f"/compare/{encoded_range}")
    require(isinstance(comparison, dict), "Commit comparison response was not an object")
    merge_base = comparison.get("merge_base_commit")
    require(isinstance(merge_base, dict), "Commit comparison omitted merge base")
    require(
        merge_base.get("sha") == target_commit,
        f"Recovery target is not an ancestor of the protected {branch} tip",
    )


def resolve_milestone(api: Any, title: str) -> int:
    milestones = api.paginate("/milestones", query={"state": "all"})
    matches = [
        milestone
        for milestone in milestones
        if isinstance(milestone, dict) and milestone.get("title") == title
    ]
    require(len(matches) == 1, f"Expected exactly one milestone named {title}, found {len(matches)}")
    milestone = matches[0]
    require(milestone.get("state") == "open", f"Milestone {title} is not open")
    return require_integer(milestone.get("number"), f"number for milestone {title}")


def release_blockers(api: Any, milestone_number: int) -> list[dict[str, Any]]:
    entries = api.paginate(
        "/issues",
        query={
            "state": "open",
            "labels": "release-blocker",
            "milestone": milestone_number,
        },
    )
    blockers: list[dict[str, Any]] = []
    for entry in entries:
        require(isinstance(entry, dict), "Release blocker response contained a non-object")
        number = require_integer(entry.get("number"), "release blocker number")
        title = require_string(entry.get("title"), f"title for release blocker #{number}")
        url = require_string(entry.get("html_url"), f"URL for release blocker #{number}")
        blockers.append(
            {
                "number": number,
                "title": title,
                "url": url,
                "pull_request": "pull_request" in entry,
            }
        )
    return blockers


def report_blockers(blockers: list[dict[str, Any]]) -> None:
    for blocker in blockers:
        kind = "pull request" if blocker["pull_request"] else "issue"
        print(
            f"Open release-blocker {kind} #{blocker['number']}: "
            f"{blocker['title']} ({blocker['url']})",
            file=sys.stderr,
        )


def require_successful_ci(api: Any, branch: str, target_commit: str) -> None:
    runs = api.paginate_key(
        "/actions/workflows/main.yml/runs",
        "workflow_runs",
        query={"head_sha": target_commit, "status": "success"},
    )
    matching_runs = [
        run
        for run in runs
        if isinstance(run, dict)
        and run.get("head_sha") == target_commit
        and run.get("head_branch") == branch
        and run.get("event") == "push"
        and run.get("conclusion") == "success"
    ]
    require(
        matching_runs,
        f"No successful push CI run found for {branch} at {target_commit}",
    )


def require_release_tag_ruleset(api: Any) -> dict[str, Any]:
    rulesets = api.paginate("/rulesets", query={"includes_parents": "true"})
    candidates = [
        ruleset
        for ruleset in rulesets
        if isinstance(ruleset, dict)
        and ruleset.get("target") == "tag"
        and ruleset.get("enforcement") == "active"
    ]
    protected = []
    for candidate in candidates:
        ruleset_id = require_integer(candidate.get("id"), "release tag ruleset ID")
        detail = api.get(f"/rulesets/{ruleset_id}")
        require(isinstance(detail, dict), f"Ruleset {ruleset_id} response was not an object")
        conditions = detail.get("conditions")
        ref_name = conditions.get("ref_name") if isinstance(conditions, dict) else None
        includes = ref_name.get("include") if isinstance(ref_name, dict) else None
        excludes = ref_name.get("exclude") if isinstance(ref_name, dict) else None
        rules = detail.get("rules")
        rule_types = {
            rule.get("type")
            for rule in rules
            if isinstance(rule, dict) and isinstance(rule.get("type"), str)
        } if isinstance(rules, list) else set()
        if (
            isinstance(includes, list)
            and RELEASE_TAG_RULESET_PATTERN in includes
            and excludes == []
            and {"creation", "update", "deletion"}.issubset(rule_types)
        ):
            protected.append(detail)
    require(
        len(protected) == 1,
        "Expected exactly one active create/update/delete ruleset for "
        f"{RELEASE_TAG_RULESET_PATTERN}, found {len(protected)}",
    )
    return protected[0]


def audit_release_tag_ruleset_bypass(api: Any, *, release_app_id: int) -> None:
    require(release_app_id > 0, "Release App ID must be positive")
    detail = require_release_tag_ruleset(api)
    expected_bypass = [
        {
            "actor_id": release_app_id,
            "actor_type": "Integration",
            "bypass_mode": "always",
        }
    ]
    require(
        detail.get("bypass_actors") == expected_bypass,
        "Release tag ruleset must allow only the designated release App to bypass",
    )


def preflight(
    api: Any,
    *,
    version: str,
    target_commit: str,
    workflow_ref: str,
    workflow_sha: str,
    allow_blockers: bool,
    recovery: bool,
) -> ReleaseContext:
    branch = branch_for_version(version)
    target_commit = validate_commit(target_commit)
    workflow_sha = validate_commit(workflow_sha, "workflow SHA")
    require(workflow_ref == "refs/heads/master", "Release workflow must run from master")
    require(
        branch_tip(api, "master") == workflow_sha,
        "Release workflow must run from the current protected master tip",
    )
    require(
        not (allow_blockers and recovery),
        "Dry run cannot resume a partial publication",
    )
    tag = f"v{version}"
    require_release_target(
        api,
        branch=branch,
        tag=tag,
        target_commit=target_commit,
        recovery=recovery,
    )
    milestone_number = resolve_milestone(api, tag)
    blockers = release_blockers(api, milestone_number)
    if blockers:
        report_blockers(blockers)
        require(allow_blockers, f"Milestone {tag} has {len(blockers)} open release blocker(s)")
    require_successful_ci(api, branch, target_commit)
    if not allow_blockers:
        require_release_tag_ruleset(api)
    return ReleaseContext(version, branch, tag, milestone_number, target_commit)


def gate(
    api: Any,
    *,
    version: str,
    target_commit: str,
    recovery: bool,
) -> ReleaseContext:
    branch = branch_for_version(version)
    target_commit = validate_commit(target_commit)
    tag = f"v{version}"
    require_release_target(
        api,
        branch=branch,
        tag=tag,
        target_commit=target_commit,
        recovery=recovery,
    )
    milestone_number = resolve_milestone(api, tag)
    blockers = release_blockers(api, milestone_number)
    if blockers:
        report_blockers(blockers)
    require(not blockers, f"Milestone {tag} has {len(blockers)} open release blocker(s)")
    require_release_tag_ruleset(api)
    return ReleaseContext(version, branch, tag, milestone_number, target_commit)


def project_value(project: Path, name: str) -> str:
    try:
        root = ElementTree.parse(project).getroot()
    except (ElementTree.ParseError, OSError) as error:
        raise ReleaseError(f"Could not parse {project}: {error}") from error
    element = root.find(f".//{name}")
    require(element is not None and element.text, f"Missing {name} in {project}")
    return element.text.strip()


def verify_source(
    source: Path, *, version: str, branch: str, target_commit: str
) -> None:
    expected_branch = branch_for_version(version)
    require(branch == expected_branch, f"Version {version} must use branch {expected_branch}")
    target_commit = validate_commit(target_commit)
    result = subprocess.run(
        ["git", "-C", str(source), "rev-parse", "HEAD"],
        check=False,
        capture_output=True,
        text=True,
    )
    require(result.returncode == 0, f"Could not resolve HEAD in {source}")
    require(result.stdout.strip() == target_commit, "Source checkout does not match target commit")
    for contract in PROJECTS:
        project = source / contract.path
        require(project_value(project, "PackageId") == contract.package_id, f"Unexpected PackageId in {project}")
        require(project_value(project, "Version") == version, f"Unexpected Version in {project}")
        require(project_value(project, "FileVersion") == version, f"Unexpected FileVersion in {project}")
    publish_workflow = source / ".github/workflows/publish.yml"
    if branch == "3.22":
        require(
            not publish_workflow.exists(),
            "3.22 still contains the legacy tag-triggered publish workflow",
        )
    else:
        require(publish_workflow.is_file(), "Master release workflow is missing")
        workflow_text = publish_workflow.read_text(encoding="utf-8")
        require(
            re.search(r"(?m)^\s{2}push:\s*$", workflow_text) is None,
            "Master release workflow still has a push trigger",
        )


def xml_child_text(parent: ElementTree.Element, name: str) -> str:
    child = parent.find(f"{{*}}{name}")
    require(child is not None and child.text, f"Package manifest omitted {name}")
    return child.text.strip()


def verify_packages(package_directory: Path, version: str) -> dict[str, Path]:
    require(package_directory.is_dir(), f"Package directory does not exist: {package_directory}")
    expected_version = normalized_package_version(version)
    packages = sorted(package_directory.glob("*.nupkg"))
    require(len(packages) == len(PROJECTS), f"Expected three packages, found {len(packages)}")
    expected_ids = {project.package_id for project in PROJECTS}
    resolved: dict[str, Path] = {}
    for package in packages:
        try:
            with zipfile.ZipFile(package) as archive:
                manifests = [name for name in archive.namelist() if name.endswith(".nuspec")]
                require(len(manifests) == 1, f"Expected one nuspec in {package.name}")
                manifest = ElementTree.fromstring(archive.read(manifests[0]))
        except (OSError, zipfile.BadZipFile, ElementTree.ParseError) as error:
            raise ReleaseError(f"Could not inspect {package}: {error}") from error
        metadata = manifest.find("{*}metadata")
        require(metadata is not None, f"Package manifest omitted metadata in {package.name}")
        package_id = xml_child_text(metadata, "id")
        package_version = xml_child_text(metadata, "version")
        require(package_id in expected_ids, f"Unexpected package ID {package_id!r}")
        require(package_id not in resolved, f"Duplicate package ID {package_id}")
        require(package_version == expected_version, f"Unexpected version for {package_id}: {package_version}")
        require(
            package.name == f"{package_id}.{expected_version}.nupkg",
            f"Unexpected package filename {package.name}",
        )
        resolved[package_id] = package.resolve()
    require(set(resolved) == expected_ids, "Package set did not match all three release projects")
    return resolved


def comparable_package_entries(package: Path | io.BytesIO) -> dict[str, bytes]:
    ignored_files = {".signature.p7s", "[Content_Types].xml", "_rels/.rels"}
    ignored_prefix = "package/services/metadata/core-properties/"
    try:
        with zipfile.ZipFile(package) as archive:
            names = [name for name in archive.namelist() if not name.endswith("/")]
            require(len(names) == len(set(names)), "Package contains duplicate archive entries")
            return {
                name: archive.read(name)
                for name in names
                if name not in ignored_files and not name.startswith(ignored_prefix)
            }
    except (OSError, zipfile.BadZipFile) as error:
        raise ReleaseError(f"Could not compare package archive: {error}") from error


def published_package_state(
    package: Path,
    *,
    package_id: str,
    version: str,
    recovery: bool,
    opener=urllib.request.urlopen,
) -> bool:
    require(package.is_file(), f"Package does not exist: {package}")
    expected_ids = {project.package_id for project in PROJECTS}
    require(package_id in expected_ids, f"Unexpected package ID {package_id!r}")
    normalized_version = normalized_package_version(version)
    lower_id = package_id.lower()
    url = (
        "https://api.nuget.org/v3-flatcontainer/"
        f"{urllib.parse.quote(lower_id, safe='')}/"
        f"{urllib.parse.quote(normalized_version, safe='')}/"
        f"{urllib.parse.quote(lower_id, safe='')}."
        f"{urllib.parse.quote(normalized_version, safe='')}.nupkg"
    )
    request = urllib.request.Request(
        url,
        headers={"User-Agent": "scylladb-csharp-driver-release-gate"},
    )
    try:
        with opener(request) as response:
            published_bytes = response.read()
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return False
        error_type = (
            TransientPackageQueryError
            if error.code in (408, 425, 429) or 500 <= error.code <= 599
            else ReleaseError
        )
        raise error_type(
            f"Could not query published package {package_id} {normalized_version}: "
            f"HTTP {error.code}"
        ) from error
    except urllib.error.URLError as error:
        raise TransientPackageQueryError(
            f"Could not query published package {package_id} {normalized_version}: "
            f"{error.reason}"
        ) from error

    require(
        recovery,
        f"{package_id} {normalized_version} is already published; use explicit recovery mode",
    )
    local_entries = comparable_package_entries(package)
    published_entries = comparable_package_entries(io.BytesIO(published_bytes))
    require(
        local_entries == published_entries,
        f"Published {package_id} {normalized_version} does not match the rebuilt artifact",
    )
    return True


def ensure_tag(api: Any, *, version: str, target_commit: str, recovery: bool) -> None:
    parse_version(version)
    target_commit = validate_commit(target_commit)
    tag = f"v{version}"
    encoded_tag = urllib.parse.quote(tag, safe="")
    path = f"/git/ref/tags/{encoded_tag}"
    existing = api.get(path, missing_ok=True)
    creation_race = False
    if existing is None:
        require(not recovery, f"Recovery requested but tag {tag} does not exist")
        try:
            api.post("/git/refs", {"ref": f"refs/tags/{tag}", "sha": target_commit})
            print(f"Created lightweight tag {tag} at {target_commit}")
            return
        except GitHubApiError as error:
            if error.status != 422:
                raise
            existing = api.get(path, missing_ok=True)
            require(existing is not None, f"Tag {tag} creation raced but the tag is still absent")
            creation_race = True
    require(isinstance(existing, dict), f"Tag {tag} response was not an object")
    ref_object = existing.get("object")
    require(isinstance(ref_object, dict), f"Tag {tag} response omitted object")
    require(ref_object.get("type") == "commit", f"Tag {tag} is not lightweight")
    require(ref_object.get("sha") == target_commit, f"Tag {tag} points at a different commit")
    if creation_race:
        print(f"A concurrent run created lightweight tag {tag} at {target_commit}")
        return
    require(recovery, f"Tag {tag} already exists; use explicit recovery mode")
    print(f"Reusing lightweight tag {tag} at {target_commit}")


def ensure_release(
    api: Any, *, version: str, target_commit: str, recovery: bool
) -> None:
    parse_version(version)
    target_commit = validate_commit(target_commit)
    tag = f"v{version}"
    require(
        tag_target(api, tag, missing_ok=True) == target_commit,
        f"Tag {tag} must be lightweight and point at the target commit",
    )
    encoded_tag = urllib.parse.quote(tag, safe="")
    existing = api.get(f"/releases/tags/{encoded_tag}", missing_ok=True)
    if existing is not None:
        require(isinstance(existing, dict), f"Release {tag} response was not an object")
        require(existing.get("tag_name") == tag, f"Release lookup returned the wrong tag for {tag}")
        require(existing.get("draft") is False, f"GitHub Release {tag} is still a draft")
        require(existing.get("prerelease") is False, f"GitHub Release {tag} is marked prerelease")
        require_string(existing.get("published_at"), f"publication time for {tag}")
        require(recovery, f"GitHub Release {tag} already exists; use explicit recovery mode")
        print(f"GitHub Release {tag} already exists")
        return
    created = api.post(
        "/releases",
        {
            "tag_name": tag,
            "target_commitish": target_commit,
            "name": tag,
            "draft": False,
            "prerelease": False,
            "generate_release_notes": True,
        },
    )
    require(isinstance(created, dict), f"Created release {tag} response was not an object")
    require(created.get("tag_name") == tag, f"Created release returned the wrong tag for {tag}")
    require(created.get("draft") is False, f"Created GitHub Release {tag} is a draft")
    require(created.get("prerelease") is False, f"Created GitHub Release {tag} is a prerelease")
    require_string(created.get("published_at"), f"publication time for {tag}")
    print(f"Created GitHub Release {tag}")


def write_github_outputs(path: str | None, values: dict[str, str]) -> None:
    if not path:
        return
    with Path(path).open("a", encoding="utf-8") as output:
        for name, value in values.items():
            require("\n" not in value and "\r" not in value, f"Invalid output value for {name}")
            output.write(f"{name}={value}\n")


def github_api(repository: str) -> GitHubApi:
    token = os.environ.get("GITHUB_TOKEN", "")
    api_url = os.environ.get("GITHUB_API_URL", "https://api.github.com")
    return GitHubApi(repository, token, api_url=api_url)


def add_common_release_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--repository", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--target-commit", required=True)


def parse_arguments(arguments: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Gate and orchestrate driver releases")
    subparsers = parser.add_subparsers(dest="command", required=True)

    preflight_parser = subparsers.add_parser("preflight")
    add_common_release_arguments(preflight_parser)
    preflight_parser.add_argument("--workflow-ref", required=True)
    preflight_parser.add_argument("--workflow-sha", required=True)
    preflight_parser.add_argument("--allow-blockers", action="store_true")
    preflight_parser.add_argument("--recovery", action="store_true")
    preflight_parser.add_argument("--github-output")

    gate_parser = subparsers.add_parser("gate")
    add_common_release_arguments(gate_parser)
    gate_parser.add_argument("--recovery", action="store_true")

    source_parser = subparsers.add_parser("verify-source")
    source_parser.add_argument("--source", type=Path, required=True)
    source_parser.add_argument("--version", required=True)
    source_parser.add_argument("--branch", required=True)
    source_parser.add_argument("--target-commit", required=True)

    package_parser = subparsers.add_parser("verify-packages")
    package_parser.add_argument("--package-directory", type=Path, required=True)
    package_parser.add_argument("--version", required=True)
    package_parser.add_argument("--github-output")

    published_parser = subparsers.add_parser("published-package")
    published_parser.add_argument("--package", type=Path, required=True)
    published_parser.add_argument("--package-id", required=True)
    published_parser.add_argument("--version", required=True)
    published_parser.add_argument("--recovery", action="store_true")
    published_parser.add_argument("--transient-errors-as-retry", action="store_true")

    tag_parser = subparsers.add_parser("ensure-tag")
    add_common_release_arguments(tag_parser)
    tag_parser.add_argument("--recovery", action="store_true")

    release_parser = subparsers.add_parser("ensure-release")
    release_parser.add_argument("--repository", required=True)
    release_parser.add_argument("--version", required=True)
    release_parser.add_argument("--target-commit", required=True)
    release_parser.add_argument("--recovery", action="store_true")

    audit_parser = subparsers.add_parser("audit-ruleset")
    audit_parser.add_argument("--repository", required=True)
    audit_parser.add_argument("--release-app-id", type=int, required=True)

    return parser.parse_args(arguments)


def main(arguments: list[str] | None = None) -> None:
    options = parse_arguments(arguments)
    if options.command == "preflight":
        context = preflight(
            github_api(options.repository),
            version=options.version,
            target_commit=options.target_commit,
            workflow_ref=options.workflow_ref,
            workflow_sha=options.workflow_sha,
            allow_blockers=options.allow_blockers,
            recovery=options.recovery,
        )
        write_github_outputs(
            options.github_output,
            {
                "branch": context.branch,
                "tag": context.tag,
                "milestone_number": str(context.milestone_number),
            },
        )
    elif options.command == "gate":
        gate(
            github_api(options.repository),
            version=options.version,
            target_commit=options.target_commit,
            recovery=options.recovery,
        )
    elif options.command == "verify-source":
        verify_source(
            options.source,
            version=options.version,
            branch=options.branch,
            target_commit=options.target_commit,
        )
    elif options.command == "verify-packages":
        packages = verify_packages(options.package_directory, options.version)
        write_github_outputs(
            options.github_output,
            {
                "core": str(packages["ScyllaDBCSharpDriver"]),
                "appmetrics": str(packages["ScyllaDBCSharpDriver.AppMetrics"]),
                "opentelemetry": str(packages["ScyllaDBCSharpDriver.OpenTelemetry"]),
            },
        )
    elif options.command == "published-package":
        try:
            published = published_package_state(
                options.package,
                package_id=options.package_id,
                version=options.version,
                recovery=options.recovery,
            )
        except TransientPackageQueryError:
            if not options.transient_errors_as_retry:
                raise
            print("retry")
        else:
            print("present" if published else "absent")
    elif options.command == "ensure-tag":
        ensure_tag(
            github_api(options.repository),
            version=options.version,
            target_commit=options.target_commit,
            recovery=options.recovery,
        )
    elif options.command == "ensure-release":
        ensure_release(
            github_api(options.repository),
            version=options.version,
            target_commit=options.target_commit,
            recovery=options.recovery,
        )
    elif options.command == "audit-ruleset":
        audit_release_tag_ruleset_bypass(
            github_api(options.repository),
            release_app_id=options.release_app_id,
        )
    else:
        raise ReleaseError(f"Unknown command {options.command}")


if __name__ == "__main__":
    try:
        main()
    except ReleaseError as error:
        print(f"release gate: {error}", file=sys.stderr)
        raise SystemExit(1)
