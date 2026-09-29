# Repository instructions

## Target frameworks, language versions, and SDKs

Treat `TargetFramework`, `TargetFrameworks`, `LangVersion`, and `global.json` as compatibility policy, not ordinary build settings. Changes to any of them can alter compiler semantics, available APIs, dependency selection, generated assets, runtime coverage, and consumer support.

Do not add, remove, or change these settings as incidental cleanup. A dependency update, build repair, warning cleanup, or CI change does not by itself authorize a compatibility-policy change. If the requested work does not explicitly include such a change, preserve the existing policy and raise the tradeoff to the maintainer.

### Keep the concepts separate

- A target framework moniker (TFM) selects the compile-time API surface, implicit framework defines, NuGet asset graph, test runtime, and package output. Changing a TFM can be a breaking consumer-support change.
- `global.json` selects the SDK, compiler, MSBuild, and CLI used to build. It is independent of the runtime targeted by a project. Changing it can alter builds and analyzers even when no TFM changes.
- `LangVersion` selects C# syntax and compiler behavior. It does not change the target API surface or make a newer runtime available. Compiler-semantic changes such as overload resolution can affect unchanged source even when no new syntax is used.
- Test-runner adapters and build tools execute under particular runtimes and have their own TFM support. Successful package restore alone does not prove that tests will be discovered or run.

### Language-version policy

Prefer omitting `LangVersion` in SDK-style projects. Omission lets the SDK select the C# version associated with each TFM and is the normal Microsoft-supported configuration.

Do not substitute `latest`, `latestMajor`, `default`, or `preview` for omission. Those values follow compiler or SDK capabilities rather than the TFM and can produce machine-dependent or unsupported builds. If an override is necessary, use an exact numeric version.

Removing an existing `LangVersion` is also a policy change. First calculate the effective language version for every target and confirm that the source still compiles with the resulting compiler semantics. Do not assume an old explicit value is redundant.

For shipping projects:

- Do not select a language version newer than the version associated with any target TFM. Microsoft documents that combination as unsupported.
- Prefer source changes or a compatible dependency version over broadening the language version to make a build pass.
- Keep a deliberate lower language ceiling when source compatibility or project policy requires it; document why it exists.

For test-only or tooling projects, a newer exact language version is permissible only as a narrow, documented exception when all of the following are true:

- a dependency or compiler-semantic requirement is demonstrated;
- the feature is understood and does not require unavailable runtime APIs;
- the required SDK/compiler is pinned and installed in CI;
- every target is restored, built, discovered, and executed;
- the override is not propagated into shipping projects; and
- the PR records the supported alternative and why it was not chosen.

Current repository-specific constraints:

- The shipping `Cassandra` and `Cassandra.AppMetrics` projects intentionally pin C# 7.1. Omitting those pins currently changes their effective language version to C# 7.3; handle that only in a dedicated compatibility change. `Cassandra.OpenTelemetry` already uses the TFM-derived default.
- If NUnit 4.6 or later is used while the test projects retain pre-.NET 9 targets, its preferred `Action`, `Func<T>`, and `Func<Task>` overloads require C# 13 overload-priority semantics. TFM-derived C# 10, 11, and 12 builds produce ambiguous-call errors. Treat an exact C# 13 setting as a test-only exception, not a precedent for production projects.
- The standards-aligned alternative to that exception is to type the affected lambdas explicitly as `Action`, `Func<T>`, or `Func<Task>` and omit `LangVersion`. Other alternatives are pinning NUnit below 4.6 or intentionally dropping older test targets. Do not silently choose among these policies.
- The main GitHub Actions workflow ignores changes that affect only root JSON or Markdown files. Do not assume an SDK-only `global.json` change has been validated merely because no CI job failed; arrange an explicit run or update the trigger in the same change.
- Compatibility documentation can lag the live project files. Derive the current matrix from the project files, `global.json`, the Makefile, and CI, then update `README.md` and `CONTRIBUTING.md` when policy changes.

### Target-framework policy

Adding, removing, or upgrading a TFM requires explicit scope and a written support rationale. Before editing, determine whether the target belongs to a shipped package, an example, or only the runtime test matrix; those have different compatibility consequences.

Do not drop an older or end-of-life test target solely because applications should no longer deploy on it. A library may retain compatibility coverage for consumers. Record the reason, CI/runtime availability, security posture, and removal plan.

When a TFM changes, inspect all framework-specific behavior together:

- conditional `PropertyGroup`, `ItemGroup`, and `PackageReference` entries;
- `ProjectReference` metadata such as `SetTargetFramework`;
- NuGet direct and transitive assets for every target;
- implicit and custom compilation symbols and all `#if` branches;
- test adapters, loggers, analyzers, generators, and build tasks;
- CI matrices, installed SDKs/runtimes, Makefile variables, and scripts;
- package output, public API compatibility, support tables, and release notes.

Do not assume raw TFM strings are canonical. This repository contains shorthand TFMs and exact string/regex conditions. A change such as `net8` to `net8.0`, a platform-qualified TFM, or a future two-digit TFM can bypass those conditions. If target spelling or membership changes, locate and update every relevant condition atomically. Prefer parsed framework identifiers/versions or MSBuild framework helpers for new logic when practical.

### Required review and validation

Before implementation:

1. Inventory `TargetFramework`, `TargetFrameworks`, `LangVersion`, `global.json`, `BuildTarget`, `TARGET_FRAMEWORK`, framework conditions, and preprocessor symbols across the repository.
2. State the before-and-after matrix of project, TFM, effective C# version, SDK/compiler, runtime, and important conditional packages.
3. Explain the compatibility reason and the narrower alternatives considered.
4. Identify consumer, packaging, CI, and documentation impact. Keep production and test-only policy changes separate when possible.

After implementation:

1. Perform a fresh restore for every supported target and inspect the resolved package/tool versions per target.
2. Build every target with the pinned SDK. Do not hide incompatibility with warning suppression.
3. Compare test discovery counts and identities with the merge base, then run unit tests on every target and the relevant integration matrix.
4. For shipped TFM changes, pack the projects, inspect the package assets, and run the repository's API/package compatibility checks.
5. Run formatting and diff checks, and update CI, contributor instructions, compatibility tables, and release notes in the same scoped change.
6. Describe the final matrix and exact validation in the PR body. A green default-target build is not sufficient evidence.

Useful primary references:

- [Microsoft: Configure C# language version](https://learn.microsoft.com/dotnet/csharp/language-reference/configure-language-version)
- [Microsoft: Target frameworks in SDK-style projects](https://learn.microsoft.com/dotnet/standard/frameworks)
- [Microsoft: global.json overview](https://learn.microsoft.com/dotnet/core/tools/global-json)
- [NUnit framework release notes](https://docs.nunit.org/articles/nunit/release-notes/framework.html)
