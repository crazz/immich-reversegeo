#!/usr/bin/env bash
set -euo pipefail

if [[ ! "${RELEASE_TAG:-}" =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]]; then
    echo "Release tags must use vMAJOR.MINOR.PATCH, optionally followed by a prerelease suffix."
    exit 1
fi
if [[ "$RELEASE_TAG" == *-* ]]; then
    IFS='.' read -ra identifiers <<< "${RELEASE_TAG#*-}"
    for identifier in "${identifiers[@]}"; do
        if [[ "$identifier" =~ ^0[0-9]+$ ]]; then
            echo "Numeric prerelease identifiers must not have leading zeros."
            exit 1
        fi
    done
fi
