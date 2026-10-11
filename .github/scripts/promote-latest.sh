#!/usr/bin/env bash
set -euo pipefail

if [[ "${EVENT_NAME:-}" == release ]]; then
    if [[ "${IS_PRERELEASE:-false}" == true || "${RELEASE_TAG:-}" == *-* ]]; then
        echo "Prereleases do not update latest."
        exit 0
    fi
    : "${REPOSITORY:?}" "${RELEASE_TAG:?}"
    latest_release=$(gh api "repos/$REPOSITORY/releases/latest" --jq .tag_name)
    if [[ "$latest_release" != "$RELEASE_TAG" ]]; then
        echo "Skipping latest promotion: $RELEASE_TAG is not GitHub's latest stable release ($latest_release)."
        exit 0
    fi
elif [[ "${EVENT_NAME:-}" != workflow_dispatch || "${MANUAL_PROMOTION:-false}" != true ]]; then
    echo "No latest promotion requested."
    exit 0
fi

: "${SOURCE_IMAGE:?}" "${SOURCE_DIGEST:?}"
docker buildx imagetools create --prefer-index=false \
    --tag "${SOURCE_IMAGE%:*}:latest" "${SOURCE_IMAGE}@${SOURCE_DIGEST}"
