#!/usr/bin/env python3
"""Run one agent_loop.py per local co-op client.

Client i listens on BASE_PORT + i (needs the per-client-port patch in
sts2mcp-per-client-port.patch, or your own STS2_MCP.conf/STS2_MCP_PORT per
client). Client 0 is the lobby host, the rest join. Each client can use a
different model/tunnel; characters are distinct by default.

  python run_multiplayer.py --config players.json
  python run_multiplayer.py --clients 2 --provider openai --model Qwen/Qwen3-32B-AWQ \\
        --base-url http://localhost:8001/v1

players.json (list, one entry per client; missing keys fall back to CLI flags):
  [{"model": "Qwen/Qwen3-32B-AWQ", "base_url": "http://localhost:8001/v1", "character": "IRONCLAD"},
   {"model": "Qwen/Qwen3-8B",      "base_url": "http://localhost:8002/v1", "character": "SILENT"}]
"""
import argparse
import json
import random
import socket
import subprocess
import sys
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
CHARACTERS = ["IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"]


def port_open(port: int) -> bool:
    with socket.socket() as s:
        s.settimeout(0.5)
        return s.connect_ex(("127.0.0.1", port)) == 0


def pump(tag: str, stream):
    for line in iter(stream.readline, ""):
        print(f"[{tag}] {line.rstrip()}", flush=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--clients", type=int, default=None, help="Number of clients (default: len(config), else 2).")
    ap.add_argument("--base-port", type=int, default=15526, help="Mod port of client 0; client i uses base+i.")
    ap.add_argument("--config", type=str, default=None, help="JSON list of per-client settings.")
    ap.add_argument("--provider", default="openai", choices=["anthropic", "openai"])
    ap.add_argument("--model", default=None)
    ap.add_argument("--base-url", default=None)
    ap.add_argument("--runs", type=int, default=0, help="Runs per client before stopping (0 = forever).")
    ap.add_argument("--max-turns", type=int, default=None, help="Total action budget per client (default: unlimited).")
    ap.add_argument("--wait", type=int, default=180, help="Seconds to wait for each client's mod port.")
    ap.add_argument("--stagger", type=float, default=5.0, help="Seconds between starting agents (host first).")
    ap.add_argument("--single-player", action="store_true",
                    help="Debug: run without --multiplayer (each client plays its own solo run).")
    args = ap.parse_args()

    players = json.loads(Path(args.config).read_text()) if args.config else []
    n = args.clients or len(players) or 2
    if not 1 <= n <= 4:
        sys.exit("STS2 co-op supports 1-4 clients.")
    players = (players + [{}] * n)[:n]

    chars = random.sample(CHARACTERS, n)
    procs = []
    try:
        for i, pl in enumerate(players):
            port = args.base_port + i
            print(f"[launcher] waiting for client {i}'s mod on port {port} ...", flush=True)
            deadline = time.time() + args.wait
            while not port_open(port):
                if time.time() > deadline:
                    sys.exit(f"[launcher] port {port} never opened. Is client {i} running with the "
                             f"per-client-port patch applied? (Without it every client uses 15526.)")
                time.sleep(2)

            cmd = [sys.executable, "-u", str(HERE / "agent_loop.py"),
                   "--port", str(port),
                   "--provider", pl.get("provider", args.provider),
                   "--character", pl.get("character", chars[i]),
                   "--runs", str(args.runs)]
            if args.max_turns is not None:
                cmd += ["--max-turns", str(args.max_turns)]
            model = pl.get("model", args.model)
            base_url = pl.get("base_url", args.base_url)
            if model: cmd += ["--model", model]
            if base_url: cmd += ["--base-url", base_url]
            if not args.single_player:
                cmd += ["--multiplayer", "--mp-role", "host" if i == 0 else "join"]

            tag = f"p{i}:{pl.get('character', chars[i])}"
            p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                 text=True, bufsize=1)
            threading.Thread(target=pump, args=(tag, p.stdout), daemon=True).start()
            procs.append((tag, p))
            time.sleep(args.stagger)  # host gets a head start on creating the lobby

        while any(p.poll() is None for _, p in procs):
            time.sleep(2)
    except KeyboardInterrupt:
        print("[launcher] stopping agents ...")
    finally:
        for _, p in procs:
            if p.poll() is None:
                p.terminate()
        for tag, p in procs:
            try:
                p.wait(10)
            except subprocess.TimeoutExpired:
                p.kill()
            print(f"[launcher] {tag} exited with {p.returncode}")


if __name__ == "__main__":
    main()