#!/usr/bin/env bash
set -Eeuo pipefail

if ! SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"; then
    printf '%s\n' 'docker-smoke: cannot resolve the script directory.' >&2
    exit 2
fi
readonly SCRIPT_DIR
if ! REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd -P)"; then
    printf '%s\n' 'docker-smoke: cannot resolve the repository root.' >&2
    exit 2
fi
readonly REPO_ROOT
readonly POSTGRES_IMAGE="postgres@sha256:cf78e76683b9ca8c5733cbbdce6c9262b45b6767934dd0a95e671f9a0fc20685"
readonly FIXTURE_LOCK_KEY="460046"
readonly S1_ASSET_ID="46000000-0000-0000-0000-000000000001"
readonly S2_ASSET_ID="46000000-0000-0000-0000-000000000002"

usage() {
    cat <<'EOF'
Usage: npm run test:docker-smoke [-- --image IMAGE]

Build one production image and exercise its Standard, Web-only, Run-once,
invalid-mode, and private-protocol deployment boundaries on native Linux Docker.

Options:
  --image IMAGE  Reuse an existing image; every case still uses its resolved ID.
  --help         Show this help.

Harness verification:
  DOCKER_SMOKE_INJECT_FAILURE=after-packaging-create causes a controlled failure
  in the daemon-create compensation window. retained-secret additionally requires
  DOCKER_SMOKE_REDACTION_SELF_CHECK_CANARY and verifies retained-evidence redaction.
EOF
}

PREBUILT_IMAGE="${DOCKER_SMOKE_IMAGE:-}"
while (($# > 0)); do
    case "$1" in
        --image)
            if (($# < 2)); then
                printf '%s\n' 'docker-smoke: --image requires a value.' >&2
                exit 2
            fi
            PREBUILT_IMAGE="$2"
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            printf 'docker-smoke: unknown option: %s\n' "$1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ "$(uname -s)" != "Linux" ]]; then
    printf '%s\n' 'docker-smoke: native Linux is required for UID/GID and container-IP listener assertions.' >&2
    exit 2
fi

for prerequisite in bash docker curl jq sqlite3 openssl sha256sum stat timeout awk sed grep find date strace sleep; do
    if ! command -v "$prerequisite" >/dev/null 2>&1; then
        printf 'docker-smoke: required command is unavailable: %s\n' "$prerequisite" >&2
        exit 2
    fi
done
if [[ "$(id -u)" != "0" ]]; then
    if ! command -v sudo >/dev/null 2>&1 || ! sudo -n true >/dev/null 2>&1; then
        printf '%s\n' 'docker-smoke: root or working passwordless sudo is required for numeric-UID bind mounts and process tracing.' >&2
        exit 2
    fi
fi

cd "$REPO_ROOT"
if ! DOCKER_BIN="$(command -v docker)"; then
    printf '%s\n' 'docker-smoke: cannot resolve the Docker executable.' >&2
    exit 2
fi
readonly DOCKER_BIN
if ! STRACE_BIN="$(command -v strace)"; then
    printf '%s\n' 'docker-smoke: cannot resolve the strace executable.' >&2
    exit 2
fi
readonly STRACE_BIN

sleep 1 &
trace_probe_pid=$!
trace_probe_status=0
if [[ "$(id -u)" == "0" ]]; then
    timeout 5 "$STRACE_BIN" -qq -e trace=none -p "$trace_probe_pid" -o /dev/null >/dev/null 2>&1 || trace_probe_status=$?
else
    sudo -n timeout 5 "$STRACE_BIN" -qq -e trace=none -p "$trace_probe_pid" -o /dev/null >/dev/null 2>&1 || trace_probe_status=$?
fi
wait "$trace_probe_pid" >/dev/null 2>&1 || true
if ((trace_probe_status != 0)); then
    printf '%s\n' 'docker-smoke: strace cannot attach to a same-user process; ptrace capability is required for child-reap evidence.' >&2
    exit 2
fi

if ! RUN_TIMESTAMP="$(date -u +%Y%m%dT%H%M%SZ)"; then
    printf '%s\n' 'docker-smoke: cannot create the run timestamp.' >&2
    exit 2
fi
if ! RUN_RANDOM="$(openssl rand -hex 3)"; then
    printf '%s\n' 'docker-smoke: cannot create the run identifier.' >&2
    exit 2
fi
readonly RUN_TIMESTAMP RUN_RANDOM
readonly RUN_SUFFIX="$RUN_TIMESTAMP-$$-$RUN_RANDOM"
readonly RUN_ID="immich46-$RUN_SUFFIX"
readonly LABEL_KEY="io.immich-reversegeo.docker-smoke.run"
readonly LABEL_VALUE="$RUN_ID"
readonly LABEL_FILTER="$LABEL_KEY=$LABEL_VALUE"
readonly OUTPUT_ROOT="$REPO_ROOT/_out/docker-mode-smoke/$RUN_ID"
readonly WORK_ROOT="$OUTPUT_ROOT/work"
readonly EVIDENCE_ROOT="$OUTPUT_ROOT/evidence"
readonly RAW_ROOT="$WORK_ROOT/raw"
readonly ASSERTIONS_FILE="$EVIDENCE_ROOT/assertions.tsv"
readonly NETWORK_NAME="$RUN_ID-network"
readonly PUBLIC_NETWORK_NAME="$RUN_ID-web-network"
readonly POSTGRES_VOLUME_NAME="$RUN_ID-postgres-data"
readonly POSTGRES_NAME="$RUN_ID-postgres"
readonly IMAGE_TAG="${RUN_ID,,}-image:smoke"
readonly FIXTURE_SQL="$REPO_ROOT/tests/docker-mode-smoke/fixture.sql"
readonly SETTINGS_FIXTURE="$REPO_ROOT/tests/docker-mode-smoke/settings.json"
if ! mkdir -p "$RAW_ROOT" "$EVIDENCE_ROOT"; then
    printf 'docker-smoke: cannot create the run output root: %s\n' "$OUTPUT_ROOT" >&2
    exit 2
fi
: > "$ASSERTIONS_FILE"

CONTAINERS=()
BACKGROUND_PIDS=()
SECRETS=()
GATE_APPLICATIONS=()
BUILT_IMAGE=0
IMAGE_ID=""
IMAGE_UID=""
IMAGE_GID=""
POSTGRES_READY=0
CLEANUP_FORCED=0
CLEANUP_LEAK=0
FIFO_WRITER_OPEN=0
CREATED_CONTAINER=""
DOCKER_COMMAND_TIMEOUT=30
STANDARD_ADMISSIONS=0
STANDARD_PARENT_HOST_PID=""
STANDARD_CHILD_HOST_PID=""
STANDARD_CHILD_NAMESPACE_PID=""
STANDARD_TRACE_PID=""
STANDARD_TRACER_OS_PID=""
STANDARD_TRACE_PREFIX=""
STANDARD_PARENT_TIDS_FILE=""
BACKGROUND_WAIT_STATUS=""
readonly PRIVATE_READY_TIMEOUT=30
readonly PRIVATE_SQLITE_TIMEOUT=10
readonly PRIVATE_METADATA_TIMEOUT=5
readonly PRIVATE_TCP_TIMEOUT=3
readonly PRIVATE_REAP_TIMEOUT=20
readonly PRIVATE_PHASE_MARGIN=10
readonly PRIVATE_OBSERVATION_BUDGET=$((PRIVATE_SQLITE_TIMEOUT + PRIVATE_METADATA_TIMEOUT + DOCKER_COMMAND_TIMEOUT + DOCKER_COMMAND_TIMEOUT + PRIVATE_TCP_TIMEOUT + PRIVATE_PHASE_MARGIN))
readonly PRIVATE_ATTACH_TIMEOUT=$((PRIVATE_READY_TIMEOUT + PRIVATE_OBSERVATION_BUDGET + PRIVATE_REAP_TIMEOUT + PRIVATE_PHASE_MARGIN))

ADMIN_USER="irgeo46_admin_${RUN_SUFFIX//[^a-zA-Z0-9]/_}"
DATABASE_NAME="irgeo46_${RUN_SUFFIX//[^a-zA-Z0-9]/_}"
STANDARD_ROLE="irgeo46_standard_${RUN_SUFFIX//[^a-zA-Z0-9]/_}"
WEBONLY_ROLE="irgeo46_webonly_${RUN_SUFFIX//[^a-zA-Z0-9]/_}"
RUNONCE_ROLE="irgeo46_runonce_${RUN_SUFFIX//[^a-zA-Z0-9]/_}"
STANDARD_APP="irgeo46-standard-$RUN_SUFFIX"
WEBONLY_APP="irgeo46-webonly-$RUN_SUFFIX"
RUNONCE_APP="irgeo46-runonce-$RUN_SUFFIX"
ADMIN_PASSWORD="$(openssl rand -hex 24)"
STANDARD_PASSWORD="$(openssl rand -hex 24)"
WEBONLY_PASSWORD="$(openssl rand -hex 24)"
RUNONCE_PASSWORD="$(openssl rand -hex 24)"
INVALID_CANARY="$(openssl rand -hex 24)"
INVALID_MODE="invalid-$INVALID_CANARY"
SECRETS+=("$ADMIN_PASSWORD" "$STANDARD_PASSWORD" "$WEBONLY_PASSWORD" "$RUNONCE_PASSWORD" "$INVALID_CANARY" "$INVALID_MODE")
if [[ -n "${DOCKER_SMOKE_REDACTION_SELF_CHECK_CANARY:-}" ]]; then
    if [[ ! "$DOCKER_SMOKE_REDACTION_SELF_CHECK_CANARY" =~ ^[A-Za-z0-9_-]{16,64}$ ]]; then
        printf '%s\n' 'docker-smoke: redaction self-check canary must contain 16-64 safe ASCII characters.' >&2
        exit 2
    fi
    SECRETS+=("$DOCKER_SMOKE_REDACTION_SELF_CHECK_CANARY")
fi

record() {
    local state="$1"
    local assertion="$2"
    printf '%s\t%s\n' "$state" "$assertion" >> "$ASSERTIONS_FILE"
}

pass() {
    record PASS "$1"
}

fail() {
    local diagnostic
    if ! diagnostic="$(printf '%s\n' "$1" | sanitize_stream)"; then
        diagnostic="assertion failed and diagnostic redaction failed"
    fi
    record FAIL "$diagnostic"
    printf 'docker-smoke: FAIL: %s\n' "$diagnostic" >&2
    exit 1
}

require_equal() {
    local actual="$1"
    local expected="$2"
    local assertion="$3"
    if [[ "$actual" != "$expected" ]]; then
        fail "$assertion (expected '$expected', observed '$actual')"
    fi
    pass "$assertion"
}

require_contains() {
    local file="$1"
    local literal="$2"
    local assertion="$3"
    if ! grep -Fq -- "$literal" "$file"; then
        fail "$assertion"
    fi
    pass "$assertion"
}

require_absent() {
    local file="$1"
    local literal="$2"
    local assertion="$3"
    if grep -Fq -- "$literal" "$file"; then
        fail "$assertion"
    fi
    pass "$assertion"
}

wait_until() {
    local seconds="$1"
    local assertion="$2"
    shift 2
    local deadline=$((SECONDS + seconds))
    while ((SECONDS < deadline)); do
        if "$@"; then
            pass "$assertion"
            return 0
        fi
        sleep 1
    done
    fail "$assertion (deadline ${seconds}s)"
}

sanitize_stream() {
    local expressions=()
    local secret
    for secret in "${SECRETS[@]}"; do
        expressions+=("-e" "s/${secret}/[REDACTED]/g")
    done
    sed "${expressions[@]}"
}

capture_sanitized() {
    local destination="$1"
    shift
    local raw="$RAW_ROOT/${destination##*/}.raw"
    "$@" > "$raw" 2>&1 || true
    sanitize_stream < "$raw" > "$destination"
    rm -f "$raw"
}

capture_checked() {
    local destination="$1"
    shift
    local raw="$RAW_ROOT/${destination##*/}.$$.raw"
    local status=0
    "$@" > "$raw" 2>&1 || status=$?
    if ! sanitize_stream < "$raw" > "$destination.tmp"; then
        rm -f "$raw" "$destination.tmp"
        return 1
    fi
    mv "$destination.tmp" "$destination"
    rm -f "$raw"
    return "$status"
}

docker_available() {
    docker info >/dev/null 2>&1
}

docker() {
    timeout "$DOCKER_COMMAND_TIMEOUT" "$DOCKER_BIN" "$@"
}

run_root() {
    if [[ "$(id -u)" == "0" ]]; then
        "$@"
    else
        sudo -n "$@"
    fi
}

container_exists() {
    docker inspect "$1" >/dev/null 2>&1
}

container_running() {
    [[ "$(docker inspect --format '{{.State.Running}}' "$1" 2>/dev/null || true)" == "true" ]]
}

container_stopped() {
    [[ "$(docker inspect --format '{{.State.Running}}' "$1" 2>/dev/null || true)" == "false" ]]
}

register_container() {
    CONTAINERS+=("$1")
}

container_has_owned_label() {
    local name="$1"
    [[ "$(docker inspect --format "{{index .Config.Labels \"$LABEL_KEY\"}}" "$name" 2>/dev/null || true)" == "$LABEL_VALUE" ]]
}

cleanup_owned_container() {
    local name="$1"
    if ! container_exists "$name"; then
        return 0
    fi
    if ! container_has_owned_label "$name"; then
        CLEANUP_LEAK=1
        printf 'FAIL\t%s\n' "cleanup-refused-mismatched-container-label:$name" >> "$ASSERTIONS_FILE"
        return 0
    fi
    if container_running "$name"; then
        if ! docker stop --time 12 "$name" >/dev/null 2>&1; then
            CLEANUP_FORCED=1
            docker kill "$name" >/dev/null 2>&1 || true
        fi
        if [[ "$(docker inspect --format '{{.State.ExitCode}}' "$name" 2>/dev/null || true)" == "137" ]]; then
            CLEANUP_FORCED=1
        fi
    fi
    if ! docker rm "$name" >/dev/null 2>&1; then
        CLEANUP_FORCED=1
        docker rm --force "$name" >/dev/null 2>&1 || true
    fi
}

network_has_owned_label() {
    [[ "$(docker network inspect --format "{{index .Labels \"$LABEL_KEY\"}}" "$1" 2>/dev/null || true)" == "$LABEL_VALUE" ]]
}

cleanup_owned_network() {
    local network="$1"
    if ! docker network inspect "$network" >/dev/null 2>&1; then
        return 0
    fi
    if ! network_has_owned_label "$network"; then
        CLEANUP_LEAK=1
        printf 'FAIL\t%s\n' "cleanup-refused-mismatched-network-label:$network" >> "$ASSERTIONS_FILE"
        return 0
    fi
    docker network rm "$network" >/dev/null 2>&1 || CLEANUP_LEAK=1
}

volume_has_owned_label() {
    [[ "$(docker volume inspect --format "{{index .Labels \"$LABEL_KEY\"}}" "$1" 2>/dev/null || true)" == "$LABEL_VALUE" ]]
}

cleanup_owned_volume() {
    local volume="$1"
    if ! docker volume inspect "$volume" >/dev/null 2>&1; then
        return 0
    fi
    if ! volume_has_owned_label "$volume"; then
        CLEANUP_LEAK=1
        printf 'FAIL\t%s\n' "cleanup-refused-mismatched-volume-label:$volume" >> "$ASSERTIONS_FILE"
        return 0
    fi
    docker volume rm "$volume" >/dev/null 2>&1 || CLEANUP_LEAK=1
}

capture_diagnostics() {
    if ! docker_available; then
        return 0
    fi

    capture_sanitized "$EVIDENCE_ROOT/image-inspect.json" docker image inspect "$IMAGE_ID"
    capture_sanitized "$EVIDENCE_ROOT/docker-info.txt" docker info
    local container
    for container in "${CONTAINERS[@]}"; do
        if container_exists "$container"; then
            capture_sanitized "$EVIDENCE_ROOT/$container-inspect.json" docker inspect "$container"
            capture_sanitized "$EVIDENCE_ROOT/$container-logs.txt" docker logs --timestamps "$container"
            capture_sanitized "$EVIDENCE_ROOT/$container-top.txt" docker top "$container" -eo pid,ppid,uid,gid,args
        fi
    done

    if ((POSTGRES_READY)); then
        capture_sanitized "$EVIDENCE_ROOT/postgres-activity.txt" pg_admin -P pager=off -x -c \
            "SELECT pid, usename, application_name, state, wait_event_type, wait_event, query FROM pg_stat_activity WHERE datname = current_database() ORDER BY pid"
        capture_sanitized "$EVIDENCE_ROOT/postgres-locks.txt" pg_admin -P pager=off -x -c \
            "SELECT pid, locktype, classid, objid, objsubid, mode, granted FROM pg_locks WHERE database = (SELECT oid FROM pg_database WHERE datname = current_database()) ORDER BY pid, locktype"
    fi

    run_root find "$WORK_ROOT" -mindepth 1 -maxdepth 5 -printf '%M|%U|%G|%s|%p\n' 2>/dev/null \
        | sanitize_stream > "$EVIDENCE_ROOT/work-tree.txt" || true
}

terminate_gate_backends() {
    if ((!POSTGRES_READY)) || ! container_running "$POSTGRES_NAME"; then
        return 0
    fi
    local application
    for application in "${GATE_APPLICATIONS[@]}"; do
        pg_admin -Atc "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=current_database() AND application_name='${application}' AND pid<>pg_backend_pid()" >/dev/null 2>&1 || true
    done
}

cleanup() {
    local incoming_status="$1"
    trap '' INT TERM
    trap - EXIT
    set +e

    if ((FIFO_WRITER_OPEN)); then
        exec 9>&-
        FIFO_WRITER_OPEN=0
    fi

    capture_diagnostics
    terminate_gate_backends

    if [[ -n "$STANDARD_TRACER_OS_PID" && -e "/proc/$STANDARD_TRACER_OS_PID/status" ]]; then
        run_root kill -TERM "$STANDARD_TRACER_OS_PID" >/dev/null 2>&1 || CLEANUP_FORCED=1
        if ! timeout 10 tail --pid="$STANDARD_TRACER_OS_PID" -f /dev/null >/dev/null 2>&1; then
            CLEANUP_FORCED=1
            run_root kill -KILL "$STANDARD_TRACER_OS_PID" >/dev/null 2>&1 || true
        fi
    fi

    local pid
    for pid in "${BACKGROUND_PIDS[@]}"; do
        if kill -0 "$pid" >/dev/null 2>&1; then
            kill -TERM "$pid" >/dev/null 2>&1
        fi
        if ! timeout 10 tail --pid="$pid" -f /dev/null >/dev/null 2>&1; then
            CLEANUP_FORCED=1
            kill -KILL "$pid" >/dev/null 2>&1
        fi
        wait "$pid" >/dev/null 2>&1 || true
    done

    local container
    for container in "${CONTAINERS[@]}"; do
        cleanup_owned_container "$container"
    done

    local labeled_containers
    if ! labeled_containers="$(docker ps -aq --filter "label=$LABEL_FILTER" 2>/dev/null)"; then
        CLEANUP_LEAK=1
        printf 'FAIL\t%s\n' 'cleanup-container-reconciliation-query-failed' >> "$ASSERTIONS_FILE"
    else
        while IFS= read -r container; do
            [[ -n "$container" ]] && cleanup_owned_container "$container"
        done <<< "$labeled_containers"
    fi

    local network
    for network in "$PUBLIC_NETWORK_NAME" "$NETWORK_NAME"; do
        cleanup_owned_network "$network"
    done
    local labeled_networks
    if ! labeled_networks="$(docker network ls -q --filter "label=$LABEL_FILTER" 2>/dev/null)"; then
        CLEANUP_LEAK=1
        printf 'FAIL\t%s\n' 'cleanup-network-reconciliation-query-failed' >> "$ASSERTIONS_FILE"
    else
        while IFS= read -r network; do
            [[ -n "$network" ]] && cleanup_owned_network "$network"
        done <<< "$labeled_networks"
    fi

    cleanup_owned_volume "$POSTGRES_VOLUME_NAME"
    local labeled_volumes volume
    if ! labeled_volumes="$(docker volume ls -q --filter "label=$LABEL_FILTER" 2>/dev/null)"; then
        CLEANUP_LEAK=1
        printf 'FAIL\t%s\n' 'cleanup-volume-reconciliation-query-failed' >> "$ASSERTIONS_FILE"
    else
        while IFS= read -r volume; do
            [[ -n "$volume" ]] && cleanup_owned_volume "$volume"
        done <<< "$labeled_volumes"
    fi

    if ((BUILT_IMAGE)); then
        docker image rm "$IMAGE_TAG" >/dev/null 2>&1 || CLEANUP_LEAK=1
    fi

    local remaining_containers remaining_networks remaining_volumes
    if ! remaining_containers="$(docker ps -aq --filter "label=$LABEL_FILTER" 2>/dev/null)"; then
        CLEANUP_LEAK=1
        printf 'FAIL\t%s\n' 'cleanup-container-query-failed' >> "$ASSERTIONS_FILE"
    elif [[ -n "$remaining_containers" ]]; then
        CLEANUP_LEAK=1
    fi
    if ! remaining_networks="$(docker network ls -q --filter "label=$LABEL_FILTER" 2>/dev/null)"; then
        CLEANUP_LEAK=1
        printf 'FAIL\t%s\n' 'cleanup-network-query-failed' >> "$ASSERTIONS_FILE"
    elif [[ -n "$remaining_networks" ]]; then
        CLEANUP_LEAK=1
    fi
    if ! remaining_volumes="$(docker volume ls -q --filter "label=$LABEL_FILTER" 2>/dev/null)"; then
        CLEANUP_LEAK=1
        printf 'FAIL\t%s\n' 'cleanup-volume-query-failed' >> "$ASSERTIONS_FILE"
    elif [[ -n "$remaining_volumes" ]]; then
        CLEANUP_LEAK=1
    fi

    local secret evidence_file redacted_file secret_found=0
    while IFS= read -r evidence_file; do
        for secret in "${SECRETS[@]}"; do
            if grep -Fq -- "$secret" "$evidence_file" 2>/dev/null; then
                secret_found=1
                redacted_file="$RAW_ROOT/redacted.$$.tmp"
                if sanitize_stream < "$evidence_file" > "$redacted_file"; then
                    mv "$redacted_file" "$evidence_file"
                else
                    rm -f "$redacted_file" "$evidence_file"
                    printf '%s\n' '[evidence removed because redaction failed]' > "$evidence_file"
                fi
                break
            fi
        done
    done < <(find "$EVIDENCE_ROOT" -type f -print 2>/dev/null)
    if ((secret_found)); then
        CLEANUP_LEAK=1
        printf '%s\n' 'FAIL	evidence-secret-scan-redacted' >> "$ASSERTIONS_FILE"
    fi

    if [[ "$WORK_ROOT" != "$REPO_ROOT/_out/docker-mode-smoke/$RUN_ID/work" ]]; then
        CLEANUP_LEAK=1
    elif ! run_root timeout 20 rm -rf -- "$WORK_ROOT"; then
        CLEANUP_LEAK=1
    fi
    if [[ -e "$WORK_ROOT" ]]; then
        CLEANUP_LEAK=1
    fi
    if [[ -n "$STANDARD_TRACER_OS_PID" && -e "/proc/$STANDARD_TRACER_OS_PID/status" ]]; then
        CLEANUP_LEAK=1
        printf '%s\n' 'FAIL	task-owned-tracer-survived-cleanup' >> "$ASSERTIONS_FILE"
    fi

    if ((CLEANUP_FORCED)); then
        printf '%s\n' 'FAIL	cleanup-required-force' >> "$ASSERTIONS_FILE"
    else
        printf '%s\n' 'PASS	cleanup-no-force' >> "$ASSERTIONS_FILE"
    fi
    if ((CLEANUP_LEAK)); then
        printf '%s\n' 'FAIL	cleanup-or-redaction-leak' >> "$ASSERTIONS_FILE"
    else
        printf '%s\n' 'PASS	cleanup-zero-owned-resources-and-secret-free-evidence' >> "$ASSERTIONS_FILE"
    fi

    if ((incoming_status == 0 && (CLEANUP_FORCED || CLEANUP_LEAK))); then
        incoming_status=1
    fi
    if ((incoming_status == 0)); then
        printf 'docker-smoke: PASS (%s) evidence=%s\n' "$RUN_ID" "$EVIDENCE_ROOT"
    else
        printf 'docker-smoke: FAILED (%s) evidence=%s\n' "$RUN_ID" "$EVIDENCE_ROOT" >&2
    fi
    exit "$incoming_status"
}

on_signal() {
    record FAIL "interrupted-by-$1"
    exit 130
}

trap 'cleanup $?' EXIT
trap 'on_signal INT' INT
trap 'on_signal TERM' TERM

await_background() {
    local pid="$1"
    local seconds="$2"
    if ! timeout "$seconds" tail --pid="$pid" -f /dev/null >/dev/null 2>&1; then
        return 1
    fi
    local status=0
    wait "$pid" || status=$?
    BACKGROUND_WAIT_STATUS="$status"
}

timeout 1 sleep 5 &
deadline_self_check_pid=$!
if ! await_background "$deadline_self_check_pid" 3; then
    fail "deadline wrapper self-check finishes within its observation budget"
fi
require_equal "$BACKGROUND_WAIT_STATUS" "124" "deadline wrapper reports timeout as a distinct nonzero status"

pg_admin() {
    docker exec -e PGPASSWORD="$ADMIN_PASSWORD" "$POSTGRES_NAME" \
        psql -X -v ON_ERROR_STOP=1 -U "$ADMIN_USER" -d "$DATABASE_NAME" "$@"
}

pg_as() {
    local role="$1"
    local password="$2"
    shift 2
    docker exec -e PGPASSWORD="$password" "$POSTGRES_NAME" \
        psql -X -v ON_ERROR_STOP=1 -U "$role" -d "$DATABASE_NAME" "$@"
}

postgres_ready() {
    local raw="$RAW_ROOT/postgres-readiness.txt"
    local ready=0
    if docker exec -e PGPASSWORD="$ADMIN_PASSWORD" "$POSTGRES_NAME" \
        pg_isready -U "$ADMIN_USER" -d "$DATABASE_NAME" > "$raw" 2>&1 \
        && pg_admin -Atc 'SELECT 1' >> "$raw" 2>&1; then
        ready=1
    fi
    sanitize_stream < "$raw" > "$EVIDENCE_ROOT/postgres-readiness.txt"
    ((ready))
}

prepare_case_dirs() {
    local case_name="$1"
    local include_settings="$2"
    local root="$WORK_ROOT/cases/$case_name"
    mkdir -p "$root/config" "$root/data"
    if [[ "$include_settings" == "yes" ]]; then
        cp "$SETTINGS_FIXTURE" "$root/config/settings.json"
    fi

    if [[ "$(id -u)" == "0" ]]; then
        chown -R "$IMAGE_UID:$IMAGE_GID" "$root/config" "$root/data"
    elif sudo -n chown -R "$IMAGE_UID:$IMAGE_GID" "$root/config" "$root/data" >/dev/null 2>&1; then
        :
    else
        fail "prepare $case_name bind mounts for image UID/GID $IMAGE_UID:$IMAGE_GID (root or passwordless sudo is required)"
    fi
    if [[ "$(id -u)" == "0" ]]; then
        chmod 0750 "$root/config" "$root/data"
    else
        sudo -n chmod 0750 "$root/config" "$root/data"
    fi
    printf '%s' "$root"
}

file_sha256() {
    run_root sha256sum "$1" | awk '{print $1}'
}

sqlite_scalar() {
    local database="$1"
    local sql="$2"
    run_root timeout 10 sqlite3 "file:$database?mode=ro" "$sql"
}

require_mount_owner() {
    local path="$1"
    local assertion="$2"
    local owner
    owner="$(run_root stat -c '%u:%g' "$path")"
    require_equal "$owner" "$IMAGE_UID:$IMAGE_GID" "$assertion"
}

create_app_container() {
    local case_name="$1"
    local mode="$2"
    local role="$3"
    local password="$4"
    local publish_http="$5"
    local case_root="$6"
    shift 6
    local name="$RUN_ID-$case_name"
    local args=(create --name "$name" --label "$LABEL_FILTER" --label "$LABEL_KEY.case=$case_name"
        --network "$NETWORK_NAME" --mount "type=bind,src=$case_root/config,dst=/config"
        --mount "type=bind,src=$case_root/data,dst=/data")
    if [[ -n "$role" ]]; then
        args+=(--env "DB_HOST=$POSTGRES_NAME" --env DB_PORT=5432 --env "DB_USERNAME=$role"
            --env "DB_PASSWORD=$password" --env "DB_DATABASE_NAME=$DATABASE_NAME")
    fi
    if [[ -n "$mode" ]]; then
        args+=(--env "IMMICH_REVERSEGEO_MODE=$mode")
    fi
    if [[ "$publish_http" == "yes" ]]; then
        args+=(--network "$PUBLIC_NETWORK_NAME" --publish 127.0.0.1::8080)
    fi
    if [[ "$case_name" == "private" ]]; then
        args+=(--interactive)
    fi
    args+=("$IMAGE_ID")
    args+=("$@")
    register_container "$name"
    docker "${args[@]}" >/dev/null
    assert_common_container "$name" "$case_name" "$mode" "$publish_http" "$case_root"
    CREATED_CONTAINER="$name"
}

assert_common_container() {
    local name="$1"
    local case_name="$2"
    local mode="$3"
    local publish_http="$4"
    local case_root="$5"
    local actual_image actual_user actual_workdir mounts socket_count mode_count bindings network_count
    actual_image="$(docker inspect --format '{{.Image}}' "$name")"
    require_equal "$actual_image" "$IMAGE_ID" "$case_name uses immutable image ID"
    actual_user="$(docker inspect --format '{{.Config.User}}' "$name")"
    require_equal "$actual_user" "$(docker image inspect --format '{{.Config.User}}' "$IMAGE_ID")" "$case_name does not override image user"
    actual_workdir="$(docker inspect --format '{{.Config.WorkingDir}}' "$name")"
    require_equal "$actual_workdir" "/app" "$case_name keeps /app working directory"
    require_equal "$(docker inspect --format '{{json .Config.Entrypoint}}' "$name")" '["dotnet","ImmichReverseGeo.Web.dll"]' "$case_name keeps production entrypoint"
    mounts="$(docker inspect --format '{{range .Mounts}}{{println .Source "|" .Destination}}{{end}}' "$name")"
    if ! grep -Fq "$case_root/config | /config" <<< "$mounts" || ! grep -Fq "$case_root/data | /data" <<< "$mounts"; then
        fail "$case_name has exact isolated config/data bind mounts"
    fi
    pass "$case_name has exact isolated config/data bind mounts"
    socket_count="$(docker inspect "$name" | jq '[.[0].Mounts[]? | select(.Destination=="/var/run/docker.sock")] | length')"
    require_equal "$socket_count" "0" "$case_name has no Docker socket mount"
    mode_count="$(docker inspect "$name" | jq --arg prefix 'IMMICH_REVERSEGEO_MODE=' '[.[0].Config.Env[] | select(startswith($prefix))] | length')"
    if [[ -z "$mode" ]]; then
        require_equal "$mode_count" "0" "$case_name omits deployment-mode environment"
    else
        require_equal "$mode_count" "1" "$case_name has one explicit deployment-mode environment"
        require_equal "$(docker inspect "$name" | jq --arg expected "IMMICH_REVERSEGEO_MODE=$mode" 'any(.[0].Config.Env[]; . == $expected)')" "true" "$case_name uses its exact deployment-mode value"
    fi
    bindings="$(docker inspect "$name" | jq -c '.[0].HostConfig.PortBindings // {}')"
    if [[ "$publish_http" == "yes" ]]; then
        require_equal "$(jq 'keys == ["8080/tcp"]' <<< "$bindings")" "true" "$case_name publishes only container port 8080"
        require_equal "$(jq -r '.["8080/tcp"][0].HostIp' <<< "$bindings")" "127.0.0.1" "$case_name binds HTTP only to loopback"
    else
        require_equal "$bindings" "{}" "$case_name publishes no ports"
    fi
    if [[ "$case_name" == "private" ]]; then
        require_equal "$(docker inspect --format '{{.Config.OpenStdin}}' "$name")" "true" "private probe keeps controlled stdin open from create time"
    else
        require_equal "$(docker inspect --format '{{.Config.OpenStdin}}' "$name")" "false" "$case_name does not keep stdin open"
    fi
    network_count="$(docker inspect "$name" | jq '.[0].NetworkSettings.Networks | length')"
    if [[ "$publish_http" == "yes" ]]; then
        require_equal "$network_count" "2" "$case_name is dual-homed only for loopback Web publication"
        require_equal "$(docker inspect "$name" | jq --arg private "$NETWORK_NAME" --arg public "$PUBLIC_NETWORK_NAME" '((.[0].NetworkSettings.Networks | keys | sort) == ([$private, $public] | sort))')" "true" "$case_name has exact internal and Web publication networks"
    else
        require_equal "$network_count" "1" "$case_name remains only on the internal network"
        require_equal "$(docker inspect "$name" | jq --arg private "$NETWORK_NAME" '.[0].NetworkSettings.Networks | has($private)')" "true" "$case_name has the exact internal network"
    fi
}

container_ip() {
    docker inspect "$1" | jq -r --arg network "$NETWORK_NAME" '.[0].NetworkSettings.Networks[$network].IPAddress'
}

http_port() {
    docker port "$1" 8080/tcp | awk -F: 'END {print $NF}'
}

fetch_page() {
    local url="$1"
    local destination="$2"
    local raw="$RAW_ROOT/${destination##*/}.$$.raw"
    if curl --fail --silent --show-error --connect-timeout 2 --max-time 5 "$url" > "$raw" 2>&1 \
        && sanitize_stream < "$raw" > "$destination.tmp"; then
        mv "$destination.tmp" "$destination"
        rm -f "$raw"
        return 0
    fi
    rm -f "$raw" "$destination.tmp"
    return 1
}

host_tcp_open() {
    local host="$1"
    local port="$2"
    timeout 3 bash -c 'exec 7<>/dev/tcp/$1/$2' _ "$host" "$port" >/dev/null 2>&1
}

assert_no_http_listener() {
    local name="$1"
    local case_name="$2"
    local ip
    ip="$(container_ip "$name")"
    if [[ -z "$ip" || "$ip" == "null" ]]; then
        fail "$case_name has a bridge-network address"
    fi
    if host_tcp_open "$ip" 8080; then
        fail "$case_name has no listener on container IP port 8080"
    fi
    pass "$case_name has no listener on container IP port 8080"
}

process_absent() {
    [[ ! -e "/proc/$1/status" ]]
}

process_live() {
    local pid="$1"
    local state
    [[ -r "/proc/$pid/status" ]] || return 1
    state="$(awk '/^State:/ {print $2}' "/proc/$pid/status")"
    [[ -n "$state" && "$state" != "Z" && "$state" != "X" && "$state" != "x" ]]
}

stop_gracefully() {
    local name="$1"
    local case_name="$2"
    if ! docker stop --time 15 "$name" >/dev/null; then
        fail "$case_name stops within the bounded graceful deadline"
    fi
    require_equal "$(docker inspect --format '{{.State.OOMKilled}}' "$name")" "false" "$case_name is not OOM-killed"
    if [[ "$(docker inspect --format '{{.State.ExitCode}}' "$name")" == "137" ]]; then
        fail "$case_name stops without Docker SIGKILL fallback"
    fi
    pass "$case_name stops without Docker SIGKILL fallback"
}

first_file() {
    run_root find "$1" -type f -print -quit
}

assert_single_app_identity() {
    local name="$1"
    local case_name="$2"
    local expected="$3"
    local destination="$4"
    capture_checked "$destination" docker top "$name" -eo pid,ppid,uid,gid,args \
        || fail "$case_name process snapshot succeeds"
    require_equal "$(awk -v marker="$expected" 'NR>1 && index($0, marker) {count++} END {print count+0}' "$destination")" "1" "$case_name has exactly one expected app process"
    local uid gid
    read -r uid gid < <(awk -v marker="$expected" 'NR>1 && index($0, marker) {print $3, $4; exit}' "$destination")
    require_equal "$uid" "$IMAGE_UID" "$case_name process uses image UID"
    require_equal "$gid" "$IMAGE_GID" "$case_name process uses image GID"
    if [[ "$uid" == "0" || "$gid" == "0" || ! "$uid" =~ ^[0-9]+$ || ! "$gid" =~ ^[0-9]+$ ]]; then
        fail "$case_name process has numeric non-root UID/GID"
    fi
    pass "$case_name process has numeric non-root UID/GID"
}

start_gate_backend() {
    local application="$1"
    local sql="$2"
    local output="$RAW_ROOT/$application.txt"
    GATE_APPLICATIONS+=("$application")
    docker exec -e PGPASSWORD="$ADMIN_PASSWORD" -e PGAPPNAME="$application" "$POSTGRES_NAME" \
        psql -X -v ON_ERROR_STOP=1 -U "$ADMIN_USER" -d "$DATABASE_NAME" -c "$sql" \
        > "$output" 2>&1 &
    BACKGROUND_PIDS+=("$!")
}

gate_has_lock() {
    local application="$1"
    local lock_key="$2"
    [[ "$(pg_admin -Atc "SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE l.locktype='advisory' AND l.granted AND a.application_name='${application}' AND l.classid=(((${lock_key})::bigint >> 32) & 4294967295)::oid AND l.objid=((${lock_key})::bigint & 4294967295)::oid AND l.objsubid=1")" == "1" ]]
}

gate_has_relation_lock() {
    local application="$1"
    [[ "$(pg_admin -Atc "SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='${application}' AND l.relation='asset'::regclass AND l.mode='AccessExclusiveLock' AND l.granted")" == "1" ]]
}

terminate_gate() {
    local application="$1"
    local terminated
    terminated="$(pg_admin -Atc "SELECT count(*) FROM (SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname=current_database() AND application_name='${application}' AND pid<>pg_backend_pid()) AS terminated")"
    require_equal "$terminated" "1" "terminate exactly one owned gate backend $application"
}

insert_ocean_asset() {
    local asset_id="$1"
    pg_admin -c "TRUNCATE asset_exif, asset; INSERT INTO asset(id, \"createdAt\", \"deletedAt\") VALUES ('${asset_id}', clock_timestamp(), NULL); INSERT INTO asset_exif(\"assetId\", latitude, longitude, city, state, country) VALUES ('${asset_id}', 0.0, 0.0, NULL, NULL, NULL);" >/dev/null
}

reset_empty() {
    pg_admin -c "UPDATE smoke_control SET hold_worker=false WHERE singleton; TRUNCATE asset_exif, asset;" >/dev/null
}

standard_worker_is_blocked() {
    local row
    row="$(pg_admin -Atc "WITH production AS (SELECT l.pid FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE l.locktype='advisory' AND l.database=(SELECT oid FROM pg_database WHERE datname=current_database()) AND l.classid=2439209123::oid AND l.objid=4161460176::oid AND l.objsubid=1 AND l.granted AND a.usename='${STANDARD_ROLE}'), blocked AS (SELECT pid FROM pg_stat_activity WHERE datname=current_database() AND usename='${STANDARD_ROLE}' AND application_name='${STANDARD_APP}' AND state='active' AND wait_event_type='Lock' AND wait_event='advisory' AND query LIKE '%SELECT COUNT(*) FROM asset a%') SELECT production.pid || '|' || blocked.pid FROM production CROSS JOIN blocked WHERE production.pid<>blocked.pid")"
    [[ "$row" =~ ^[0-9]+\|[0-9]+$ ]]
}

standard_s1_child_reaped() {
    local destination="$EVIDENCE_ROOT/standard-s1-reap-top.txt"
    if ! capture_checked "$destination" docker top "$standard_name" -eo pid,ppid,uid,gid,args; then
        return 1
    fi
    ! grep -Fq -- '--internal-worker' "$destination"
}

validate_live_standard_tree() {
    local name="$1"
    local destination="$2"
    capture_checked "$destination" docker top "$name" -eo pid,ppid,uid,gid,args \
        || fail "Standard S2 process snapshot succeeds"
    local child_count app_count
    child_count="$(awk 'NR>1 && /--internal-worker/ {count++} END {print count+0}' "$destination")"
    app_count="$(awk 'NR>1 && /ImmichReverseGeo.Web.dll/ {count++} END {print count+0}' "$destination")"
    require_equal "$child_count" "1" "Standard S2 has exactly one live private child"
    require_equal "$app_count" "2" "Standard S2 has exactly parent and child app processes"
    local child_pid child_ppid child_uid child_gid parent_pid parent_uid parent_gid
    read -r child_pid child_ppid child_uid child_gid < <(awk 'NR>1 && /--internal-worker/ {print $1, $2, $3, $4; exit}' "$destination")
    read -r parent_pid parent_uid parent_gid < <(awk 'NR>1 && /ImmichReverseGeo.Web.dll/ && !/--internal-worker/ {print $1, $3, $4; exit}' "$destination")
    require_equal "$child_ppid" "$parent_pid" "Standard S2 child PPID is the Web parent PID"
    require_equal "$child_uid" "$parent_uid" "Standard S2 parent and child have the same UID"
    require_equal "$child_gid" "$parent_gid" "Standard S2 parent and child have the same GID"
    require_equal "$child_uid" "$IMAGE_UID" "Standard S2 processes use image UID"
    require_equal "$child_gid" "$IMAGE_GID" "Standard S2 processes use image GID"
    if [[ "$child_uid" == "0" || "$child_gid" == "0" || ! "$child_uid" =~ ^[0-9]+$ || ! "$child_gid" =~ ^[0-9]+$ ]]; then
        fail "Standard S2 parent and child use numeric non-root UID/GID"
    fi
    pass "Standard S2 parent and child use numeric non-root UID/GID"
    require_contains "$destination" "/app/ImmichReverseGeo.Web.dll --internal-worker" "Standard S2 child uses packaged /app command"
    STANDARD_PARENT_HOST_PID="$parent_pid"
    STANDARD_CHILD_HOST_PID="$child_pid"
}

standard_trace_ready() {
    local kind tid tracer observed_tracer=""
    while IFS='|' read -r kind tid; do
        [[ -n "$kind" && -n "$tid" && -r "/proc/$tid/status" ]] || return 1
        tracer="$(awk '/^TracerPid:/ {print $2}' "/proc/$tid/status")"
        [[ "$tracer" =~ ^[1-9][0-9]*$ ]] || return 1
        if [[ -z "$observed_tracer" ]]; then
            observed_tracer="$tracer"
        elif [[ "$tracer" != "$observed_tracer" ]]; then
            return 1
        fi
    done < "$STANDARD_PARENT_TIDS_FILE"
    STANDARD_TRACER_OS_PID="$observed_tracer"
}

start_standard_reap_trace() {
    local mapping_raw="$RAW_ROOT/standard-s2-pid-mapping.txt"
    local parent_tgid child_ppid
    parent_tgid="$(awk '/^Tgid:/ {print $2}' "/proc/$STANDARD_PARENT_HOST_PID/status")"
    child_ppid="$(awk '/^PPid:/ {print $2}' "/proc/$STANDARD_CHILD_HOST_PID/status")"
    STANDARD_CHILD_NAMESPACE_PID="$(awk '/^NSpid:/ {print $NF}' "/proc/$STANDARD_CHILD_HOST_PID/status")"
    require_equal "$parent_tgid" "$STANDARD_PARENT_HOST_PID" "Standard S2 trace target is the parent thread group leader"
    require_equal "$child_ppid" "$STANDARD_PARENT_HOST_PID" "Standard S2 trace child still belongs to the parent before attach"
    if [[ ! "$STANDARD_CHILD_NAMESPACE_PID" =~ ^[1-9][0-9]*$ ]]; then
        fail "Standard S2 resolves the child namespace PID"
    fi

    STANDARD_PARENT_TIDS_FILE="$RAW_ROOT/standard-s2-trace-tids.txt"
    : > "$STANDARD_PARENT_TIDS_FILE"
    local status_path tid
    for status_path in /proc/"$STANDARD_PARENT_HOST_PID"/task/*/status; do
        tid="$(awk '/^Pid:/ {print $2}' "$status_path")"
        printf 'parent|%s\n' "$tid" >> "$STANDARD_PARENT_TIDS_FILE"
    done
    for status_path in /proc/"$STANDARD_CHILD_HOST_PID"/task/*/status; do
        tid="$(awk '/^Pid:/ {print $2}' "$status_path")"
        printf 'child|%s\n' "$tid" >> "$STANDARD_PARENT_TIDS_FILE"
    done
    printf 'parent_host_pid=%s\nchild_host_pid=%s\nchild_namespace_pid=%s\n' \
        "$STANDARD_PARENT_HOST_PID" "$STANDARD_CHILD_HOST_PID" "$STANDARD_CHILD_NAMESPACE_PID" > "$mapping_raw"

    STANDARD_TRACE_PREFIX="$RAW_ROOT/standard-s2-trace"
    run_root "$STRACE_BIN" -ff -q -ttt -T -e trace=wait4,waitid,exit,exit_group \
        -o "$STANDARD_TRACE_PREFIX" -p "$STANDARD_PARENT_HOST_PID" -p "$STANDARD_CHILD_HOST_PID" \
        2> "$RAW_ROOT/standard-s2-strace-stderr.txt" &
    STANDARD_TRACE_PID=$!
    BACKGROUND_PIDS+=("$STANDARD_TRACE_PID")
    wait_until 5 "Standard S2 tracer attaches to every observed parent and child thread" standard_trace_ready
    printf 'tracer_os_pid=%s\n' "$STANDARD_TRACER_OS_PID" >> "$mapping_raw"
    if ! process_live "$STANDARD_PARENT_HOST_PID" || ! process_live "$STANDARD_CHILD_HOST_PID"; then
        fail "Standard S2 parent and child remain live after trace readiness"
    fi
    capture_checked "$EVIDENCE_ROOT/standard-s2-trace-ready-process-tree.txt" \
        docker top "$standard_name" -eo pid,ppid,uid,gid,args \
        || fail "Standard S2 captures trace-ready process liveness"
    require_contains "$EVIDENCE_ROOT/standard-s2-trace-ready-process-tree.txt" \
        "$STANDARD_CHILD_HOST_PID" "Standard S2 child is live after trace readiness and before parent stop"
    sanitize_stream < "$mapping_raw" > "$EVIDENCE_ROOT/standard-s2-pid-mapping.txt"
    sanitize_stream < "$STANDARD_PARENT_TIDS_FILE" > "$EVIDENCE_ROOT/standard-s2-trace-tids.txt"
}

retain_standard_trace_evidence() {
    local trace_file retained_dir="$EVIDENCE_ROOT/standard-s2-traces"
    mkdir -p "$retained_dir"
    for trace_file in "$STANDARD_TRACE_PREFIX".*; do
        [[ -f "$trace_file" ]] || continue
        sanitize_stream < "$trace_file" > "$retained_dir/${trace_file##*/}"
    done
    if [[ -f "$RAW_ROOT/standard-s2-strace-stderr.txt" ]]; then
        sanitize_stream < "$RAW_ROOT/standard-s2-strace-stderr.txt" > "$retained_dir/strace-stderr.txt"
    fi
}

assert_standard_reap_trace() {
    if ! await_background "$STANDARD_TRACE_PID" 10; then
        fail "Standard S2 tracer exits after the traced process tree"
    fi
    require_equal "$BACKGROUND_WAIT_STATUS" "0" "Standard S2 tracer exits successfully"
    retain_standard_trace_evidence

    local tid trace_file wait_line="" wait_tid=""
    while IFS='|' read -r kind tid; do
        [[ "$kind" == "parent" ]] || continue
        trace_file="$STANDARD_TRACE_PREFIX.$tid"
        [[ -r "$trace_file" ]] || continue
        wait_line="$(grep -E "wait4\\($STANDARD_CHILD_NAMESPACE_PID,.*\\)[[:space:]]*=[[:space:]]*$STANDARD_CHILD_NAMESPACE_PID([[:space:]]|$)" "$trace_file" | tail -n 1 || true)"
        if [[ -n "$wait_line" ]]; then
            wait_tid="$tid"
            break
        fi
    done < "$STANDARD_PARENT_TIDS_FILE"
    if [[ -z "$wait_line" ]]; then
        fail "Standard S2 parent thread records successful wait4 for the mapped child PID"
    fi
    if grep -Fq 'WEXITSTATUS(s) == 5' <<< "$wait_line"; then
        fail "Standard S2 traced child terminates from parent shutdown rather than fixture lock timeout"
    fi

    local parent_exit_line wait_time exit_time
    parent_exit_line="$(grep -E '^([0-9]+\.)?[0-9]+ \+\+\+ exited with ' "$STANDARD_TRACE_PREFIX.$STANDARD_PARENT_HOST_PID" | tail -n 1 || true)"
    if [[ -z "$parent_exit_line" ]]; then
        fail "Standard S2 trace records parent leader exit"
    fi
    wait_time="${wait_line%% *}"
    exit_time="${parent_exit_line%% *}"
    if ! awk -v waited="$wait_time" -v exited="$exit_time" 'BEGIN { exit !(waited < exited) }'; then
        fail "Standard S2 successful child wait4 precedes parent leader exit"
    fi
    {
        printf 'parent_host_pid=%s\nchild_host_pid=%s\nchild_namespace_pid=%s\nreaper_parent_tid=%s\n' \
            "$STANDARD_PARENT_HOST_PID" "$STANDARD_CHILD_HOST_PID" "$STANDARD_CHILD_NAMESPACE_PID" "$wait_tid"
        printf 'wait4=%s\nparent_exit=%s\n' "$wait_line" "$parent_exit_line"
    } | sanitize_stream > "$EVIDENCE_ROOT/standard-s2-reap-trace.txt"
    pass "Standard S2 parent successfully wait4-reaps its mapped live child before exiting"
    pass "Standard S2 child outcome differs from the fixture lock-timeout spike"
}

assert_no_downloads() {
    local data_root="$1"
    local logs_file="$2"
    if run_root find "$data_root" -type f \( -path '*/overture-divisions/*.db' -o -path '*/gadm-divisions/*.db' \) -print -quit | grep -q .; then
        fail "no live per-country geodata cache files"
    fi
    pass "no live per-country geodata cache files"
    if grep -Eiq '(Downloading|Waiting for) (in-flight )?(Overture|GADM) administrative cache|Starting (Overture|GADM) administrative cache download' "$logs_file"; then
        fail "no live geodata download markers"
    fi
    pass "no live geodata download markers"
}

server_os="$(docker info --format '{{.OSType}}' 2>/dev/null || true)"
require_equal "$server_os" "linux" "Docker server reports Linux"
server_arch="$(docker info --format '{{.Architecture}}')"
case "$(uname -m)" in
    x86_64) host_arch="x86_64" ;;
    aarch64|arm64) host_arch="aarch64" ;;
    *) fail "supported native host architecture" ;;
esac
case "$server_arch" in
    x86_64|amd64) normalized_server_arch="x86_64" ;;
    aarch64|arm64) normalized_server_arch="aarch64" ;;
    *) fail "supported Docker server architecture" ;;
esac
require_equal "$normalized_server_arch" "$host_arch" "Docker server architecture matches native Linux host"

if [[ -n "$PREBUILT_IMAGE" ]]; then
    IMAGE_ID="$(docker image inspect --format '{{.Id}}' "$PREBUILT_IMAGE" 2>/dev/null || true)"
    if [[ -z "$IMAGE_ID" ]]; then
        fail "explicit prebuilt image exists"
    fi
    pass "explicit prebuilt image resolved once"
else
    DOCKER_COMMAND_TIMEOUT=1800 capture_sanitized "$EVIDENCE_ROOT/docker-build.log" docker build \
        --file src/ImmichReverseGeo.Web/Dockerfile --tag "$IMAGE_TAG" .
    IMAGE_ID="$(docker image inspect --format '{{.Id}}' "$IMAGE_TAG" 2>/dev/null || true)"
    if [[ -z "$IMAGE_ID" ]]; then
        fail "production Docker image builds once"
    fi
    BUILT_IMAGE=1
    pass "production Docker image builds once"
fi

require_equal "$(docker image inspect --format '{{.Architecture}}' "$IMAGE_ID" | sed 's/^amd64$/x86_64/; s/^arm64$/aarch64/')" "$host_arch" "image architecture matches native Linux host"
require_equal "$(docker image inspect --format '{{.Config.WorkingDir}}' "$IMAGE_ID")" "/app" "image working directory is /app"
require_equal "$(docker image inspect --format '{{json .Config.Entrypoint}}' "$IMAGE_ID")" '["dotnet","ImmichReverseGeo.Web.dll"]' "image retains production entrypoint"
require_equal "$(docker image inspect "$IMAGE_ID" | jq '[.[0].Config.Env[] | select(startswith("IMMICH_REVERSEGEO_MODE="))] | length')" "0" "image has no baked deployment mode"
image_user="$(docker image inspect --format '{{.Config.User}}' "$IMAGE_ID")"
if [[ ! "$image_user" =~ ^[1-9][0-9]*(:[1-9][0-9]*)?$ ]]; then
    fail "image Config.User is numeric and non-root"
fi
pass "image Config.User is numeric and non-root"
IMAGE_UID="${image_user%%:*}"

packaging_name="$RUN_ID-packaging"
register_container "$packaging_name"
docker create --name "$packaging_name" --label "$LABEL_FILTER" --label "$LABEL_KEY.case=packaging" "$IMAGE_ID" >/dev/null
if [[ "${DOCKER_SMOKE_INJECT_FAILURE:-}" == "after-packaging-create" ]]; then
    fail "intentional failure after daemon accepted the owned packaging container"
elif [[ "${DOCKER_SMOKE_INJECT_FAILURE:-}" == "retained-secret" ]]; then
    if [[ -z "${DOCKER_SMOKE_REDACTION_SELF_CHECK_CANARY:-}" ]]; then
        fail "retained-secret injection requires its dedicated canary"
    fi
    printf 'simulated-captured-secret=%s\n' "$DOCKER_SMOKE_REDACTION_SELF_CHECK_CANARY" > "$EVIDENCE_ROOT/redaction-self-check.txt"
    fail "intentional retained-evidence redaction self-check"
elif [[ -n "${DOCKER_SMOKE_INJECT_FAILURE:-}" && "${DOCKER_SMOKE_INJECT_FAILURE:-}" != "after-postgres" ]]; then
    fail "unknown DOCKER_SMOKE_INJECT_FAILURE value"
fi
mkdir -p "$WORK_ROOT/packaging"
docker cp "$packaging_name:/app/bundled-data/." "$WORK_ROOT/packaging/"
docker cp "$packaging_name:/etc/passwd" "$WORK_ROOT/packaging/passwd"
IMAGE_GID="$(awk -F: -v uid="$IMAGE_UID" '$3 == uid {print $4; exit}' "$WORK_ROOT/packaging/passwd")"
if [[ ! "$IMAGE_GID" =~ ^[1-9][0-9]*$ ]]; then
    fail "image runtime GID is numeric and non-root"
fi
pass "image runtime GID is numeric and non-root"
for bundled in \
    iso3166.json \
    defaults/city-resolver-profiles.json \
    defaults/OVERTURE-ATTRIBUTION.txt \
    defaults/overture-airports.db \
    defaults/overture-country-divisions.db; do
    if [[ ! -r "$WORK_ROOT/packaging/$bundled" || ! -s "$WORK_ROOT/packaging/$bundled" ]]; then
        fail "bundled artifact is readable and nonempty: $bundled"
    fi
    pass "bundled artifact is readable and nonempty: $bundled"
done
require_equal "$(sqlite_scalar "$WORK_ROOT/packaging/defaults/overture-airports.db" 'PRAGMA quick_check;')" "ok" "bundled airport SQLite passes quick_check"
require_equal "$(sqlite_scalar "$WORK_ROOT/packaging/defaults/overture-country-divisions.db" 'PRAGMA quick_check;')" "ok" "bundled country SQLite passes quick_check"

docker network create --internal --label "$LABEL_FILTER" "$NETWORK_NAME" >/dev/null
pass "run-unique internal bridge network created"
docker network create --label "$LABEL_FILTER" "$PUBLIC_NETWORK_NAME" >/dev/null
pass "run-unique Web publication bridge created"
docker volume create --label "$LABEL_FILTER" "$POSTGRES_VOLUME_NAME" >/dev/null
pass "run-unique PostgreSQL data volume created"
DOCKER_COMMAND_TIMEOUT=900 capture_sanitized "$EVIDENCE_ROOT/postgres-pull.log" docker pull "$POSTGRES_IMAGE"
if ! docker image inspect "$POSTGRES_IMAGE" >/dev/null 2>&1; then
    fail "pinned PostgreSQL image is available"
fi
pass "pinned PostgreSQL image is available"
register_container "$POSTGRES_NAME"
docker run --detach --name "$POSTGRES_NAME" --label "$LABEL_FILTER" --label "$LABEL_KEY.case=postgres" \
    --network "$NETWORK_NAME" --mount "type=volume,src=$POSTGRES_VOLUME_NAME,dst=/var/lib/postgresql/data" \
    --env "POSTGRES_USER=$ADMIN_USER" --env "POSTGRES_PASSWORD=$ADMIN_PASSWORD" \
    --env "POSTGRES_DB=$DATABASE_NAME" "$POSTGRES_IMAGE" \
    -c log_statement=all -c log_connections=on -c log_disconnections=on \
    -c "log_line_prefix=%m [%p] user=%u,db=%d,app=%a " >/dev/null
require_equal "$(docker inspect "$POSTGRES_NAME" | jq -c '.[0].HostConfig.PortBindings // {}')" "{}" "PostgreSQL publishes no host port"
require_equal "$(docker inspect "$POSTGRES_NAME" | jq '.[0].NetworkSettings.Networks | length')" "1" "PostgreSQL has exactly one network"
require_equal "$(docker inspect "$POSTGRES_NAME" | jq --arg private "$NETWORK_NAME" '.[0].NetworkSettings.Networks | has($private)')" "true" "PostgreSQL remains only on the internal network"
wait_until 60 "PostgreSQL passes active readiness" postgres_ready
POSTGRES_READY=1

fixture_status=0
docker exec -i -e PGPASSWORD="$ADMIN_PASSWORD" "$POSTGRES_NAME" \
    psql -X -v ON_ERROR_STOP=1 -U "$ADMIN_USER" -d "$DATABASE_NAME" \
    -v "database_name=$DATABASE_NAME" \
    -v "standard_role=$STANDARD_ROLE" -v "standard_password=$STANDARD_PASSWORD" -v "standard_application_name=$STANDARD_APP" \
    -v "webonly_role=$WEBONLY_ROLE" -v "webonly_password=$WEBONLY_PASSWORD" -v "webonly_application_name=$WEBONLY_APP" \
    -v "runonce_role=$RUNONCE_ROLE" -v "runonce_password=$RUNONCE_PASSWORD" -v "runonce_application_name=$RUNONCE_APP" \
    < "$FIXTURE_SQL" > "$RAW_ROOT/fixture-bootstrap.txt" 2>&1 || fixture_status=$?
sanitize_stream < "$RAW_ROOT/fixture-bootstrap.txt" > "$EVIDENCE_ROOT/fixture-bootstrap.txt"
require_equal "$fixture_status" "0" "fixture bootstrap exits successfully"
pass "minimal fixture schema and scoped roles applied"

role_scope="$(pg_admin -Atc "SELECT (NOT rolsuper AND NOT rolcreaterole AND NOT rolcreatedb AND rolname <> (SELECT tableowner FROM pg_tables WHERE schemaname='public' AND tablename='asset')) FROM pg_roles WHERE rolname='${STANDARD_ROLE}'")"
require_equal "$role_scope" "t" "Standard app role is non-superuser, non-owner, and unprivileged"
require_equal "$(pg_admin -Atc "SELECT relrowsecurity || '|' || relforcerowsecurity FROM pg_class WHERE oid='asset'::regclass")" "true|true" "asset fixture has enabled FORCE RLS"
require_equal "$(pg_as "$STANDARD_ROLE" "$STANDARD_PASSWORD" -Atc 'SHOW lock_timeout')" "20s" "fixture lock timeout is finite and below Npgsql default command timeout"

postgres_ip="$(container_ip "$POSTGRES_NAME")"
wait_until 10 "native host reaches PostgreSQL container IP" host_tcp_open "$postgres_ip" 5432

if [[ "${DOCKER_SMOKE_INJECT_FAILURE:-}" == "after-postgres" ]]; then
    fail "intentional failure after PostgreSQL readiness"
elif [[ -n "${DOCKER_SMOKE_INJECT_FAILURE:-}" ]]; then
    fail "unknown DOCKER_SMOKE_INJECT_FAILURE value"
fi

# Standard: one absent-mode container, two complementary scheduled phases.
reset_empty
insert_ocean_asset "$S1_ASSET_ID"
standard_root="$(prepare_case_dirs standard yes)"
standard_config_hash="$(file_sha256 "$standard_root/config/settings.json")"
standard_log_baseline="$(docker logs "$POSTGRES_NAME" 2>&1 | wc -l | tr -d ' ')"
create_app_container standard '' "$STANDARD_ROLE" "$STANDARD_PASSWORD" yes "$standard_root"
standard_name="$CREATED_CONTAINER"
docker start "$standard_name" >/dev/null
standard_port="$(http_port "$standard_name")"
wait_until 45 "Standard HTTP SSR becomes ready" fetch_page "http://127.0.0.1:$standard_port/" "$EVIDENCE_ROOT/standard-dashboard-start.html"
require_contains "$EVIDENCE_ROOT/standard-dashboard-start.html" "Standard" "Standard SSR reports Standard mode"
require_contains "$EVIDENCE_ROOT/standard-dashboard-start.html" "Available" "Standard SSR reports scheduling available"
standard_key="$(first_file "$standard_root/config/dataprotection-keys" || true)"
if [[ -z "$standard_key" ]]; then
    fail "Standard creates DataProtection key material"
fi
pass "Standard creates DataProtection key material"
require_mount_owner "$standard_key" "Standard config artifact has image UID/GID"

standard_logs="$EVIDENCE_ROOT/standard-logs-s1.html"
standard_s1_complete() {
    fetch_page "http://127.0.0.1:$standard_port/logs" "$standard_logs" \
        && grep -Fq "Asset $S1_ASSET_ID: no country found at (0.0000, 0.0000), skipping." "$standard_logs" \
        && grep -Fq 'Run complete. Processed=0 Skipped=1 Errors=0' "$standard_logs"
}
wait_until 105 "Standard S1 reaches actual no-country terminal state" standard_s1_complete
STANDARD_ADMISSIONS=$((STANDARD_ADMISSIONS + 1))
require_contains "$standard_logs" "Run started. 1 assets to process." "Standard S1 reports positive worker eligibility"
require_equal "$(pg_admin -Atc "SELECT city IS NULL AND state IS NULL AND country IS NULL FROM asset_exif WHERE \"assetId\"='${S1_ASSET_ID}'")" "t" "Standard S1 leaves no-country Postgres location unchanged"
require_equal "$(sqlite_scalar "$standard_root/data/skipped.db" "SELECT count(*) FROM skipped_assets WHERE asset_id='${S1_ASSET_ID}';")" "1" "Standard S1 persists exactly the ocean asset skip"
require_mount_owner "$standard_root/data/skipped.db" "Standard data artifact has image UID/GID"
assert_no_downloads "$standard_root/data" "$standard_logs"
wait_until 10 "Standard S1 completed child is reaped" standard_s1_child_reaped

# Make S1 ineligible before preparing the next due opportunity.
pg_admin -c "UPDATE asset_exif SET country='SMOKE_DONE' WHERE \"assetId\"='${S1_ASSET_ID}';" >/dev/null
insert_ocean_asset "$S2_ASSET_ID"
pg_admin -c "UPDATE smoke_control SET hold_worker=true WHERE singleton;" >/dev/null
standard_gate_app="irgeo46-standard-gate-$RUN_SUFFIX"
start_gate_backend "$standard_gate_app" "SELECT pg_advisory_lock($FIXTURE_LOCK_KEY); SELECT pg_sleep(300);"
wait_until 15 "Standard S2 fixture advisory gate is held" gate_has_lock "$standard_gate_app" "$FIXTURE_LOCK_KEY"
wait_until 105 "Standard S2 worker count blocks after detector admission" standard_worker_is_blocked
STANDARD_ADMISSIONS=$((STANDARD_ADMISSIONS + 1))
standard_tree="$EVIDENCE_ROOT/standard-s2-process-tree.txt"
validate_live_standard_tree "$standard_name" "$standard_tree"
start_standard_reap_trace
fetch_page "http://127.0.0.1:$standard_port/" "$EVIDENCE_ROOT/standard-dashboard-s2.html" || fail "Standard S2 Dashboard SSR is reachable"
if ! grep -Eq 'Worker: (Starting|Running)' "$EVIDENCE_ROOT/standard-dashboard-s2.html"; then
    fail "Standard S2 SSR reports positive child lifecycle"
fi
pass "Standard S2 SSR reports positive child lifecycle"
capture_sanitized "$EVIDENCE_ROOT/standard-s2-db-evidence.txt" pg_admin -P pager=off -x -c \
    "SELECT pid, usename, application_name, state, wait_event_type, wait_event, query FROM pg_stat_activity WHERE usename='${STANDARD_ROLE}' ORDER BY pid; SELECT pid, classid, objid, objsubid, granted FROM pg_locks WHERE locktype='advisory' AND database=(SELECT oid FROM pg_database WHERE datname=current_database()) ORDER BY pid"
stop_gracefully "$standard_name" "Standard S2 parent"
wait_until 5 "Standard S2 container and child are reaped" container_stopped "$standard_name"
assert_standard_reap_trace
wait_until 5 "Standard S2 child host PID disappears" process_absent "$STANDARD_CHILD_HOST_PID"
terminate_gate "$standard_gate_app"
require_equal "$(file_sha256 "$standard_root/config/settings.json")" "$standard_config_hash" "Standard retains exact saved schedule bytes"
docker logs "$POSTGRES_NAME" 2>&1 | tail -n "+$((standard_log_baseline + 1))" | sanitize_stream > "$EVIDENCE_ROOT/standard-postgres-window.txt"
standard_lock_acquisitions="$(awk -v role="user=$STANDARD_ROLE" 'index($0, role) && index($0, "SELECT pg_try_advisory_lock($1)") {count++} END {print count+0}' "$EVIDENCE_ROOT/standard-postgres-window.txt")"
require_equal "$STANDARD_ADMISSIONS" "2" "Standard observes exactly two complementary admissions"
require_equal "$standard_lock_acquisitions" "2" "Standard has exactly two production-lock child acquisitions"

# Web-only: identical config, two corresponding due opportunities, raw SSR only.
reset_empty
webonly_root="$(prepare_case_dirs webonly yes)"
webonly_config_hash="$(file_sha256 "$webonly_root/config/settings.json")"
require_equal "$webonly_config_hash" "$standard_config_hash" "Web-only and Standard start from identical saved config bytes"
web_log_baseline="$(docker logs "$POSTGRES_NAME" 2>&1 | wc -l | tr -d ' ')"
create_app_container webonly web-only "$WEBONLY_ROLE" "$WEBONLY_PASSWORD" yes "$webonly_root"
webonly_name="$CREATED_CONTAINER"
docker start "$webonly_name" >/dev/null
webonly_port="$(http_port "$webonly_name")"
wait_until 45 "Web-only HTTP SSR becomes ready" fetch_page "http://127.0.0.1:$webonly_port/" "$EVIDENCE_ROOT/webonly-dashboard-start.html"
require_contains "$EVIDENCE_ROOT/webonly-dashboard-start.html" "Web-only" "Web-only SSR reports Web-only mode"
require_contains "$EVIDENCE_ROOT/webonly-dashboard-start.html" "Disabled by Web-only" "Web-only SSR positively reports disabled scheduling"
require_contains "$EVIDENCE_ROOT/webonly-dashboard-start.html" "Saved schedule values are retained" "Web-only SSR reports saved schedule retention"
require_contains "$EVIDENCE_ROOT/webonly-dashboard-start.html" "Worker: Idle" "Web-only SSR reports idle worker"
assert_single_app_identity "$webonly_name" "Web-only" "ImmichReverseGeo.Web.dll" "$EVIDENCE_ROOT/webonly-process-start.txt"
webonly_key="$(first_file "$webonly_root/config/dataprotection-keys" || true)"
if [[ -z "$webonly_key" ]]; then
    fail "Web-only creates DataProtection key material"
fi
pass "Web-only creates DataProtection key material"
require_mount_owner "$webonly_key" "Web-only config artifact has image UID/GID"
fetch_page "http://127.0.0.1:$webonly_port/settings" "$EVIDENCE_ROOT/webonly-settings.html" || fail "Web-only settings SSR is reachable"
require_contains "$EVIDENCE_ROOT/webonly-settings.html" "Web-only mode keeps these schedule settings" "Web-only settings SSR reports retained schedule policy"
require_contains "$EVIDENCE_ROOT/webonly-settings.html" "* * * * *" "Web-only settings SSR renders the saved due cron"

web_start_epoch="$(date -u +%s)"
web_second_due=$(( (web_start_epoch / 60 + 2) * 60 + 5 ))
web_deadline=$((SECONDS + 130))
: > "$RAW_ROOT/webonly-process-samples.txt"
while (( $(date -u +%s) < web_second_due )); do
    if ((SECONDS >= web_deadline)); then
        fail "Web-only crosses two bounded due opportunities"
    fi
    docker top "$webonly_name" -eo pid,ppid,uid,gid,args >> "$RAW_ROOT/webonly-process-samples.txt"
    sleep 2
done
sanitize_stream < "$RAW_ROOT/webonly-process-samples.txt" > "$EVIDENCE_ROOT/webonly-process-samples.txt"
pass "Web-only crosses two bounded due opportunities"
if grep -Fq -- '--internal-worker' "$EVIDENCE_ROOT/webonly-process-samples.txt"; then
    fail "Web-only has no child process across due opportunities"
fi
pass "Web-only has no child process across due opportunities"
fetch_page "http://127.0.0.1:$webonly_port/logs" "$EVIDENCE_ROOT/webonly-logs.html" || fail "Web-only logs SSR is reachable"
for forbidden in 'Next run scheduled at' 'Run started.' 'assets to process.' 'Run complete.'; do
    require_absent "$EVIDENCE_ROOT/webonly-logs.html" "$forbidden" "Web-only logs omit automatic marker: $forbidden"
done
fetch_page "http://127.0.0.1:$webonly_port/" "$EVIDENCE_ROOT/webonly-dashboard-end.html" || fail "Web-only final Dashboard SSR is reachable"
require_contains "$EVIDENCE_ROOT/webonly-dashboard-end.html" "Worker: Idle" "Web-only remains positively idle after due opportunities"
require_equal "$(file_sha256 "$webonly_root/config/settings.json")" "$webonly_config_hash" "Web-only retains exact saved schedule bytes"
require_equal "$(run_root find "$webonly_root/data" -mindepth 1 -print | wc -l | tr -d ' ')" "0" "Web-only creates no processing data artifacts"
require_equal "$(pg_admin -Atc "SELECT count(*) FROM pg_stat_activity WHERE usename='${WEBONLY_ROLE}' OR application_name='${WEBONLY_APP}'")" "0" "Web-only has zero live DB backends"
docker logs "$POSTGRES_NAME" 2>&1 | tail -n "+$((web_log_baseline + 1))" | sanitize_stream > "$EVIDENCE_ROOT/webonly-postgres-window.txt"
require_absent "$EVIDENCE_ROOT/webonly-postgres-window.txt" "user=$WEBONLY_ROLE" "Web-only has zero historical role-scoped DB activity"
require_absent "$EVIDENCE_ROOT/webonly-postgres-window.txt" "app=$WEBONLY_APP" "Web-only has zero historical application-scoped DB activity"
stop_gracefully "$webonly_name" "Web-only"

# Run-once: block the real empty count, prove no listener, then release once.
reset_empty
runonce_gate_app="irgeo46-runonce-gate-$RUN_SUFFIX"
start_gate_backend "$runonce_gate_app" 'BEGIN; LOCK TABLE asset IN ACCESS EXCLUSIVE MODE; SELECT pg_sleep(300); COMMIT;'
wait_until 15 "Run-once relation gate is held" gate_has_relation_lock "$runonce_gate_app"
runonce_root="$(prepare_case_dirs runonce no)"
create_app_container runonce run-once "$RUNONCE_ROLE" "$RUNONCE_PASSWORD" no "$runonce_root"
runonce_name="$CREATED_CONTAINER"
docker start "$runonce_name" >/dev/null
runonce_count_blocked() {
    [[ "$(pg_admin -Atc "SELECT count(*) FROM pg_stat_activity WHERE usename='${RUNONCE_ROLE}' AND application_name='${RUNONCE_APP}' AND state='active' AND wait_event_type='Lock' AND query LIKE '%SELECT COUNT(*) FROM asset a%'")" == "1" ]]
}
wait_until 30 "Run-once real count is blocked by the owned relation gate" runonce_count_blocked
assert_single_app_identity "$runonce_name" "Run-once" "ImmichReverseGeo.Web.dll" "$EVIDENCE_ROOT/runonce-process-held.txt"
assert_no_http_listener "$runonce_name" "Run-once"
terminate_gate "$runonce_gate_app"
wait_until 45 "Run-once reaches one terminal stopped state" container_stopped "$runonce_name"
require_equal "$(docker inspect --format '{{.State.ExitCode}}' "$runonce_name")" "0" "Run-once exits 0"
docker logs "$runonce_name" > "$RAW_ROOT/runonce-stdout.txt" 2> "$RAW_ROOT/runonce-stderr.txt"
sanitize_stream < "$RAW_ROOT/runonce-stdout.txt" > "$EVIDENCE_ROOT/runonce-stdout.txt"
sanitize_stream < "$RAW_ROOT/runonce-stderr.txt" > "$EVIDENCE_ROOT/runonce-stderr.txt"
expected_runonce="$RAW_ROOT/runonce-expected.txt"
printf '%s\n' 'Run started.' 'Eligible assets: 0. Nothing to process.' 'Run completed: processed=0 updated=0 skipped=0 failed=0.' > "$expected_runonce"
if ! cmp -s "$expected_runonce" "$EVIDENCE_ROOT/runonce-stdout.txt"; then
    fail "Run-once emits exact three-line no-work stdout"
fi
pass "Run-once emits exact three-line no-work stdout"
require_equal "$(stat -c '%s' "$EVIDENCE_ROOT/runonce-stderr.txt")" "0" "Run-once successful stderr is empty"
require_equal "$(grep -Fxc 'Run started.' "$EVIDENCE_ROOT/runonce-stdout.txt")" "1" "Run-once reports one started attempt"
require_equal "$(grep -Fxc 'Run completed: processed=0 updated=0 skipped=0 failed=0.' "$EVIDENCE_ROOT/runonce-stdout.txt")" "1" "Run-once reports one terminal attempt"
require_mount_owner "$runonce_root/data/skipped.db" "Run-once data artifact has image UID/GID"

# Invalid public mode: no database dependency and no mount side effects.
invalid_root="$(prepare_case_dirs invalid no)"
create_app_container invalid "$INVALID_MODE" '' '' no "$invalid_root"
invalid_name="$CREATED_CONTAINER"
DOCKER_COMMAND_TIMEOUT=15 docker start --attach "$invalid_name" > "$RAW_ROOT/invalid-stdout.txt" 2> "$RAW_ROOT/invalid-stderr.txt" || invalid_attach_status=$?
invalid_attach_status="${invalid_attach_status:-0}"
if [[ "$invalid_attach_status" == "124" ]]; then
    fail "invalid mode exits before its 15s deadline"
fi
wait_until 3 "invalid mode reaches stopped state" container_stopped "$invalid_name"
require_equal "$(docker inspect --format '{{.State.ExitCode}}' "$invalid_name")" "2" "invalid mode exits 2"
require_equal "$(stat -c '%s' "$RAW_ROOT/invalid-stdout.txt")" "0" "invalid mode stdout is empty"
expected_invalid='invalid-deployment-mode: IMMICH_REVERSEGEO_MODE must be one of: standard, web-only, run-once.'
require_equal "$(tr -d '\r\n' < "$RAW_ROOT/invalid-stderr.txt")" "$expected_invalid" "invalid mode emits the constant accepted-values diagnostic"
require_equal "$(run_root find "$invalid_root/config" "$invalid_root/data" -mindepth 1 -print | wc -l | tr -d ' ')" "0" "invalid mode creates zero mount files"
sanitize_stream < "$RAW_ROOT/invalid-stderr.txt" > "$EVIDENCE_ROOT/invalid-stderr.txt"

# Private protocol: keep stdin open through ready/no-listener observation, then EOF.
private_root="$(prepare_case_dirs private no)"
create_app_container private '' '' '' no "$private_root" --internal-worker
private_name="$CREATED_CONTAINER"
private_fifo="$WORK_ROOT/private-stdin.fifo"
mkfifo "$private_fifo"
DOCKER_COMMAND_TIMEOUT="$PRIVATE_ATTACH_TIMEOUT" docker start --attach --interactive "$private_name" \
    < "$private_fifo" > "$RAW_ROOT/private-stdout.txt" 2> "$RAW_ROOT/private-stderr.txt" &
private_attach_pid=$!
BACKGROUND_PIDS+=("$private_attach_pid")
exec 9> "$private_fifo"
FIFO_WRITER_OPEN=1
private_ready() {
    [[ -s "$RAW_ROOT/private-stdout.txt" ]] \
        && head -n 1 "$RAW_ROOT/private-stdout.txt" | jq -e '
            .protocol == "immich-reversegeo.worker"
            and .version == 1
            and .direction == "worker-to-controller"
            and .category == "lifecycle"
            and .type == "ready"
            and .sequence == 1
            and .runId == null
            and .payload == {}
            and (.timestampUtc | test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\\.[0-9]+)?Z$"))' >/dev/null
}
wait_until "$PRIVATE_READY_TIMEOUT" "private worker emits canonical ready frame" private_ready
private_phase_deadline=$((SECONDS + PRIVATE_OBSERVATION_BUDGET))
require_equal "$(wc -l < "$RAW_ROOT/private-stdout.txt" | tr -d ' ')" "1" "private probe has emitted only the ready frame while held"
require_equal "$(sqlite_scalar "$private_root/data/skipped.db" "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='skipped_assets';")" "1" "private startup initializes the real skipped store before ready"
require_mount_owner "$private_root/data/skipped.db" "private worker data artifact has image UID/GID"
private_top="$EVIDENCE_ROOT/private-process.txt"
capture_checked "$private_top" docker top "$private_name" -eo pid,ppid,uid,gid,args \
    || fail "private worker process snapshot succeeds"
require_contains "$private_top" "ImmichReverseGeo.Web.dll --internal-worker" "private probe uses packaged app command"
private_uid="$(awk 'NR>1 && /--internal-worker/ {print $3; exit}' "$private_top")"
private_gid="$(awk 'NR>1 && /--internal-worker/ {print $4; exit}' "$private_top")"
if [[ "$private_uid" == "0" || "$private_gid" == "0" || ! "$private_uid" =~ ^[0-9]+$ || ! "$private_gid" =~ ^[0-9]+$ ]]; then
    fail "private worker has numeric non-root UID/GID"
fi
pass "private worker has numeric non-root UID/GID"
require_equal "$private_uid" "$IMAGE_UID" "private worker process uses image UID"
require_equal "$private_gid" "$IMAGE_GID" "private worker process uses image GID"
assert_no_http_listener "$private_name" "Private worker"
if ((SECONDS >= private_phase_deadline)); then
    fail "private ready-to-EOF observations finish within their derived phase budget"
fi
pass "private ready-to-EOF observations finish within their derived phase budget"
if ! kill -0 "$private_attach_pid" 2>/dev/null; then
    fail "private attach remains live immediately before controlled EOF"
fi
pass "private attach remains live immediately before controlled EOF"
if ! container_running "$private_name"; then
    fail "private container remains live immediately before controlled EOF"
fi
pass "private container remains live immediately before controlled EOF"
exec 9>&-
FIFO_WRITER_OPEN=0
if ! await_background "$private_attach_pid" "$PRIVATE_REAP_TIMEOUT"; then
    fail "private attach finishes within the post-EOF reap budget"
fi
if [[ "$BACKGROUND_WAIT_STATUS" == "124" ]]; then
    fail "private attach does not confuse its wrapper deadline with EOF completion"
fi
sanitize_stream < "$RAW_ROOT/private-stdout.txt" > "$EVIDENCE_ROOT/private-stdout.txt"
sanitize_stream < "$RAW_ROOT/private-stderr.txt" > "$EVIDENCE_ROOT/private-stderr.txt"
wait_until "$PRIVATE_REAP_TIMEOUT" "private worker reaps after controlled stdin EOF" container_stopped "$private_name"
private_container_exit="$(docker inspect --format '{{.State.ExitCode}}' "$private_name")"
require_equal "$BACKGROUND_WAIT_STATUS" "$private_container_exit" "private attach reports the exact container EOF outcome"
require_equal "$private_container_exit" "2" "private pre-request EOF returns the canonical invalid-input exit code"
require_equal "$(wc -l < "$EVIDENCE_ROOT/private-stdout.txt" | tr -d ' ')" "1" "private EOF probe retains exactly its one ready frame"
require_contains "$EVIDENCE_ROOT/private-stderr.txt" \
    'worker-exit-summary outcome=invalid-input phase=input message=worker invocation or input is invalid' \
    "private pre-request EOF emits the canonical safe summary"

capture_diagnostics
pass "all five deployment-role rows used one immutable image ID"
