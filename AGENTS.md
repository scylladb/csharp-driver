# 3.22 maintenance policy

The `branch-3.22` branch is the compatibility maintenance line. It accepts
security and correctness fixes only until end of life is announced. Do not add features
or new public APIs.

## Change admission

- Implement fixes on `master` first, then cherry-pick the focused commits to
  `branch-3.22`.
- A fix that applies only to legacy code may start on `branch-3.22` when its pull
  request links an issue explaining why no `master` change applies.
- Never merge `master` into `branch-3.22` after the branch cut. Backport individual
  reviewed commits instead.
- Do not remove or break public APIs. Dependency changes must be required for a
  security or correctness fix and should use the smallest compatible update.

## .NET compatibility freeze

Treat `TargetFramework`, `TargetFrameworks`, `LangVersion`, and `global.json`
as compatibility policy; never change them incidentally.

- Keep all shipping projects on `netstandard2.0`.
- Keep unit and integration test coverage on `net6`, `net7`, `net8`, and
  `net9`; do not drop a test TFM solely because it is end of life.
- Keep SDK `9.0.318` in `global.json`.
- Omit `LangVersion` by default. Shipping projects must not use a language
  version newer than their TFM.
- Preserve the numeric C# 7.1 pins in `Cassandra`, `Cassandra.AppMetrics`,
  `Cassandra.Tests`, and `Cassandra.IntegrationTests`. Never use `latest`,
  `latestMajor`, `default`, or `preview`.
- NUnit 4.6+ on pre-.NET 9 test targets is a test-only C# 13 exception. Prefer
  explicitly typed `Action`/`Func` lambdas unless a task explicitly authorizes
  and validates that exception.

A TFM, SDK, or language-version change requires an explicit support and release
decision. For an authorized compatibility change, audit conditional
references, CI, documentation, and package output; fresh-restore, build, and
test every TFM; then pack, inspect assets, and check API compatibility before
updating release notes and the pull request description.
