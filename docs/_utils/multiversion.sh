#!/bin/bash

cd .. && sphinx-multiversion docs/source docs/_build/dirhtml \
    --pre-build './docs/_utils/docfx.sh'


