# Repository instructions

## .NET compatibility policy

Treat `TargetFramework`, `TargetFrameworks`, `LangVersion`, and `global.json` as compatibility policy. Do not change them as incidental cleanup or dependency-update fallout. If a request does not explicitly include a compatibility change, preserve the existing values and surface the tradeoff.

- A TFM selects the compile-time API surface, framework defines, NuGet assets, test runtime, package output, and default C# version.
- `LangVersion` selects C# syntax and compiler semantics; it does not provide newer runtime APIs.
- `global.json` selects the SDK, compiler, MSBuild, and CLI independently of the target runtime.

### `LangVersion`

- Prefer omission so the SDK selects the language version supported by each TFM.
- Do not substitute `latest`, `latestMajor`, `default`, or `preview`. Use an exact numeric override only for a documented constraint.
- Removing an existing pin is also a policy change. Determine the resulting version for every target and build all of them first.
- Shipping projects must not use a language version newer than their TFM. Prefer a source fix or compatible dependency instead.
- A newer test/tooling override must be a narrow compiler-only exception with a pinned SDK, validation on every target, and alternatives documented in the PR. Do not propagate it to shipping projects.

Repository constraints:

- `Cassandra` and `Cassandra.AppMetrics` pin C# 7.1; omission changes them to C# 7.3. `Cassandra.OpenTelemetry` already uses the TFM default.
- NUnit 4.6+ needs C# 13 overload-priority semantics when tests retain pre-.NET 9 targets. Either use an exact test-only C# 13 override or type affected lambdas as `Action`, `Func<T>`, or `Func<Task>` before omitting `LangVersion`.

### TFM and SDK changes

- Adding, removing, or upgrading a TFM requires an explicit support and release rationale. Distinguish shipped package targets from test-only runtime coverage.
- Do not drop an older test target solely because the runtime is end-of-life; library compatibility coverage can remain intentional.
- Audit conditional packages and project references, `SetTargetFramework`, compile symbols, test adapters, the Makefile, CI matrices, examples, documentation, and NuGet package output together.
- Existing shorthand TFMs and raw string/regex conditions are fragile for dotted, platform-qualified, or two-digit TFMs. Update all affected conditions atomically; prefer parsed MSBuild framework identifiers and versions for new logic.
- The main workflow ignores root JSON-only changes. Arrange explicit CI validation for a `global.json`-only change.

### Required validation

For an authorized compatibility change:

1. Record the before-and-after matrix of project, TFM, effective C# version, SDK/runtime, and conditional packages.
2. Fresh-restore and build every target; inspect resolved package and tool assets.
3. Compare test discovery with the merge base, run unit tests on every target, and run applicable integration tests.
4. For shipping changes, pack the projects and inspect package/API compatibility.
5. Update CI, compatibility documentation, and release notes, and record exact validation in the PR.
