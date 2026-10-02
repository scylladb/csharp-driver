# Maintenance and release policy

The ScyllaDB C# Driver has separate branches for the maintained version 3 line
and version 4 development.

| Line | Status | Branch | Package target | Validated runtime |
|---|---|---|---|---|
| 3.22.x | Maintained; security and correctness fixes only | [`3.22`](https://github.com/scylladb/csharp-driver/tree/3.22) | `netstandard2.0` | .NET 6–.NET 9 |
| 4.x | Active development; not yet released | [`master`](https://github.com/scylladb/csharp-driver/tree/master) | `net10.0` | .NET 10 |

The 3.22.x line remains supported until the project announces an end-of-life
date in advance. Version 4 does not replace that compatibility line before the
announced date takes effect.

## 3.22 change policy

The `3.22` branch accepts fixes for security vulnerabilities and incorrect
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
   a pull request targeting `3.22`.
4. If affected code exists only on `3.22`, link an issue that explains why a
   `master` change is not applicable.
5. Resolve conflicts in the backport. Never merge post-cut `master` into
   `3.22`.

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
Release, then update the documentation catalog and `stable` pointer.
