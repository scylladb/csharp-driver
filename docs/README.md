# ScyllaDB C# Driver Documentation

This directory contains the documentation for the ScyllaDB C# Driver, built with Sphinx and integrated with DocFX for API reference generation.

## Prerequisites

- Python 3.10+
- [uv](https://docs.astral.sh/uv/) - Python dependency management
- [.NET SDK 10.0.401](https://dotnet.microsoft.com/download/dotnet/10.0) - For API documentation generation (also pinned by `global.json`)
- [DocFX 2.77+](https://dotnet.github.io/docfx/) - C# API documentation tool

## Quickstart

Install dependencies (first time only):

```bash
# Install uv and DocFX
make setupenv
```

Preview the documentation locally:

```bash
make preview
```

## API Documentation

The API reference is generated with DocFX and available at:
- Local preview: `http://localhost:5500/api-docs/`
- Build output: `_build/dirhtml/api-docs/`

**Note:** If DocFX is not installed, the build will succeed but skip API documentation generation.
