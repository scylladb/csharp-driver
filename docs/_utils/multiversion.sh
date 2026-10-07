#!/bin/bash
set -euo pipefail

export SPHINX_RELEASED_TAGS="$(git tag --list 'v*')"
cd .. && sphinx-multiversion docs/source docs/_build/dirhtml \
    --pre-build './docs/_utils/docfx.sh'

