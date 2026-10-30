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
    ap.add_argument("--num-models", type=int, default=2, choices=[1, 2, 3, 4],
                    help="Number of models (players) per game, 1-4 (default: 2).")
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
    ap.add_argument("--extra", nargs=argparse.REMAINDER, default=[],
                    help="Anything after --extra is passed straight to run_multiplayer.py.")
    ap.add_argument("--dry-run", action="store_true",
                    help="Print what would run (and the players.json for it) without running.")
    args = ap.parse_args()

    if args.runs < 1:
        ap.error("--runs must be at least 1")
    for name, values in (("--ports", args.ports), ("--gpunodes", args.gpunodes)):
        if len(values) != 1 and len(values) < args.num_models:
            ap.error(f"{name} needs one entry per player ({args.num_models}), or a single entry "
                     f"shared by every player; got {len(values)}.")
    args.comms = [COMM_STYLES[flag] for flag in COMM_STYLES if getattr(args, flag)]
    if not args.comms:
        ap.error("every communication style is off; enable at least one "
                 "(--no_comm, --blackboard_comm, --stack_comm).")
    return args


def build_players(args, model_name: str, existing: list) -> list[dict]:
    """One entry per player. Keys already in players.json that this script doesn't
    manage (e.g. a fixed "character") are kept."""
    players = []
    for i in range(args.num_models):
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

    # Run index outermost, so conditions alternate (a, b, c, a, b, c, ...) and
    # any drift over a long session is spread across all of them.
    plan = [(k, size, comm)
            for k in range(1, args.runs + 1)
            for size in args.model_sizes
            for comm in args.comms]
    print(f"[experiment] {len(plan)} run(s): comms={args.comms} sizes={args.model_sizes} "
          f"players={args.num_models} runs/condition={args.runs}", flush=True)

    if not args.dry_run:
        out_dir.mkdir(parents=True, exist_ok=True)
        summary_file = open(out_dir / "summary.csv", "w", newline="", encoding="utf-8")
        summary = csv.writer(summary_file)
        summary.writerow(["n", "run", "model_size", "model", "comms", "players", "started",
                          "duration_s", "return_code", "folder"] + METRIC_FIELDS)
    try:
        for n, (k, size, comm) in enumerate(plan, 1):
            model_name = MODELS[size]
            players = build_players(args, model_name, load_existing(players_path))
            label = f"{comm}_{size}_run{k}"
            run_dir = out_dir / f"{n:03d}_{label}"  # launcher.log + one state log per agent
            cmd = [sys.executable, "-u", str(HERE / "run_multiplayer.py"),
                   "--config", str(players_path),
                   "--clients", str(args.num_models),
                   "--runs", "1",
                   "--comms", comm,
                   "--state-log-dir", str(run_dir),
                   "--run-label", label] + args.extra
            print(f"\n[experiment] ===== {n}/{len(plan)}: {label} =====", flush=True)

            if args.dry_run:
                print(json.dumps(players, indent=2))
                print("$ " + " ".join(cmd))
                continue

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
            summary.writerow([n, k, size, model_name, comm, args.num_models,
                              started.isoformat(timespec="seconds"), duration, code,
                              run_dir.name] + [metrics.get(f, "") for f in METRIC_FIELDS])
            summary_file.flush()
            print(f"[experiment] {label} finished with code {code} after {duration}s: "
                  f"{metrics.get('result')}, act {metrics.get('final_act')} floor "
                  f"{metrics.get('final_floor')}, {metrics.get('combats')} fights, "
                  f"{metrics.get('gameplay_s')}s of gameplay", flush=True)
            if code != 0:
                print(f"[experiment] WARNING: {label} exited with code {code}; see {log_path}. "
                      f"Continuing with the next run.", flush=True)

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