#!/usr/bin/env bash
# Experimental Linux launcher for CouchCoop local co-op (the upstream launcher is
# Windows/PowerShell only). Starts the broker, writes per-client configs, and
# launches N game clients, each with its own LOCALCOOP_CONFIG_DIR and mod port.
#
#   GAME=~/sts2-coop-mcp/game COOP=~/sts2-coop-mcp/repos/STS2CouchCoop ./start_coop_linux.sh 2
#   ./start_coop_linux.sh build        # build the CouchCoop mod + broker once
#   DRY_RUN=1 ./start_coop_linux.sh 2  # write configs and print commands only
set -euo pipefail

GAME="${GAME:-$HOME/sts2-coop-mcp/game}"
COOP="${COOP:-$HOME/sts2-coop-mcp/repos/STS2CouchCoop}"
SESSION="${SESSION:-local-test}"
BROKER_PORT="${BROKER_PORT:-38989}"
BASE_PORT="${BASE_PORT:-15526}"
RUNTIME="${RUNTIME:-$HOME/.local/share/sts2-localcoop}"
DATA_DIR="$GAME/data_sts2_linuxbsd_x86_64"
BROKER_DLL="$COOP/src/LocalCoop.Broker.Cli/bin/Release/net9.0/LocalCoop.Broker.Cli.dll"

build() {
  [ -f "$DATA_DIR/sts2.dll" ] || { echo "No sts2.dll in $DATA_DIR (set GAME=...)"; exit 1; }
  # The mod's csproj hardcodes the Windows data folder; point it at the Linux one.
  sed -i 's/data_sts2_windows_x86_64/data_sts2_linuxbsd_x86_64/g' "$COOP/src/LocalCoop.Mod/LocalCoop.Mod.csproj"
  mkdir -p "$GAME/mods/LocalCoop"
  dotnet build "$COOP/src/LocalCoop.Mod/LocalCoop.Mod.csproj" -c Release \
    -p:Sts2GameRoot="$GAME" -p:LocalCoopModOutputPath="$GAME/mods/LocalCoop/"
  cp "$COOP/release/LocalCoop.json" "$GAME/mods/LocalCoop/LocalCoop.json"
  dotnet build "$COOP/src/LocalCoop.Broker.Cli/LocalCoop.Broker.Cli.csproj" -c Release
  echo "Built. Mod in $GAME/mods/LocalCoop, broker at $BROKER_DLL"
}

if [ "${1:-}" = "build" ]; then build; exit 0; fi

N="${1:-2}"
[[ "$N" =~ ^[1-4]$ ]] || { echo "usage: $0 <1-4 | build>"; exit 1; }
[ -f "$BROKER_DLL" ] || { echo "Broker not built. Run: $0 build"; exit 1; }
[ -f "$GAME/mods/LocalCoop/LocalCoop.dll" ] || { echo "LocalCoop mod not built. Run: $0 build"; exit 1; }

mkdir -p "$RUNTIME"
for ((i=0; i<N; i++)); do
  d="$RUNTIME/client-$i"; mkdir -p "$d"
  role=client; [ "$i" -eq 0 ] && role=host
  cat > "$d/enable-local-broker.txt" <<CFG
role=$role
clientIndex=$i
playerSlot=$i
inputMode=none
controllerClientCount=0
endpoint=127.0.0.1:$BROKER_PORT
sessionId=$SESSION
CFG
done
echo "Wrote configs under $RUNTIME (role: client-0=host, others=client)"

run() { if [ -n "${DRY_RUN:-}" ]; then echo "DRY: $*"; else "$@"; fi; }
pids=()
cleanup() { for p in "${pids[@]:-}"; do kill "$p" 2>/dev/null || true; done; }
trap cleanup EXIT INT TERM

# Window layout: 2 columns x 2 rows on a 1920x1080 screen.
W=960; H=540
echo "Starting broker on 127.0.0.1:$BROKER_PORT (session $SESSION)"
if [ -z "${DRY_RUN:-}" ]; then
  dotnet "$BROKER_DLL" "$SESSION" "$BROKER_PORT" "$RUNTIME/broker.log" >"$RUNTIME/broker.out" 2>&1 &
  pids+=($!); sleep 3
  kill -0 "${pids[0]}" 2>/dev/null || { echo "Broker exited:"; cat "$RUNTIME/broker.out"; exit 1; }
fi

for ((i=0; i<N; i++)); do
  x=$(( (i % 2) * W )); y=$(( (i / 2) * H ))
  echo "Starting client $i (mod port $((BASE_PORT+i)), window ${W}x${H}+$x+$y)"
  if [ -n "${DRY_RUN:-}" ]; then
    echo "DRY: LOCALCOOP_CONFIG_DIR=$RUNTIME/client-$i STS2_MCP_PORT=$((BASE_PORT+i)) $GAME/SlayTheSpire2 --rendering-driver vulkan --windowed --position $x,$y --resolution ${W}x${H}"
  else
    ( cd "$GAME" && LOCALCOOP_CONFIG_DIR="$RUNTIME/client-$i" STS2_MCP_PORT=$((BASE_PORT+i)) \
        exec ./SlayTheSpire2 --rendering-driver vulkan --windowed --position "$x,$y" --resolution "${W}x${H}" ) \
        >"$RUNTIME/client-$i.log" 2>&1 &
    pids+=($!)
    sleep 15   # stagger: host first, then joiners
  fi
done

[ -n "${DRY_RUN:-}" ] && exit 0
echo "Clients up. Mod ports: $(seq -s' ' $BASE_PORT $((BASE_PORT+N-1))). Ctrl+C stops everything."
wait