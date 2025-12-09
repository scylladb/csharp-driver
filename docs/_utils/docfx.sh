#!/bin/bash

# Check if docfx is available
if ! command -v docfx &> /dev/null; then
    echo "Warning: docfx not found. Skipping API documentation generation."
    echo "Install with: dotnet tool update -g docfx"
    exit 0
fi

# Ensure the signing key exists for docfx builds
# The scylladb.snk file is gitignored, so use the dev key
if [ ! -f "build/scylladb.snk" ]; then
    echo "Using scylladb-dev.snk for documentation build..."
    cp build/scylladb-dev.snk build/scylladb.snk
fi

# Navigate to the api-docs directory where docfx.json is located
cd docs/source/api-docs

# Define output folder
OUTPUT_DIR="../../_build/dirhtml/api-docs"
if [[ "$SPHINX_MULTIVERSION_OUTPUTDIR" != "" ]]; then
    OUTPUT_DIR="$SPHINX_MULTIVERSION_OUTPUTDIR/api-docs"
fi

# Generate API documentation with docfx
echo "Generating API documentation with docfx..."
docfx metadata docfx.json
docfx build docfx.json

# Move the built API docs to the output directory
[ -d "$OUTPUT_DIR" ] && rm -r "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"
if [ -d "api-docs" ]; then
    mv -f api-docs/* "$OUTPUT_DIR/"
    echo "API documentation generated successfully at $OUTPUT_DIR"
else
    echo "Warning: api-docs directory not found after docfx build"
fi

