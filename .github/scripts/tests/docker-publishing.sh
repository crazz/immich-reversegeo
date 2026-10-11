#!/usr/bin/env bash
set -euo pipefail

publishing_repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd)
publishing_test_dir=$(mktemp -d)
trap 'rm -f "$publishing_test_dir/gh" "$publishing_test_dir/docker" "$publishing_test_dir/calls" "$publishing_test_dir/output"; rmdir "$publishing_test_dir"' EXIT

for release_tag in v0.0.0 v1.2.3 v1.2.3-0 v1.2.3-rc.1 v1.2.3-alpha--beta v1.2.3-x-y-z.-- v1.2.3--alpha v1.2.3-rc-; do
    RELEASE_TAG="$release_tag" bash "$publishing_repo_root/.github/scripts/validate-release-version.sh"
done
for release_tag in '' 1.2.3 v01.2.3 v1.02.3 v1.2.03 v1.2.3- v1.2.3-rc..1 v1.2.3-01 v1.2.3-rc.01 v1.2.3+build; do
    if RELEASE_TAG="$release_tag" bash "$publishing_repo_root/.github/scripts/validate-release-version.sh" > "$publishing_test_dir/output" 2>&1; then
        echo "Unexpectedly accepted release tag: $release_tag"
        exit 1
    fi
done

cat > "$publishing_test_dir/gh" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
[[ "$*" == 'api repos/example/app/releases/latest --jq .tag_name' ]]
[[ "${MOCK_GH_FAIL:-false}" != true ]]
printf '%s\n' "$MOCK_LATEST_TAG"
MOCK
cat > "$publishing_test_dir/docker" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$MOCK_DOCKER_CALLS"
[[ "${MOCK_DOCKER_FAIL:-false}" != true ]]
MOCK
chmod +x "$publishing_test_dir/gh" "$publishing_test_dir/docker"
export PATH="$publishing_test_dir:$PATH"
export MOCK_DOCKER_CALLS="$publishing_test_dir/calls"
export EVENT_NAME=release REPOSITORY=example/app RELEASE_TAG=v1.2.3 IS_PRERELEASE=false MANUAL_PROMOTION=false
export SOURCE_IMAGE=ghcr.io/example/app:1.2.3
export SOURCE_DIGEST=sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
export MOCK_LATEST_TAG=v1.2.3 MOCK_GH_FAIL=false MOCK_DOCKER_FAIL=false

run_promotion()
{
    : > "$MOCK_DOCKER_CALLS"
    bash "$publishing_repo_root/.github/scripts/promote-latest.sh" > "$publishing_test_dir/output" 2>&1
}
assert_skipped()
{
    run_promotion
    [[ ! -s "$MOCK_DOCKER_CALLS" ]]
}
assert_promoted()
{
    run_promotion
    [[ "$(cat "$MOCK_DOCKER_CALLS")" == "buildx imagetools create --prefer-index=false --tag ghcr.io/example/app:latest $SOURCE_IMAGE@$SOURCE_DIGEST" ]]
}

assert_promoted
# Simulate an older release retry after a newer release is designated latest.
MOCK_LATEST_TAG=v1.2.4
assert_skipped
# Simulate a delayed older build after the newer image was already promoted.
RELEASE_TAG=v1.2.4 SOURCE_IMAGE=ghcr.io/example/app:1.2.4
assert_promoted
RELEASE_TAG=v1.2.3 SOURCE_IMAGE=ghcr.io/example/app:1.2.3
assert_skipped
IS_PRERELEASE=true
assert_skipped
IS_PRERELEASE=false RELEASE_TAG=v1.2.3-alpha--beta
assert_skipped
EVENT_NAME=workflow_dispatch MANUAL_PROMOTION=false
assert_skipped
MANUAL_PROMOTION=true MOCK_GH_FAIL=true
assert_promoted
EVENT_NAME=release RELEASE_TAG=v1.2.3 MANUAL_PROMOTION=false
if run_promotion; then
    echo "Promotion should fail when GitHub's latest release cannot be checked."
    exit 1
fi
[[ ! -s "$MOCK_DOCKER_CALLS" ]]
MOCK_GH_FAIL=false MOCK_LATEST_TAG=v1.2.3 MOCK_DOCKER_FAIL=true
if run_promotion; then
    echo "Promotion should report a registry failure."
    exit 1
fi

echo "Docker publishing regression checks passed."
