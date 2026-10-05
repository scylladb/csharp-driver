# .NET compatibility

Treat `TargetFramework`, `TargetFrameworks`, `LangVersion`, and `global.json` as compatibility policy; never change them incidentally.

- Omit `LangVersion` by default. Never use `latest`, `latestMajor`, `default`, or `preview`. A numeric override—or removal of an existing pin—requires a documented reason and validation on every TFM.
- Shipping projects must not use a language version newer than their TFM. On `master`, all projects target `net10.0` and use its default C# language version; the `3.22` branch retains its frozen C# 7.1 pins.
- NUnit 4.6+ on pre-.NET 9 test targets is a test-only C# 13 exception. The alternative is explicitly typed `Action`/`Func` lambdas.
- TFM or SDK changes require an explicit support/release decision. Audit conditional references, CI, documentation, and package output; do not drop a test TFM solely because it is end-of-life.
- For authorized changes, fresh-restore, build, inspect assets, and run tests on every target. Pack and check API compatibility for shipping changes, then update CI, documentation, release notes, and the PR description.
