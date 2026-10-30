"""Per-step state logging for experiments.

Each agent appends one JSON line per step to its own file (agent_loop.py
--state-log PATH). A line holds:

    ts, run_label, player, model, comms, turn, event
        event: "decision"      the model (or the harness's forced fallback) picked an action
               "stack_resolve" a --comms stack item of this agent was executed
               "fallback"      the harness guessed an action for an unmapped screen
               "run_end"       the run reached the game-over screen
    state_type, act, floor, round
    action  {tool, args}        what was done from this state (if anything)
    result  {status, message}   what the game (or the stack) answered
    snapshot                    the readable summary below, taken BEFORE the action
    raw                         the full game state as the mod reported it

snapshot:
    me          character, hp, max_hp, block, energy, max_energy, gold, statuses
    hand        each card: index, name, cost, type, playable, text, and the
                damage / hits / block parsed from its text
    potential   what the hand could do this turn:
                  damage_playable / block_playable   every playable card added up
                  best_damage / best_block           best total within current energy
    relics, potions, pile counts
    enemies     each enemy: id, name, hp, max_hp, block, intents, intended_damage
    incoming_damage   sum of all enemies' attack intents (in co-op the game shows the
                      number for the attack's target; it doesn't say who that is)
    teammates   the other players: character, hp, max_hp, block, alive, ready to end turn

Damage and block come from the card text as the game currently shows it, so
they are estimates: effects that aren't written as "Deal N damage" / "Gain N
Block" (poison, orbs, powers) aren't counted. The raw state is always kept, so
anything can be recomputed later.
"""

import json
import re
import time
from pathlib import Path

DAMAGE_RE = re.compile(r"deal\s+(\d+)\s+damage(?:\s+to\s+all\s+enemies)?(?:\s+(\d+)\s+times)?",
                       re.IGNORECASE)
BLOCK_RE = re.compile(r"gain\s+(\d+)\s+block", re.IGNORECASE)
INTENT_RE = re.compile(r"(\d+)\s*[x×]\s*(\d+)|(\d+)")


def _int(v):
    try:
        return int(v)
    except (TypeError, ValueError):
        return None


def card_numbers(text: str) -> dict:
    """Damage, hits and block written in a card's text ("Deal 3 damage 2 times")."""
    text = text or ""
    damage = hits = 0
    for m in DAMAGE_RE.finditer(text):
        n, times = int(m.group(1)), int(m.group(2) or 1)
        damage += n * times
        hits += times
    block = sum(int(m.group(1)) for m in BLOCK_RE.finditer(text))
    return {"damage": damage, "hits": hits, "block": block}


def intent_damage(intent: dict) -> int:
    """Damage an attack intent shows on its icon: "12" or "5x3" (= 15)."""
    if "attack" not in str(intent.get("type", "")).lower():
        return 0
    m = INTENT_RE.search(str(intent.get("label") or ""))
    if not m:
        return 0
    if m.group(1):
        return int(m.group(1)) * int(m.group(2))
    return int(m.group(3))


def best_within_energy(cards: list[dict], energy, key: str) -> int:
    """Highest total `key` (damage or block) from a set of playable cards whose
    cost fits in `energy` (0/1 knapsack; X-cost cards count as costing all energy)."""
    if energy is None:
        return sum(c[key] for c in cards)
    best = [0] * (energy + 1)
    for c in cards:
        cost = c["cost_int"]
        if cost is None:  # X or unknown cost
            cost = energy
        if cost > energy or c[key] <= 0:
            continue
        for e in range(energy, cost - 1, -1):
            best[e] = max(best[e], best[e - cost] + c[key])
    return best[energy]


def snapshot(state: dict) -> dict:
    p = state.get("player") or {}
    battle = state.get("battle") or {}
    snap: dict = {
        "me": {k: p.get(k) for k in ("character", "hp", "max_hp", "block", "energy",
                                      "max_energy", "stars", "gold") if p.get(k) is not None},
    }
    if p.get("status"):
        snap["me"]["statuses"] = [{"name": s.get("name"), "amount": s.get("amount")}
                                  for s in p["status"] if isinstance(s, dict)]

    hand = [c for c in p.get("hand") or [] if isinstance(c, dict)]
    if hand or "hand" in p:
        cards = []
        for c in hand:
            nums = card_numbers(c.get("description"))
            cards.append({"index": c.get("index"), "id": c.get("id"), "name": c.get("name"),
                          "cost": c.get("cost"), "cost_int": _int(c.get("cost")),
                          "type": c.get("type"), "playable": c.get("can_play"),
                          "text": c.get("description"), **nums})
        playable = [c for c in cards if c["playable"] is not False]
        energy = _int(p.get("energy"))
        snap["hand"] = [{k: v for k, v in c.items() if k != "cost_int"} for c in cards]
        snap["potential"] = {
            "damage_playable": sum(c["damage"] for c in playable),
            "block_playable": sum(c["block"] for c in playable),
            "best_damage": best_within_energy(playable, energy, "damage"),
            "best_block": best_within_energy(playable, energy, "block"),
        }

    snap["relics"] = [{"id": r.get("id"), "name": r.get("name"), "counter": r.get("counter")}
                      for r in p.get("relics") or [] if isinstance(r, dict)]
    snap["potions"] = [{"slot": x.get("slot"), "id": x.get("id"), "name": x.get("name")}
                       for x in p.get("potions") or [] if isinstance(x, dict)]
    piles = {k: p.get(k) for k in ("draw_pile_count", "discard_pile_count", "exhaust_pile_count")
             if p.get(k) is not None}
    if piles:
        snap["piles"] = piles

    enemies = [e for e in battle.get("enemies") or [] if isinstance(e, dict)]
    if enemies or "enemies" in battle:
        snap["enemies"] = []
        for e in enemies:
            intents = [i for i in e.get("intents") or [] if isinstance(i, dict)]
            snap["enemies"].append({
                "entity_id": e.get("entity_id"), "combat_id": e.get("combat_id"),
                "name": e.get("name"),
                "hp": e.get("hp"), "max_hp": e.get("max_hp"), "block": e.get("block"),
                "statuses": [{"name": s.get("name"), "amount": s.get("amount")}
                             for s in e.get("status") or [] if isinstance(s, dict)],
                "intents": [{"type": i.get("type"), "label": i.get("label")} for i in intents],
                "intended_damage": sum(intent_damage(i) for i in intents),
            })
        snap["incoming_damage"] = sum(e["intended_damage"] for e in snap["enemies"])

    mates = [m for m in state.get("players") or [] if isinstance(m, dict) and not m.get("is_local")]
    if mates:
        snap["teammates"] = [{k: m.get(k) for k in ("player_id", "character", "hp", "max_hp",
                                                     "block", "is_alive", "is_ready_to_end_turn")
                              if m.get(k) is not None} for m in mates]
    return snap


class StateLogger:
    """Appends one JSON line per step. With no path, it does nothing."""

    def __init__(self, path: str | None, run_label: str = "", player: str = "",
                 model: str = "", comms: str = ""):
        self.path = Path(path) if path else None
        self.meta = {"run_label": run_label, "player": player, "model": model, "comms": comms}
        if self.path:
            self.path.parent.mkdir(parents=True, exist_ok=True)

    def log(self, event: str, state: dict, turn=None, tool: str | None = None,
            args: dict | None = None, result_text: str | None = None) -> None:
        if not self.path or not isinstance(state, dict):
            return
        rec = {"ts": time.time(), **self.meta, "turn": turn, "event": event,
               "state_type": state.get("state_type"),
               "act": (state.get("run") or {}).get("act"),
               "floor": (state.get("run") or {}).get("floor"),
               "round": (state.get("battle") or {}).get("round")}
        if tool:
            rec["action"] = {"tool": tool, "args": args or {}}
        if result_text is not None:
            try:
                parsed = json.loads(result_text)
            except (json.JSONDecodeError, TypeError):
                parsed = None
            if isinstance(parsed, dict):
                rec["result"] = {"status": parsed.get("status"), "message": parsed.get("message")}
            else:
                rec["result"] = {"status": None, "message": str(result_text)[:300]}
        try:
            rec["snapshot"] = snapshot(state)
        except Exception as e:  # never let logging break a run
            rec["snapshot_error"] = f"{type(e).__name__}: {e}"
        rec["raw"] = state
        with open(self.path, "a", encoding="utf-8") as f:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")