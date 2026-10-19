#!/usr/bin/env bash
#
# start-localcoop-clients.sh
#
# Linux equivalent of Start-LocalCoopClients.ps1 / Start-LocalCoopTwoClient.ps1,
# with all Win32 window-placement, DPI-awareness, and Steam-Input-focus-bootstrap
# logic dropped (none of it resolves on Linux, and it isn't needed here since
# each client is driven headlessly via its own STS2_MCP MCP port rather than by
# screen/keyboard orientation).
#
# Requires: dotnet SDK on PATH, a built/dev checkout of LocalCoop's repo
# (for the broker CLI and the multi-client harness), and an STS2 install with
# both the LocalCoop and STS2_MCP mods in <game_root>/mods/.
#
# Usage:
#   ./start-localcoop-clients.sh -g /path/to/Slay-the-Spire-2 -c 4
#
# Env vars this script sets per client process:
#   LOCALCOOP_CONFIG_DIR   -> per-client config dir (enable-local-broker.txt)
#                              also consumed by the STS2_MCP LoadPort() patch
#                              to derive that client's MCP port (15526+index).
#
set -euo pipefail

# ---------------------------------------------------------------------------
# Defaults (override via flags)
# ---------------------------------------------------------------------------
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GAME_ROOT=""
CLIENT_COUNT=4
SESSION_ID="local-test"
BROKER_HOST="127.0.0.1"
BROKER_PORT=38989
CONFIG_ROOT="${REPO_ROOT}/.localcoop-clients"
GAME_EXECUTABLE=""          # auto-detected under GAME_ROOT if unset
BROKER_STARTUP_TIMEOUT=60
REUSE_EXISTING_BROKER=0
SKIP_CLIENT_LAUNCH=0

usage() {
    cat <<EOF
Usage: $0 -g <game_root> [options]

  -g <path>     Path to the Slay the Spire 2 install (contains mods/).  Required.
  -c <2-4>      Number of local clients to launch.                     Default: 4
  -s <id>       Broker session id.                                     Default: local-test
  -p <port>     Broker TCP port.                                       Default: 38989
  -e <path>     Game executable path (auto-detected if omitted).
  -C <path>     Config root for per-client enable-local-broker.txt.    Default: ./.localcoop-clients
  -r            Reuse an already-listening broker instead of restarting it.
  -n            Prepare configs only; don't launch game clients.
  -h            Show this help.
EOF
    exit 1
}

while getopts "g:c:s:p:e:C:rnh" opt; do
    case "$opt" in
        g) GAME_ROOT="$OPTARG" ;;
        c) CLIENT_COUNT="$OPTARG" ;;
        s) SESSION_ID="$OPTARG" ;;
        p) BROKER_PORT="$OPTARG" ;;
        e) GAME_EXECUTABLE="$OPTARG" ;;
        C) CONFIG_ROOT="$OPTARG" ;;
        r) REUSE_EXISTING_BROKER=1 ;;
        n) SKIP_CLIENT_LAUNCH=1 ;;
        h|*) usage ;;
    esac
done

if [[ -z "$GAME_ROOT" ]]; then
    echo "error: -g <game_root> is required" >&2
    usage
fi
if [[ "$CLIENT_COUNT" -lt 2 || "$CLIENT_COUNT" -gt 4 ]]; then
    echo "error: client count must be 2 through 4" >&2
    exit 1
fi

GAME_ROOT="$(cd "$GAME_ROOT" && pwd)"

# ---------------------------------------------------------------------------
# Resolve the game executable if not explicitly given.
# ---------------------------------------------------------------------------
if [[ -z "$GAME_EXECUTABLE" ]]; then
    for candidate in \
        "${GAME_ROOT}/SlayTheSpire2.x86_64" \
        "${GAME_ROOT}/SlayTheSpire2.sh" \
        "${GAME_ROOT}/slay-the-spire-2"; do
        if [[ -x "$candidate" ]]; then
            GAME_EXECUTABLE="$candidate"
            break
        fi
    done
    if [[ -z "$GAME_EXECUTABLE" ]]; then
        echo "error: could not auto-detect the game executable under $GAME_ROOT; pass -e explicitly." >&2
        exit 1
    fi
fi
echo "Game executable: $GAME_EXECUTABLE"

# steam_appid.txt is required for the Steamworks shim to init outside Steam launch.
if [[ ! -f "${GAME_ROOT}/steam_appid.txt" ]]; then
    echo "2868840" > "${GAME_ROOT}/steam_appid.txt"
    echo "Created ${GAME_ROOT}/steam_appid.txt"
fi

# ---------------------------------------------------------------------------
# Locate the LocalCoop repo (broker CLI + multi-client harness). Prefer a
# packaged install layout (broker/LocalCoop.Broker.Cli next to this script);
# fall back to a dev checkout.
# ---------------------------------------------------------------------------
BROKER_CLI_PROJECT="${REPO_ROOT}/src/LocalCoop.Broker.Cli/LocalCoop.Broker.Cli.csproj"
HARNESS_PROJECT="${REPO_ROOT}/tools/LocalCoop.MultiClientHarness/LocalCoop.MultiClientHarness.csproj"
if [[ ! -f "$BROKER_CLI_PROJECT" || ! -f "$HARNESS_PROJECT" ]]; then
    echo "error: could not find LocalCoop.Broker.Cli / LocalCoop.MultiClientHarness projects under $REPO_ROOT" >&2
    echo "       run this script from inside (or symlinked into) the LocalCoop repo checkout." >&2
    exit 1
fi

is_port_listening() {
    (exec 3<>"/dev/tcp/${BROKER_HOST}/${1}") 2>/dev/null && exec 3>&- 3<&-
}

wait_for_port() {
    local port="$1" timeout="$2" waited=0
    while (( waited < timeout )); do
        if is_port_listening "$port"; then
            return 0
        fi
        sleep 0.25
        waited=$((waited + 1))
    done
    return 1
}

# ---------------------------------------------------------------------------
# Broker
# ---------------------------------------------------------------------------
BROKER_PID=""
if [[ "$REUSE_EXISTING_BROKER" -eq 1 ]] && is_port_listening "$BROKER_PORT"; then
    echo "Broker already listening on ${BROKER_HOST}:${BROKER_PORT}; reusing it."
else
    if is_port_listening "$BROKER_PORT"; then
        echo "warning: something is already listening on ${BROKER_HOST}:${BROKER_PORT}. Kill it or pass -r to reuse it." >&2
        exit 1
    fi

    echo "Starting broker: session=${SESSION_ID} port=${BROKER_PORT}"
    dotnet run --project "$BROKER_CLI_PROJECT" -- "$SESSION_ID" "$BROKER_PORT" \
        > "${REPO_ROOT}/broker.log" 2>&1 &
    BROKER_PID=$!
    echo "Broker process id: $BROKER_PID (log: ${REPO_ROOT}/broker.log)"

    if ! wait_for_port "$BROKER_PORT" "$BROKER_STARTUP_TIMEOUT"; then
        echo "error: broker did not start listening on ${BROKER_HOST}:${BROKER_PORT} within ${BROKER_STARTUP_TIMEOUT}s" >&2
        kill "$BROKER_PID" 2>/dev/null || true
        exit 1
    fi
fi

cleanup() {
    if [[ -n "$BROKER_PID" ]] && kill -0 "$BROKER_PID" 2>/dev/null; then
        echo "Stopping broker (pid $BROKER_PID)..."
        kill "$BROKER_PID" 2>/dev/null || true
    fi
    for pid in "${CLIENT_PIDS[@]:-}"; do
        [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null && kill "$pid" 2>/dev/null || true
    done
}
trap cleanup EXIT INT TERM

# ---------------------------------------------------------------------------
# Prepare per-client configs via the (portable, no-Win32) multi-client harness.
# ---------------------------------------------------------------------------
echo "Preparing ${CLIENT_COUNT}-client configs under ${CONFIG_ROOT}..."
dotnet run --no-restore --project "$HARNESS_PROJECT" -- \
    prepare-clients "$CONFIG_ROOT" "$CLIENT_COUNT" "$SESSION_ID" "$BROKER_PORT" "$GAME_EXECUTABLE"

if [[ "$SKIP_CLIENT_LAUNCH" -eq 1 ]]; then
    echo "Configs prepared; client launch skipped (-n)."
    wait "$BROKER_PID" 2>/dev/null || true
    exit 0
fi

# Clear stale per-client mod logs so this run's logs aren't mixed with the last one.
find "${GAME_ROOT}/mods/LocalCoop" -maxdepth 1 -type f \
    \( -name 'localcoop-host-*-events.txt' \
       -o -name 'localcoop-client-*-events.txt' \
       -o -name 'localcoop-transport-probe-*.txt' \) \
    -delete 2>/dev/null || true

# ---------------------------------------------------------------------------
# Launch clients. No window placement, no DPI awareness, no Steam Input
# focus bootstrap — each client is driven via its own STS2_MCP port instead.
# ---------------------------------------------------------------------------
declare -a CLIENT_PIDS=()
for (( i=0; i<CLIENT_COUNT; i++ )); do
    client_dir="${CONFIG_ROOT}/client-${i}"
    if [[ ! -f "${client_dir}/enable-local-broker.txt" ]]; then
        echo "error: missing config for client $i at $client_dir" >&2
        exit 1
    fi

    mcp_port=$((15526 + i))
    echo "Starting client $i (config: $client_dir, expected STS2_MCP port: $mcp_port)"
    LOCALCOOP_CONFIG_DIR="$client_dir" "$GAME_EXECUTABLE" \
        > "${CONFIG_ROOT}/client-${i}.stdout.log" 2>&1 &
    CLIENT_PIDS+=("$!")
    echo "  -> pid $!"
done

echo ""
echo "All ${CLIENT_COUNT} clients launched. STS2_MCP ports: $(seq -s ', ' 15526 $((15526 + CLIENT_COUNT - 1)))"
echo "Point each MCP server instance ('--port N') at its matching client."
echo "Press Ctrl+C to stop the broker and clients."

wait "$BROKER_PID" 2>/dev/null || true
