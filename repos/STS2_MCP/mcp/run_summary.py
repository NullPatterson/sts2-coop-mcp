#!/usr/bin/env python3
"""Per-run metrics computed from the per-step state logs (state_logger.py).

experiment_handler.py calls summarize() after every run and adds the result
to summary.csv. You can also run this file on an experiment folder to
recompute metrics for runs that already happened:

    python run_summary.py experiments/20261030-121015
    -> writes experiments/20261030-121015/metrics.csv (one row per run folder)

Metrics (all agents of the run combined unless the name says by_agent):
    gameplay_s          first in-run step -> game-over screen (excludes lobby/menus)
    result              loss            every player at 0 HP when the run ended
                        win_inferred    run ended with players alive after a boss fight
                        incomplete      no game-over screen in the logs (stopped/crashed)
                        unknown         anything else
                        (the mod's game-over screen doesn't say win or loss)
    final_act, final_floor
    combats, elites, bosses
    combat_rounds       combat rounds summed over all fights
    actions_total       steps the agents took during the run (menus excluded)
    actions_by_agent    e.g. "p0:212;p1:198"
    stack_resolves      --comms stack items that actually got played
    final_hp_by_agent   e.g. "p0:0/80;p1:12/70"
    characters_by_agent e.g. "p0:The Ironclad;p1:The Silent"
    last_combat         floor and enemies of the last fight, e.g. "f16 boss: The Kin"
"""

import csv
import json
import sys
from pathlib import Path

COMBAT_TYPES = ("monster", "elite", "boss")

FIELDS = ["gameplay_s", "result", "final_act", "final_floor", "combats", "elites", "bosses",
          "combat_rounds", "actions_total", "actions_by_agent", "stack_resolves",
          "final_hp_by_agent", "characters_by_agent", "last_combat"]


def _load(path: Path) -> list[dict]:
    recs = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            try:
                recs.append(json.loads(line))
            except json.JSONDecodeError:
                continue  # a line cut off when the run was killed
    return recs


def _in_run(r: dict) -> bool:
    return r.get("floor") is not None or r.get("state_type") in COMBAT_TYPES


def summarize(run_dir) -> dict:
    """Metrics for one run folder holding p<i>_<port>.jsonl state logs."""
    run_dir = Path(run_dir)
    files = sorted(run_dir.glob("p*_*.jsonl"))
    out = {k: "" for k in FIELDS}
    if not files:
        out["result"] = "no_state_logs"
        return out

    agents = {f.stem.split("_")[0]: _load(f) for f in files}  # "p0" -> records
    every = [r for recs in agents.values() for r in recs]
    in_run = sorted((r for r in every if _in_run(r)), key=lambda r: r.get("ts", 0))
    ends = [r for r in every if r.get("event") == "run_end"]

    # Time: first in-run step to the (latest) game-over screen.
    if in_run:
        start = in_run[0]["ts"]
        stop = max(r["ts"] for r in ends) if ends else in_run[-1]["ts"]
        out["gameplay_s"] = round(stop - start, 1)

    # How far it got.
    acts = [r["act"] for r in in_run if isinstance(r.get("act"), int)]
    floors = [r["floor"] for r in in_run if isinstance(r.get("floor"), int)]
    out["final_act"] = max(acts) if acts else ""
    out["final_floor"] = max(floors) if floors else ""

    # Fights: one per (act, floor) seen in a combat state; rounds = highest round seen.
    fights: dict[tuple, dict] = {}
    for r in in_run:
        if r.get("state_type") in COMBAT_TYPES:
            key = (r.get("act"), r.get("floor"))
            fight = fights.setdefault(key, {"type": r["state_type"], "rounds": 0,
                                            "enemies": set(), "ts": r.get("ts", 0)})
            if isinstance(r.get("round"), int):
                fight["rounds"] = max(fight["rounds"], r["round"])
            for e in (r.get("snapshot") or {}).get("enemies") or []:
                if e.get("name"):
                    fight["enemies"].add(e["name"])
            fight["ts"] = max(fight["ts"], r.get("ts", 0))
    out["combats"] = len(fights)
    out["elites"] = sum(1 for f in fights.values() if f["type"] == "elite")
    out["bosses"] = sum(1 for f in fights.values() if f["type"] == "boss")
    out["combat_rounds"] = sum(f["rounds"] for f in fights.values())
    last_fight = max(fights.items(), key=lambda kv: kv[1]["ts"]) if fights else None
    if last_fight:
        (_, floor), f = last_fight
        out["last_combat"] = f"f{floor} {f['type']}: " + ", ".join(sorted(f["enemies"]))

    # Actions per agent (in-run only, so menus and lobbies don't count).
    counts = {a: sum(1 for r in recs if _in_run(r) and r.get("event") in ("decision", "fallback"))
              for a, recs in agents.items()}
    out["actions_total"] = sum(counts.values())
    out["actions_by_agent"] = ";".join(f"{a}:{n}" for a, n in counts.items())
    out["stack_resolves"] = sum(1 for r in every if r.get("event") == "stack_resolve")

    # Final HP and character per agent: the last record that reports them.
    final_hp, chars = {}, {}
    for a, recs in agents.items():
        for r in reversed(recs):
            me = (r.get("snapshot") or {}).get("me") or {}
            if me.get("hp") is not None and a not in final_hp:
                final_hp[a] = (me["hp"], me.get("max_hp"))
            if me.get("character") and a not in chars:
                chars[a] = me["character"]
            if a in final_hp and a in chars:
                break
    out["final_hp_by_agent"] = ";".join(f"{a}:{hp}/{mx}" for a, (hp, mx) in final_hp.items())
    out["characters_by_agent"] = ";".join(f"{a}:{c}" for a, c in chars.items())

    # Result (inferred; see module docstring).
    if not ends:
        out["result"] = "incomplete"
    elif final_hp and all(hp is not None and hp <= 0 for hp, _ in final_hp.values()):
        out["result"] = "loss"
    elif last_fight and last_fight[1]["type"] == "boss" and any(
            hp and hp > 0 for hp, _ in final_hp.values()):
        out["result"] = "win_inferred"
    else:
        out["result"] = "unknown"
    return out


def main():
    if len(sys.argv) != 2:
        sys.exit("usage: python run_summary.py <experiments/<timestamp> folder>")
    exp = Path(sys.argv[1])
    run_dirs = sorted(d for d in exp.iterdir() if d.is_dir() and any(d.glob("p*_*.jsonl")))
    if not run_dirs:
        sys.exit(f"no run folders with state logs in {exp}")
    out_path = exp / "metrics.csv"
    with open(out_path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=["folder"] + FIELDS)
        w.writeheader()
        for d in run_dirs:
            row = summarize(d)
            w.writerow({"folder": d.name, **row})
            print(f"{d.name}: {row['result']}, floor {row['final_floor']}, "
                  f"{row['gameplay_s']}s, {row['actions_total']} actions")
    print(f"wrote {out_path}")


if __name__ == "__main__":
    main()