#!/usr/bin/env python3
"""Run a batch of co-op experiments, one fresh run_multiplayer.py call per run.

For every run, every enabled communication style and every model size, this
rewrites players.json and calls:

    python run_multiplayer.py --config players.json --clients N --runs 1 --comms <style>

Calling run_multiplayer.py again for each run (instead of --runs N) avoids
the problems seen when one launcher kept playing run after run.

Examples:
    # default: no comms only, 32B, 2 players, 1 run
    python experiment_handler.py --ports 8001 8002 --gpunodes gpu01 gpu02

    # compare all three styles, 5 runs each
    python experiment_handler.py --blackboard_comm --stack_comm --runs 5 \\
        --ports 8001 8002 --gpunodes gpu01 gpu02

    # stack only (turn the default no-comm condition off)
    python experiment_handler.py --no_comm false --stack_comm --ports 8001 8002 --gpunodes gpu01 gpu02

Ports are the local ports your SSH tunnels forward to each GPU node's vLLM
server, so player i uses http://localhost:<ports[i]>/v1 (change this with
--base-url-template). Pass one port and one node to have every player share
the same server.

Each run gets a folder experiments/<timestamp>/<NNN>_<condition>/ holding the
launcher output (launcher.log) and every agent's per-step state log
(p<i>_<port>.jsonl, see state_logger.py), plus a summary.csv of every run:
condition, return code, wall-clock duration, folder, and the run metrics
from run_summary.py (gameplay time, result, act/floor reached, fights,
combat rounds, actions per agent, final HP, ...).
"""

import argparse
import csv
import json
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path

from run_summary import FIELDS as METRIC_FIELDS, summarize

HERE = Path(__file__).resolve().parent

# Model size -> full model name passed to the agents. Add sizes here as you serve them.
MODELS = {
    "32B": "Qwen/Qwen3-32B-AWQ",
    "72B": "Qwen/Qwen2.5-72B-Instruct-AWQ",
}

# Communication flag -> the --comms value run_multiplayer.py / agent_loop.py expect.
COMM_STYLES = {
    "no_comm": "none",
    "blackboard_comm": "blackboard",
    "stack_comm": "stack",
}


def str2bool(v) -> bool:
    if isinstance(v, bool):
        return v
    if v.lower() in ("true", "t", "yes", "y", "1"):
        return True
    if v.lower() in ("false", "f", "no", "n", "0"):
        return False
    raise argparse.ArgumentTypeError(f"expected true or false, got {v!r}")


def parse_args():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    # Communication styles. A bare flag means true; "--no_comm false" turns one off.
    ap.add_argument("--no_comm", type=str2bool, nargs="?", const=True, default=True,
                    help="Test with no communication (default: true).")
    ap.add_argument("--blackboard_comm", type=str2bool, nargs="?", const=True, default=False,
                    help="Test with the shared blackboard (default: false).")
    ap.add_argument("--stack_comm", type=str2bool, nargs="?", const=True, default=False,
                    help="Test with the priority stack (default: false).")

    ap.add_argument("--model-sizes", nargs="+", default=["32B"], choices=sorted(MODELS),
                    help="Model sizes to test (keys of MODELS; default: 32B).")
    ap.add_argument("--num-models", type=int, nargs="+", default=[2], choices=[1, 2, 3, 4],
                    help="Number of models (players) per game, 1-4 (default: 2). Several "
                         "counts run one after another, e.g. --num-models 2 3 does every "
                         "2-player run first, then every 3-player run. Start as many game "
                         "clients as the largest count; spare clients wait at the menu.")
    ap.add_argument("--ports", nargs="+", type=int, required=True,
                    help="Local port for each player's model server, in player order.")
    ap.add_argument("--gpunodes", nargs="+", required=True,
                    help="GPU node serving each player's model, in player order.")
    ap.add_argument("--runs", type=int, default=1,
                    help="Runs per condition (communication style x model size). Default 1.")

    ap.add_argument("--base-url-template", default="http://localhost:{port}/v1",
                    help="How a player's base_url is built; {port} and {node} are filled in.")
    ap.add_argument("--players-file", default=str(HERE / "players.json"),
                    help="The players.json run_multiplayer.py reads (rewritten every run).")
    ap.add_argument("--cooldown", type=float, default=15.0,
                    help="Seconds to wait between runs so the game clients settle on the menu.")
    ap.add_argument("--mod-base-port", type=int, default=15526,
                    help="STS2_MCP mod port of game client 0 (client i uses base+i). Passed to "
                         "run_multiplayer.py as --base-port, and used to check the clients are "
                         "at the main menu before each run.")
    ap.add_argument("--menu-wait", type=float, default=180.0,
                    help="Before each run, wait up to this many seconds for every game client "
                         "to be at the main menu. If one is still mid-run, the batch stops "
                         "instead of starting a new run on top of it.")
    ap.add_argument("--extra", nargs=argparse.REMAINDER, default=[],
                    help="Anything after --extra is passed straight to run_multiplayer.py.")
    ap.add_argument("--dry-run", action="store_true",
                    help="Print what would run (and the players.json for it) without running.")
    args = ap.parse_args()

    if args.runs < 1:
        ap.error("--runs must be at least 1")
    args.num_models = list(dict.fromkeys(args.num_models))  # drop repeats, keep order
    most = max(args.num_models)
    for name, values in (("--ports", args.ports), ("--gpunodes", args.gpunodes)):
        if len(values) != 1 and len(values) < most:
            ap.error(f"{name} needs one entry per player ({most} for the largest game), or a "
                     f"single entry shared by every player; got {len(values)}.")
    args.comms = [COMM_STYLES[flag] for flag in COMM_STYLES if getattr(args, flag)]
    if not args.comms:
        ap.error("every communication style is off; enable at least one "
                 "(--no_comm, --blackboard_comm, --stack_comm).")
    return args


def build_players(args, model_name: str, existing: list, count: int) -> list[dict]:
    """One entry per player. Keys already in players.json that this script doesn't
    manage (e.g. a fixed "character") are kept."""
    players = []
    for i in range(count):
        port = args.ports[i] if len(args.ports) > 1 else args.ports[0]
        node = args.gpunodes[i] if len(args.gpunodes) > 1 else args.gpunodes[0]
        entry = dict(existing[i]) if i < len(existing) and isinstance(existing[i], dict) else {}
        entry.update({
            "model": model_name,
            "base_url": args.base_url_template.format(port=port, node=node),
            "gpu_node": node,
        })
        players.append(entry)
    return players


def client_status(port: int) -> str:
    """Where one game client is, asked from its mod: "menu", "not running",
    or a description of the run it's in."""
    import urllib.error
    import urllib.request
    url = f"http://127.0.0.1:{port}/api/v1/singleplayer?format=json"
    try:
        with urllib.request.urlopen(url, timeout=5) as r:
            state = json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        # The mod refuses the singleplayer endpoint while a co-op run is active.
        return "in a co-op run" if e.code == 409 else f"HTTP {e.code}"
    except (urllib.error.URLError, OSError):
        return "not running"  # run_multiplayer.py waits for the port itself
    except json.JSONDecodeError:
        return "unreadable state"
    if state.get("state_type") == "menu" and "run" not in state:
        return "menu"
    return f"in a run ({state.get('state_type')})"


def wait_for_menus(base_port: int, n: int, timeout: float) -> tuple[bool, list[str]]:
    """True once every client is at the main menu (or not running yet)."""
    deadline = time.monotonic() + timeout
    while True:
        statuses = [client_status(base_port + i) for i in range(n)]
        if all(s in ("menu", "not running") for s in statuses):
            return True, statuses
        if time.monotonic() >= deadline:
            return False, statuses
        time.sleep(5)


def load_existing(path: Path) -> list:
    try:
        data = json.loads(path.read_text())
        return data if isinstance(data, list) else []
    except (FileNotFoundError, json.JSONDecodeError):
        return []


def run_and_log(cmd: list[str], log_path: Path) -> int:
    """Run the launcher, echoing its output to the console and a log file."""
    with open(log_path, "w", encoding="utf-8") as log:
        log.write("$ " + " ".join(cmd) + "\n\n")
        proc = subprocess.Popen(cmd, cwd=HERE, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, text=True, bufsize=1)
        try:
            for line in proc.stdout:
                sys.stdout.write(line)
                log.write(line)
            return proc.wait()
        except KeyboardInterrupt:
            proc.terminate()  # run_multiplayer.py shuts its agents down on terminate
            try:
                proc.wait(30)
            except subprocess.TimeoutExpired:
                proc.kill()
            raise


def main():
    args = parse_args()
    players_path = Path(args.players_file)
    out_dir = HERE / "experiments" / datetime.now().strftime("%Y%m%d-%H%M%S")

    # Player counts run one after another (all 2-player runs, then all 3-player
    # runs). Within a count the run index is outermost, so conditions alternate
    # (a, b, c, a, b, c, ...) and any drift over a long session is spread across
    # all of them.
    plan = [(count, k, size, comm)
            for count in args.num_models
            for k in range(1, args.runs + 1)
            for size in args.model_sizes
            for comm in args.comms]
    print(f"[experiment] {len(plan)} run(s): players={args.num_models} comms={args.comms} "
          f"sizes={args.model_sizes} runs/condition={args.runs}", flush=True)

    if not args.dry_run:
        out_dir.mkdir(parents=True, exist_ok=True)
        summary_file = open(out_dir / "summary.csv", "w", newline="", encoding="utf-8")
        summary = csv.writer(summary_file)
        summary.writerow(["n", "run", "model_size", "model", "comms", "players", "started",
                          "duration_s", "return_code", "folder"] + METRIC_FIELDS)
    try:
        for n, (count, k, size, comm) in enumerate(plan, 1):
            model_name = MODELS[size]
            players = build_players(args, model_name, load_existing(players_path), count)
            label = f"{comm}_{size}_{count}p_run{k}"
            run_dir = out_dir / f"{n:03d}_{label}"  # launcher.log + one state log per agent
            cmd = [sys.executable, "-u", str(HERE / "run_multiplayer.py"),
                   "--config", str(players_path),
                   "--clients", str(count),
                   "--runs", "1",
                   "--comms", comm,
                   "--state-log-dir", str(run_dir),
                   "--run-label", label,
                   "--base-port", str(args.mod_base_port)] + args.extra
            print(f"\n[experiment] ===== {n}/{len(plan)}: {label} =====", flush=True)

            if args.dry_run:
                print(json.dumps(players, indent=2))
                print("$ " + " ".join(cmd))
                continue

            # Never start a run while a game client is still in the previous one:
            # the new agents would just carry on that game under the new label.
            ready, statuses = wait_for_menus(args.mod_base_port, count, args.menu_wait)
            if not ready:
                where = ", ".join(f"client {i} (port {args.mod_base_port + i}): {s}"
                                  for i, s in enumerate(statuses))
                print(f"[experiment] STOPPING the batch before {label}: not every game client "
                      f"is at the main menu after {args.menu_wait:.0f}s ({where}). Finish or "
                      f"abandon that run in the game, then rerun the remaining conditions.",
                      flush=True)
                break

            players_path.write_text(json.dumps(players, indent=2) + "\n")
            started = datetime.now()
            t0 = time.monotonic()
            run_dir.mkdir(parents=True, exist_ok=True)
            log_path = run_dir / "launcher.log"
            code = run_and_log(cmd, log_path)
            duration = round(time.monotonic() - t0, 1)
            try:
                metrics = summarize(run_dir)  # from the per-step state logs
            except Exception as e:  # a metrics bug must never stop the batch
                metrics = {f: "" for f in METRIC_FIELDS}
                metrics["result"] = f"summary_error: {type(e).__name__}: {e}"
            summary.writerow([n, k, size, model_name, comm, count,
                              started.isoformat(timespec="seconds"), duration, code,
                              run_dir.name] + [metrics.get(f, "") for f in METRIC_FIELDS])
            summary_file.flush()
            print(f"[experiment] {label} finished with code {code} after {duration}s: "
                  f"{metrics.get('result')}, act {metrics.get('final_act')} floor "
                  f"{metrics.get('final_floor')}, {metrics.get('combats')} fights, "
                  f"{metrics.get('gameplay_s')}s of gameplay", flush=True)
            if code != 0:
                print(f"[experiment] WARNING: {label} exited with code {code} (3 = an agent "
                      f"stopped before its run ended); see {log_path}. The next run only "
                      f"starts once every client is back at the main menu.", flush=True)

            if n < len(plan):
                time.sleep(args.cooldown)
    except KeyboardInterrupt:
        print("\n[experiment] stopped by user.", flush=True)
    finally:
        if not args.dry_run:
            summary_file.close()
            print(f"[experiment] logs and summary.csv: {out_dir}", flush=True)


if __name__ == "__main__":
    main()