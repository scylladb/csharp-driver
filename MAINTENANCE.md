# 3.22 maintenance policy

The `3.22` branch is the maintained compatibility line for version 3 of the
ScyllaDB C# Driver. It will remain supported until the project announces its
end-of-life date in advance.

Version 4 development takes place on
[`master`](https://github.com/scylladb/csharp-driver/tree/master). The branches
have different compatibility contracts and must not be merged into each other
after the version 4 branch cut.

## Compatibility contract

| Area | 3.22 policy |
|---|---|
| Shipping target | `.NET Standard 2.0` (`netstandard2.0`) |
| Unit and integration test targets | .NET 6, .NET 7, .NET 8, and .NET 9 |
| Build SDK | .NET SDK 9.0.318 |
| C# language version | C# 7.1 in the four explicitly pinned core, AppMetrics, and test projects; the TFM default in OpenTelemetry |
| Public API | Backward compatible; no removals or new APIs |

The shipping target, test matrix, SDK pin, and existing language-version
configuration are frozen for the lifetime of the line. Changing one requires
an explicit support and release decision with validation across every target;
it is not routine maintenance.

## Accepted changes

The branch accepts:

- fixes for security vulnerabilities;
- fixes for incorrect behavior, regressions, data loss, resource leaks, and
  reliability defects;
- tests and documentation needed to demonstrate or explain those fixes; and
- the minimum compatible dependency update required by an accepted security or
  correctness fix.

The branch does not accept features, new public APIs, opportunistic dependency
updates, broad refactoring, or unrelated modernization. A maintenance release
must not remove or break an API present in the previous 3.22 release.

## Backport process

1. File or identify an issue that describes the security or correctness
   problem and its acceptance criteria.
2. Fix and review the problem on `master` first.
3. Cherry-pick only the focused fix and its tests with `git cherry-pick -x` to
   a pull request targeting `3.22`.
4. If the affected code exists only on `3.22`, link an issue that explains why
   a `master` change is not applicable.
5. Run the maintenance validation matrix and resolve conflicts without merging
   post-cut `master` into `3.22`.

## Release process

Each 3.22.x release is tracked by a version milestone. A release is eligible to
publish only when that milestone has no open `release-blocker` issues. Before
tagging, maintainers run the supported unit and integration matrix, audit the
dependency graph, validate all NuGet artifacts and signing, compare the public
API with the previous 3.22 release, and complete a production-key dry run.

Release tags use the four-part package version prefixed with `v`, for example
`v3.22.0.5`. `ScyllaDBCSharpDriver`, `ScyllaDBCSharpDriver.AppMetrics`, and
`ScyllaDBCSharpDriver.OpenTelemetry` receive the same version and are released
together. After publication, the GitHub Release and the `master` branch's
documentation catalog must point to the same artifacts.

## End of life

There is no calendar EOL date for 3.22 at branch creation. The project will
announce an EOL date before maintenance ends. Until that announcement takes
effect, accepted security and correctness fixes continue to follow this policy.
