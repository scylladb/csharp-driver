#!/usr/bin/env bash
set -euo pipefail

: "${GATE_TOKEN:?}"
: "${NUGET_API_KEY:?}"
: "${PACKAGE_ID:?}"
: "${PACKAGE_PATH:?}"
: "${RECOVERY:?}"
: "${RELEASE_VERSION:?}"
: "${TARGET_COMMIT:?}"
: "${GITHUB_REPOSITORY:?}"

package_options=()
if [[ "$RECOVERY" == "true" ]]; then
  package_options+=(--recovery)
fi

GITHUB_TOKEN="$GATE_TOKEN" python3 build/release-gate.py gate \
  --repository "$GITHUB_REPOSITORY" --version "$RELEASE_VERSION" \
  --target-commit "$TARGET_COMMIT" "${package_options[@]}"

state=$(python3 build/release-gate.py published-package \
  --package "$PACKAGE_PATH" --package-id "$PACKAGE_ID" \
  --version "$RELEASE_VERSION" "${package_options[@]}")
if [[ "$state" == "absent" ]]; then
  dotnet nuget push "$PACKAGE_PATH" --api-key "$NUGET_API_KEY" \
    --source https://api.nuget.org/v3/index.json --skip-duplicate
  for attempt in {1..180}; do
    state=$(python3 build/release-gate.py published-package \
      --package "$PACKAGE_PATH" --package-id "$PACKAGE_ID" \
      --version "$RELEASE_VERSION" --recovery \
      --transient-errors-as-retry)
    if [[ "$state" == "present" ]]; then break; fi
    sleep 10
  done
  [[ "$state" == "present" ]]
fi
