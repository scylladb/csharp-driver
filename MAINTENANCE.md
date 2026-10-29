# Maintenance and release policy

The ScyllaDB C# Driver has separate branches for the maintained version 3 line
and version 4 development.

| Line | Status | Branch | Package target | Validated runtime |
|---|---|---|---|---|
| 3.22.x | Maintained; security and correctness fixes only | [`branch-3.22`](https://github.com/scylladb/csharp-driver/tree/branch-3.22) | `netstandard2.0` | .NET 6–.NET 9 |
| 4.x | Active development; not yet released | [`master`](https://github.com/scylladb/csharp-driver/tree/master) | `net10.0` | .NET 10 |

The 3.22.x line remains supported until the project announces an end-of-life
date in advance. Version 4 does not replace that compatibility line before the
announced date takes effect.

## 3.22 change policy

The `branch-3.22` branch accepts fixes for security vulnerabilities and incorrect
behavior, including regressions, data loss, resource leaks, and reliability
defects. It also accepts the tests and documentation needed to demonstrate
those fixes and the minimum compatible dependency update they require.

It does not accept features, new public APIs, opportunistic dependency updates,
broad refactoring, or unrelated modernization. A 3.22.x maintenance release
must not remove or break an existing public API.

## Backports

1. File or identify an issue that describes the security or correctness
   problem and its acceptance criteria.
2. Fix and review the problem on `master` first.
3. Cherry-pick only the focused fix and its tests with `git cherry-pick -x` to
   a pull request targeting `branch-3.22`.
4. If affected code exists only on `branch-3.22`, link an issue that explains
   why a `master` change is not applicable.
5. Resolve conflicts in the backport. Never merge post-cut `master` into
   `branch-3.22`.

## Releases

Each release is tracked by a version milestone. A release is eligible to
publish only when its selected milestone has no open `release-blocker` issue or
pull request. Before tagging, maintainers run the line's full test matrix,
audit dependencies, validate all three NuGet artifacts and signing, check API
compatibility, and complete a production-key dry run.

All three packages receive the same four-part version and are released
together:

- `ScyllaDBCSharpDriver`
- `ScyllaDBCSharpDriver.AppMetrics`
- `ScyllaDBCSharpDriver.OpenTelemetry`

Release tags prefix that package version with `v`, for example `v3.22.0.5` or
`v4.0.0.0`. After publication, maintainers verify the NuGet packages and GitHub
Release, then verify that the release workflow published the versioned docs and
kept the `stable` pointer on the highest released version. A failed docs
deployment can be retried with
**Docs / Publish** after the release succeeds.

GitHub generates the Release notes from merged pull requests and excludes
PRs carrying `omit-from-release-notes`, as configured in `.github/release.yml`.
The pinned `actions/labeler` workflow checks reported changed filenames against
`.github/labeler.yml` when PRs are opened, synchronized, reopened, edited, or
closed. Every filename must match the single union of support paths; mixed
documentation, test and CI changes can qualify together. The action uses
`sync-labels: true` to remove the label when the filename rule stops matching.
Runs serialize by PR number, including manual backfills, without checking out
or executing PR contents.

Only documentation, proposals, tests, known CI workflows, and explicitly listed
repository housekeeping paths qualify automatically. This includes the listed
README files and the root `.nav` and `doxyfile` documentation configuration.
Driver or extension code, SDK/runtime manifests, packaging/signing scripts, the
NuGet publishing workflow, and unknown paths remain visible.
`build/`, `tools/`, examples, Makefiles, and Markdown files have no blanket
exemption; only listed tests, report transforms and documentation qualify there.
Labeler is the sole filename classifier. A precheck confirms the target is a
pull request and requires a complete, nonempty changed-file listing. Every PR
containing a rename stays visible, including documentation-only renames, so
the workflow does not duplicate the filename policy to check rename origins.
Failed or skipped labeling attempts removal of the omission label for a
confirmed PR. A failed precheck or labeler run remains failed even if cleanup
succeeds; an intentional rename skip succeeds if cleanup succeeds. A successful
labeler step without the `all-labels` output also fails classification and
attempts cleanup; an empty but present output is valid. Ordinary
issue numbers and unconfirmed manual targets receive no label writes. The
workflow needs no checkout or installed dependencies. Cleanup remains best
effort during API outages. File and label reads are not atomic: the precheck
and labeler can observe different revisions, and the workflow does not verify
revisions or labels after classification.

Maintainers can label an already merged PR with **Actions → Label support-only
release changes → Run workflow**, using its PR number in `pr_number`, before
publishing a release. The run reapplies the filename rule. To override its
decision, edit the PR label directly after the last labeler run. Later PR edits
or classifier reruns can replace manual overrides. Recheck labels and reapply
overrides after such a run before publication. Authors, titles, `chore`, and
`dependencies` labels do not suppress release notes.

Before creating a GitHub Release, identify the merged PRs in its release range.
Run the classifier for each, wait for successful runs, and inspect every
`omit-from-release-notes` label against the current changed files. Confirm that
PRs containing renames have no omission label. Fix failed runs or unexpected
skips before publication and apply manual label overrides only after the final
successful run. Keep the PRs and release range stable or repeat this verification
when either changes.
Release publication remains asynchronous and has no automated classification
barrier. If manual verification is missed, published notes may retain omissions
or unwanted support entries. This procedure does not rewrite existing published
notes.

The local Jira caller ignores only add/remove events for the logical whole
`omit-from-release-notes` marker, including case variants through
[GitHub's case-insensitive string comparison](https://docs.github.com/en/actions/reference/workflows-and-actions/expressions#operators).
Other Jira events retain their behavior. Existing Jira copies are not cleaned, and other sync
paths may restore copied metadata. Automatic labels written with the default
`GITHUB_TOKEN` do not trigger these label-event workflows.

### Intended public API breaks

`build/validate-packages.py` compares each package's public API with 3.22.0.4
and fails on any difference that isn't declared in a `CompatibilitySuppressions.xml`
next to the package's project. Only a major release may break the public API.
A pull request that does so regenerates those files and commits them with the
change:

```bash
python3 build/validate-packages.py --update-api-suppressions
```

The command records every current difference, so review the diff of the
regenerated files: it should show exactly the intended breaks. The PR also adds
an upgrade-guide entry for each break. A suppression that no longer matches a
difference fails validation, so the files stay accurate.

### Canonical release workflow

Run **Release NuGet packages** from the protected branch being released:
`branch-3.22` for version 3.22.x, or `master` for version 4. The manual dispatch
names the four-part version and the full SHA of the current branch tip. For
production, the workflow checks source versions, the milestone and blockers,
successful CI, and the absence of an existing release tag or GitHub Release.
It updates `build/release-version.txt` and pushes a `Release v<version>` commit
signed with the ScyllaDB Publisher GPG key `BF4BF97A8D4DF1AA`. The push runs
CI; the same workflow waits for successful CI on that exact signed SHA before
building docs and packages. A new dispatch after the signed commit was pushed
accepts that commit as the branch tip without creating another one.

After docs, integration and package validation, the workflow rechecks the
milestone and commit signature. It creates a signed annotated tag on the
validated commit, verifies the tag's publisher signature and target, and then
publishes all three packages and the GitHub Release. It uses release tooling
checked out from protected `master` and retains package hashes as an artifact.

The release workflow also runs the C# driver matrix against the exact target
commit and intended package version before publication. It checks the DataStax
driver against Scylla LATEST and the Scylla driver against LATEST, PRIOR,
LTS-LATEST, and LTS-PRIOR. The matrix repository must contain the candidate version's patch and ignore configuration before dispatch. A failing lane blocks
publication; the matrix also runs during dry runs. The exact release source
must also pass the API documentation and Sphinx build before publication.
The workflow also builds the versioned Pages artifact with the candidate
release tag before publication, then deploys that checked artifact after the
GitHub Release succeeds. Dry runs never deploy docs.

Package validation reads dependency and assembly versions from the evaluated
Release projects. The release workflow supplies the expected package version
through `RELEASE_VERSION`; local runs can use `--version` or default to the
project version. Updating a dependency or release version does not require
updating a second version list in the validator.

A dry run validates the input source commit, docs and packages with the
production strong-name key, but creates no remote commit, tag, NuGet package or
GitHub Release. Open milestone blockers are reported but do not prevent a dry
run. Production fails before creating the signed commit while any selected-milestone
`release-blocker` remains, and rechecks the gate before the tag and before each
individual package upload.

Production requires a `release` environment restricted to `master` and
`branch-3.22`, with no required-reviewer rule. The existing `SNK_KEY`
secret is available to release jobs. NuGet publication uses trusted
publishing for the `scylladb.publish.code` account. Its configured policy
trusts `scylladb/csharp-driver`, workflow file `publish.yml`, and environment
`release`. The publish job exchanges a fresh GitHub OIDC token for a
short-lived NuGet publishing credential immediately before each package
upload; no stored NuGet key is needed.
Commit preparation also requires `RELEASE_GPG_PRIVATE_KEY` for the publisher
key and `RELEASE_BOT_TOKEN` for the `scylladb-publisher` account, both scoped to
the `release` environment. The publisher must have repository write access
and an individual-user bypass for the protected `master` and `branch-3.22`
ruleset. This token pushes the signed release commit and signed annotated tag.
The publishing job gives its built-in `GITHUB_TOKEN` Contents write and
grants `id-token: write` for the NuGet exchange; other jobs retain read-only
GitHub content access.

The release environment is a GitHub repository setting, so merging a pull
request does not change its reviewer rule. After this policy change is merged,
an administrator removes that rule while retaining the branch restriction and
environment secrets:

```bash
python3 build/remove-release-review.py --repository scylladb/csharp-driver
python3 build/remove-release-review.py --repository scylladb/csharp-driver --apply
```

The first command inspects the current configuration; `--apply` updates it and
verifies the branch policies and secret names afterward. The release workflow
still requires explicit manual dispatch, the selected milestone gate, exact
branch-tip CI, signed release commit, and package validation. A release run
that was already waiting for approval may need to be rerun after the setting
changes.

Two active rulesets must protect `refs/tags/v*.*.*.*`. The existing `Protect
release tags` ruleset restricts updates and deletion with no bypass actor. A
separate creation ruleset must allow only the `scylladb-publisher` user (ID
`338827103`) to bypass its creation restriction in `always` mode. Keeping
update and deletion in the no-bypass ruleset leaves tags immutable even for
the publisher. An administrator must add the creation ruleset before production
use. The workflow rejects a preexisting tag on an initial run and never moves
or deletes a tag. Verify both rulesets afterward:

```bash
GITHUB_TOKEN=<admin-token> python3 build/release-gate.py audit-ruleset \
  --repository scylladb/csharp-driver
```

### Partial-publication recovery

If publication stops after the signed commit but before the tag, dispatch a
new workflow run from that signed branch tip, passing its full SHA as the target
commit and using normal production inputs. A GitHub Actions re-run retains the
old dispatch SHA and cannot resume this state. The new run verifies the commit
and resumes validation without creating another release commit.

If publication stops after the tag or one of the packages is published:

1. Fix any newly opened release blocker before retrying.
2. Dispatch the workflow for the same version and exact tagged commit, with
   **Resume partial publication** enabled.
3. The workflow verifies that the existing signed annotated tag points to
   that commit, rebuilds and validates the package set, and skips only package
   versions already present on NuGet.
4. Verify all three public packages and the GitHub Release after completion.

Never delete, move, force-update, or reuse a release tag to recover a failed
publication.
