"""--comms stack: a shared last-in-first-out action stack with priority, modelled
on Magic: the Gathering. Agents never talk; the stack is how they see each
other's plays.

Rules (combat only: monster / elite / boss states)
--------------------------------------------------
- Exactly one agent holds priority at a time. Everyone else waits.
- With priority, an agent may play a card or use a potion. Instead of
  happening, it is PUSHED onto the stack with everything a teammate needs to
  judge it: who, which card/potion, its cost, type, text and target.
- Priority then passes to the NEXT agent (seat order = port order). That agent
  can respond (push its own card/potion on top, which resolves FIRST) or pass
  (the stack_pass tool). Each agent who still could act gets asked in turn.
- When every other agent has passed in a row, the TOP item resolves: its
  owner's harness runs the real game tool. If the card is no longer in hand,
  can't be paid for, or its target died, it fizzles instead (like a spell
  with an illegal target).
- After a resolution, the next item down is checked the same way: starting
  with the agent after ITS owner, every other agent gets to respond to the new
  situation before it resolves. Once the stack is empty, the owner of the last
  resolved item keeps priority and carries on with its turn.
- Ending your turn is only allowed with an empty stack. An agent that ended
  its turn is out of the priority rotation until the next combat round.
  (Calling end turn while the stack has items counts as a pass.)
- Responders with nothing they could play are passed automatically, without
  asking the model (auto_pass=True).
- Other screens (map, rewards, events, ...) are not affected: agents act on
  them directly as before.

The shared state is one JSON file, read and rewritten under a file lock. Every
agent in the session must point at the same file (run_multiplayer.py makes a
fresh one per session and passes it as --comms-path).
"""

import asyncio
import json
import os
import sys
import time
from contextlib import contextmanager
from pathlib import Path
from types import SimpleNamespace

from comms import Comms

try:
    import fcntl
except ImportError:  # Windows
    fcntl = None

DEFAULT_STACK_PATH = Path(__file__).resolve().parent / ".stack" / "stack.json"

STACK_STATES = {"monster", "elite", "boss"}
# Screens that can appear mid-combat: keep this agent's stack items while on them.
KEEP_STATES = {"hand_select", "card_select", "overlay", "unknown"}
STACKABLE = {"combat_play_card", "use_potion"}

POLL_SECONDS = 0.5       # how often a waiting agent re-reads the stack file
WAIT_SLICE = 2.0         # max wait inside before_turn before the loop refreshes game state
STALE_AFTER = 90         # no heartbeat for this long = that agent is out of the rotation
HOLDER_TIMEOUT = 180     # priority holder silent this long = counts as a pass
RESOLVE_TIMEOUT = 90     # item marked resolving but not resolved by its owner = fizzles
ITEM_MAX_AGE = 900       # leftover items older than this are dropped
REGISTRY_MAX_AGE = 600   # agents not seen for this long leave the seat order
KEEP_RESOLVED = 30       # resolution history kept in the file
ARRIVAL_GRACE = 30       # at combat start, wait this long for every player's agent to check in

STACK_PASS_TOOL = SimpleNamespace(
    name="stack_pass",
    description=(
        "Pass priority: you don't respond to the top item of the stack. Once every "
        "other player has passed too, the top item resolves. Use this when nothing you "
        "could play would be better played BEFORE that item."
    ),
    inputSchema={"type": "object", "properties": {}},
)


def _base(tool_name: str) -> str:
    return tool_name[3:] if tool_name.startswith("mp_") else tool_name


def _int(v):
    try:
        return int(v)
    except (TypeError, ValueError):
        return None


def _parse(text: str):
    try:
        return json.loads(text)
    except (json.JSONDecodeError, TypeError):
        return None


def _ok(text: str) -> bool:
    parsed = _parse(text)
    return not (isinstance(parsed, dict) and parsed.get("status") == "error")


def _message(text: str) -> str:
    parsed = _parse(text)
    if isinstance(parsed, dict) and parsed.get("message"):
        return str(parsed["message"])
    return (text or "").strip()[:150]


def _result(status: str, message: str) -> str:
    return json.dumps({"status": status, "message": message})


class _FileLock:
    """Exclusive lock on <stack file>.lock for one read-modify-write."""

    def __init__(self, path: Path):
        self.lock_path = path.with_name(path.name + ".lock")

    def __enter__(self):
        self.f = open(self.lock_path, "a+")
        if fcntl:
            fcntl.flock(self.f.fileno(), fcntl.LOCK_EX)
        else:
            import msvcrt
            while True:
                try:
                    self.f.seek(0)
                    msvcrt.locking(self.f.fileno(), msvcrt.LK_NBLCK, 1)
                    break
                except OSError:
                    time.sleep(0.05)
        return self

    def __exit__(self, *exc):
        if fcntl:
            fcntl.flock(self.f.fileno(), fcntl.LOCK_UN)
        else:
            import msvcrt
            self.f.seek(0)
            msvcrt.locking(self.f.fileno(), msvcrt.LK_UNLCK, 1)
        self.f.close()


class StackComms(Comms):
    name = "stack"

    def __init__(self, player_id: str, path: str | None = None, board_path: str | None = None,
                 auto_pass: bool = True, **options):
        super().__init__(player_id, **options)
        self.path = Path(path) if path else DEFAULT_STACK_PATH
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.auto_pass = auto_pass
        self.state: dict = {}
        self.view: dict | None = None  # shared data as of this agent getting priority
        self._log(f"using stack file {self.path}")

    # ------------------------------------------------------------------ file
    @staticmethod
    def _empty() -> dict:
        return {"stack": [], "holder": None, "holder_since": 0, "passes": [],
                "status": {}, "done": {}, "resolved": [], "next_id": 1}

    def _load(self) -> dict:
        d = self._empty()
        try:
            d.update(json.loads(self.path.read_text(encoding="utf-8")))
        except (FileNotFoundError, json.JSONDecodeError):
            pass
        return d

    def _save(self, d: dict) -> None:
        tmp = self.path.with_name(f"{self.path.name}.{self.player_id}.tmp")
        tmp.write_text(json.dumps(d), encoding="utf-8")
        os.replace(tmp, self.path)  # atomic: readers never see half a file

    @contextmanager
    def _txn(self):
        with _FileLock(self.path):
            d = self._load()
            yield d
            self._save(d)

    def _log(self, msg: str) -> None:
        print(f"[stack] {self.player_id} {msg}", file=sys.stderr, flush=True)

    # --------------------------------------------------------------- helpers
    @staticmethod
    def _floor(state: dict):
        return (state.get("run") or {}).get("floor")

    @staticmethod
    def _round(state: dict):
        return (state.get("battle") or {}).get("round")

    def _status_from(self, state: dict) -> dict:
        battle = state.get("battle") or {}
        p = state.get("player") or {}
        return {"ts": time.time(), "state_type": state.get("state_type"),
                "floor": self._floor(state), "round": battle.get("round"),
                "in_play": bool(battle.get("is_play_phase")),
                "character": p.get("character"), "hp": p.get("hp"), "max_hp": p.get("max_hp")}

    def _order(self, d: dict) -> list[str]:
        """Seat order: every recently seen agent, sorted by port."""
        now = time.time()
        ids = [pid for pid, s in d["status"].items()
               if now - s.get("ts", 0) < REGISTRY_MAX_AGE or pid == d.get("holder")]
        return sorted(ids, key=lambda x: (0, int(x), x) if x.isdigit() else (1, 0, x))

    @staticmethod
    def _is_done(d: dict, pid: str) -> bool:
        s = d["status"].get(pid) or {}
        return d["done"].get(pid) == [s.get("floor"), s.get("round")]

    def _active(self, d: dict, floor) -> list[str]:
        """Agents who can take priority now: in combat on this floor, in the play
        phase, turn not ended, heartbeat fresh (the holder may be mid model call)."""
        now = time.time()
        out = []
        for pid in self._order(d):
            s = d["status"].get(pid) or {}
            fresh = now - s.get("ts", 0) < STALE_AFTER or pid == d.get("holder")
            if (fresh and s.get("state_type") in STACK_STATES and s.get("in_play")
                    and s.get("floor") == floor and not self._is_done(d, pid)):
                out.append(pid)
        return out

    def _next_after(self, d: dict, pid: str, floor, skip=()) -> str | None:
        order = self._order(d)
        active = set(self._active(d, floor))
        if not order:
            return None
        i = order.index(pid) if pid in order else -1
        for k in range(1, len(order) + 1):
            cand = order[(i + k) % len(order)]
            if cand in active and cand not in skip:
                return cand
        return None

    @staticmethod
    def _set_holder(d: dict, pid: str | None) -> None:
        d["holder"] = pid
        d["holder_since"] = time.time()

    def _remove(self, d: dict, item: dict, outcome: str, message: str) -> None:
        d["stack"] = [x for x in d["stack"] if x.get("id") != item.get("id")]
        d["resolved"].append({k: item.get(k) for k in (
            "id", "owner", "character", "kind", "name", "type", "cost", "target_label",
            "floor", "round")} | {"outcome": outcome, "message": message, "ts": time.time()})
        d["resolved"] = d["resolved"][-KEEP_RESOLVED:]

    def _describe(self, it: dict) -> str:
        who = "you" if it.get("owner") == self.player_id else f"P{it.get('owner')}"
        char = it.get("character")
        who = f"{who} ({char})" if char else who
        if it.get("kind") == "card":
            act = f"play {it.get('name')} [{it.get('type')}, {it.get('cost')} energy]"
        else:
            act = f"use potion {it.get('name')}"
        tgt = f" -> {it['target_label']}" if it.get("target_label") else ""
        desc = (it.get("description") or "").replace("\n", " ").strip()
        return f"#{it.get('id')} {who}: {act}{tgt}" + (f". Effect: {desc}" if desc else "")

    # ------------------------------------------------------- state machine
    def _advance(self, d: dict, floor) -> None:
        """Bring the shared state up to date. Run by every agent on every poll,
        so a crashed or departed agent can't stall the others."""
        now = time.time()
        # Leftovers from an earlier combat, or ancient items, are dropped.
        for it in list(d["stack"]):
            old_floor = (floor is not None and it.get("floor") is not None
                         and it["floor"] < floor)
            if old_floor or now - it.get("ts", now) > ITEM_MAX_AGE:
                self._remove(d, it, "fizzled", "the combat it was played in ended first")

        top = d["stack"][-1] if d["stack"] else None
        if top and top.get("status") == "resolving":
            if now - top.get("resolving_since", now) <= RESOLVE_TIMEOUT:
                return  # its owner is resolving it
            self._remove(d, top, "fizzled", "its owner didn't resolve it in time")
            d["passes"] = [d["stack"][-1]["owner"]] if d["stack"] else []
            self._set_holder(d, None)

        # A holder who left combat, ended the turn or went silent counts as passing.
        h = d.get("holder")
        if h is not None:
            s = d["status"].get(h) or {}
            st = s.get("state_type")
            ok = ((st in STACK_STATES and s.get("in_play")) or st == "hand_select") \
                and not self._is_done(d, h) and now - d.get("holder_since", 0) < HOLDER_TIMEOUT
            if not ok:
                if d["stack"] and h not in d["passes"]:
                    d["passes"].append(h)
                self._set_holder(d, None)

        active = self._active(d, floor)
        if d["stack"]:
            if all(pid in d["passes"] for pid in active):
                top = d["stack"][-1]
                top["status"] = "resolving"
                top["resolving_since"] = now
                self._set_holder(d, None)
            elif d.get("holder") is None:
                self._set_holder(d, self._next_after(d, d["stack"][-1]["owner"], floor,
                                                     skip=d["passes"]))
        else:
            d["passes"] = []
            if d.get("holder") is None and active and not self._everyone_here(d, floor):
                return  # combat just started: don't let the first arrival act alone
            if d.get("holder") is None and active:
                # Empty stack, nobody holding: the round's starting seat rotates.
                order = self._order(d)
                rnd = _int((d["status"].get(active[0]) or {}).get("round")) or 1
                start = order[(rnd - 1) % len(order)]
                self._set_holder(d, start if start in active else self._next_after(d, start, floor))

    def _restart_check(self, d: dict, floor) -> None:
        """After the top item leaves the stack: the new top gets a fresh round of
        responses from everyone but its owner, starting after the owner. With
        an empty stack, this agent (who just resolved) keeps priority."""
        if d["stack"]:
            owner = d["stack"][-1]["owner"]
            d["passes"] = [owner]
            self._set_holder(d, self._next_after(d, owner, floor, skip=d["passes"]))
        else:
            d["passes"] = []
            self._set_holder(d, self.player_id)

    def _note_arrival(self, d: dict, state: dict) -> None:
        """Record how many players this combat should have (living players in the
        game's own players list), so priority waits for all of their agents."""
        floor = self._floor(state)
        players = [p for p in state.get("players") or [] if isinstance(p, dict)]
        expected = sum(1 for p in players if p.get("is_alive", True)) or 1
        a = d.get("arrival") or {}
        if a.get("floor") != floor:
            d["arrival"] = {"floor": floor, "since": time.time(), "expected": expected}
        else:
            a["expected"] = expected

    def _everyone_here(self, d: dict, floor) -> bool:
        a = d.get("arrival") or {}
        if a.get("floor") != floor or time.time() - a.get("since", 0) > ARRIVAL_GRACE:
            return True
        now = time.time()
        here = sum(1 for s in d["status"].values()
                   if now - s.get("ts", 0) < STALE_AFTER and s.get("floor") == floor
                   and s.get("state_type") in STACK_STATES | KEEP_STATES)
        return here >= a.get("expected", 1)

    def _do_pass(self, d: dict, floor) -> None:
        if self.player_id not in d["passes"]:
            d["passes"].append(self.player_id)
        self._set_holder(d, self._next_after(d, self.player_id, floor, skip=d["passes"]))
        self._advance(d, floor)

    # ------------------------------------------------------------ my items
    def _mine(self, d: dict) -> list[dict]:
        return [x for x in d["stack"] if x.get("owner") == self.player_id]

    def _committed_energy(self, d: dict) -> int:
        return sum(_int(x.get("cost")) or 0 for x in self._mine(d) if x.get("kind") == "card")

    def _can_respond(self, state: dict, d: dict) -> bool:
        """Is there anything this agent could put on the stack right now?"""
        p = state.get("player") or {}
        mine = self._mine(d)
        energy = _int(p.get("energy"))
        left = None if energy is None else energy - self._committed_energy(d)
        hand = [c for c in p.get("hand") or [] if isinstance(c, dict)]
        for c in hand:
            if c.get("can_play") is False:
                continue
            same = sum(1 for x in hand if x.get("id") == c.get("id"))
            if sum(1 for x in mine if x.get("card_id") == c.get("id")) >= same:
                continue
            cost = _int(c.get("cost"))
            if cost is None or left is None or cost <= left:
                return True
        pending_slots = {x["input"].get("slot") for x in mine if x.get("kind") == "potion"}
        return any(isinstance(x, dict) and x.get("can_use_in_combat") is not False
                   and x.get("slot") not in pending_slots for x in p.get("potions") or [])

    def _build_item(self, d: dict, tool_name: str, tool_input: dict, state: dict, floor):
        """Turn a play_card / use_potion call into a stack item, or explain why not."""
        p = state.get("player") or {}
        base = _base(tool_name)
        mine = self._mine(d)
        item = {"id": d["next_id"], "owner": self.player_id, "character": p.get("character"),
                "tool": tool_name, "input": dict(tool_input or {}), "floor": floor,
                "round": self._round(state), "ts": time.time(), "status": "pending"}

        if base == "combat_play_card":
            hand = [c for c in p.get("hand") or [] if isinstance(c, dict)]
            idx = _int(item["input"].get("card_index"))
            if idx is None or not 0 <= idx < len(hand):
                return None, (f"card_index {item['input'].get('card_index')!r} isn't in your hand "
                              f"(valid: 0-{len(hand) - 1}).")
            card = hand[idx]
            name = card.get("name")
            if card.get("can_play") is False:
                return None, f"{name} can't be played right now ({card.get('unplayable_reason') or 'unplayable'})."
            same = sum(1 for c in hand if c.get("id") == card.get("id"))
            if sum(1 for x in mine if x.get("card_id") == card.get("id")) >= same:
                return None, (f"{name} is already on the stack (it stays in your hand until it "
                              f"resolves). Play a different card, or pass.")
            cost, energy = _int(card.get("cost")), _int(p.get("energy"))
            committed = self._committed_energy(d)
            if cost is not None and energy is not None and committed + cost > energy:
                return None, (f"Not enough energy for {name} (cost {cost}): you have {energy} and "
                              f"{committed} is already committed to your cards on the stack.")
            item.update(kind="card", card_id=card.get("id"), name=name, cost=card.get("cost"),
                        type=card.get("type"), description=card.get("description"))
            target_type = card.get("target_type")
        else:
            slot = _int(item["input"].get("slot"))
            pot = next((x for x in p.get("potions") or []
                        if isinstance(x, dict) and x.get("slot") == slot), None)
            if pot is None:
                return None, f"No potion in slot {item['input'].get('slot')!r}."
            if pot.get("can_use_in_combat") is False:
                return None, f"{pot.get('name')} can't be used in combat."
            if any(x.get("kind") == "potion" and x["input"].get("slot") == slot for x in mine):
                return None, f"{pot.get('name')} is already on the stack."
            item["input"]["slot"] = slot
            item.update(kind="potion", potion_id=pot.get("id"), name=pot.get("name"),
                        type="Potion", cost=0, description=pot.get("description"))
            target_type = pot.get("target_type")

        target = item["input"].get("target")
        enemies = [e for e in (state.get("battle") or {}).get("enemies") or [] if isinstance(e, dict)]
        if target_type == "AnyEnemy":
            ids = ", ".join(str(e.get("entity_id")) for e in enemies) or "none"
            if not target:
                return None, f"{item['name']} needs a target: set target to an enemy's entity_id ({ids})."
            e = next((x for x in enemies if str(x.get("entity_id")).lower() == str(target).lower()), None)
            if e is None:
                return None, f"No enemy with entity_id {target!r}. Valid targets: {ids}."
            item["input"]["target"] = e["entity_id"]
            # combat_id doesn't change when another enemy dies; entity_id can.
            item["target"] = {"kind": "enemy", "entity_id": e["entity_id"],
                              "combat_id": e.get("combat_id"), "name": e.get("name")}
            item["target_label"] = f"{e.get('name')} ({e['entity_id']}, HP {e.get('hp')}/{e.get('max_hp')})"
        elif (base == "use_potion" and target_type in ("AnyAlly", "AnyPlayer")
              and isinstance(target, str) and target.lower().startswith("player_")):
            pl = next((x for x in state.get("players") or []
                       if isinstance(x, dict) and x.get("player_id") == target), None)
            if pl is None:
                return None, f"No player with player_id {target!r}."
            if pl.get("is_local"):
                item["input"].pop("target", None)
                item["target_label"] = "themselves"
            else:
                item["target"] = {"kind": "ally", "player_id": target, "name": pl.get("character")}
                item["target_label"] = (f"teammate {pl.get('character')} ({target}, "
                                        f"HP {pl.get('hp')}/{pl.get('max_hp')})")
        else:
            item["input"].pop("target", None)
            if target_type == "AllEnemies":
                item["target_label"] = "all enemies"
            elif target_type in ("Self", "AnyAlly", "AnyPlayer"):
                item["target_label"] = "themselves"
        return item, None

    def _rebind(self, item: dict, state: dict):
        """Map a stack item onto the CURRENT game state (hand indices shift as cards
        are played; enemy entity_ids renumber when one dies). Returns
        ((tool, args), "") or (None, reason it fizzles)."""
        p = state.get("player") or {}
        if item.get("kind") == "card":
            hand = [c for c in p.get("hand") or [] if isinstance(c, dict)]
            idx = _int(item["input"].get("card_index"))
            card = None
            if idx is not None and 0 <= idx < len(hand) and hand[idx].get("id") == item.get("card_id"):
                card = hand[idx]
            if card is None:
                matches = [c for c in hand if c.get("id") == item.get("card_id")]
                card = next((c for c in matches if c.get("can_play") is not False), None) \
                    or (matches[0] if matches else None)
            if card is None:
                return None, f"{item.get('name')} is no longer in hand"
            if card.get("can_play") is False:
                return None, f"{item.get('name')} can't be played now ({card.get('unplayable_reason') or 'unplayable'})"
            args = {"card_index": card.get("index", hand.index(card))}
        else:
            pots = [x for x in p.get("potions") or [] if isinstance(x, dict)]
            slot = item["input"].get("slot")
            pot = next((x for x in pots if x.get("slot") == slot and x.get("id") == item.get("potion_id")), None) \
                or next((x for x in pots if x.get("id") == item.get("potion_id")), None)
            if pot is None:
                return None, f"potion {item.get('name')} is gone"
            args = {"slot": pot.get("slot")}

        tgt = item.get("target")
        if tgt and tgt.get("kind") == "enemy":
            enemies = [e for e in (state.get("battle") or {}).get("enemies") or [] if isinstance(e, dict)]
            if tgt.get("combat_id") is not None:
                e = next((x for x in enemies if x.get("combat_id") == tgt["combat_id"]), None)
            else:
                e = next((x for x in enemies if x.get("entity_id") == tgt.get("entity_id")), None)
            if e is None or (isinstance(e.get("hp"), int) and e["hp"] <= 0):
                return None, f"its target {tgt.get('name')} is already dead"
            args["target"] = e["entity_id"]
        elif tgt and tgt.get("kind") == "ally":
            pl = next((x for x in state.get("players") or []
                       if isinstance(x, dict) and x.get("player_id") == tgt["player_id"]), None)
            if pl is None or pl.get("is_alive") is False:
                return None, f"its target {tgt.get('name')} is dead"
            args["target"] = tgt["player_id"]
        return (item["tool"], args), ""

    async def _resolve(self, item: dict, state: dict, execute, floor) -> None:
        call, why = self._rebind(item, state)
        if call is None:
            outcome, msg = "fizzled", why
        else:
            text = await execute(*call)
            if _ok(text):
                outcome, msg = "resolved", _message(text)
            else:
                outcome, msg = "fizzled", f"the game rejected it: {_message(text)}"
        with self._txn() as d:
            cur = next((x for x in d["stack"] if x.get("id") == item.get("id")), None)
            if cur is not None:
                self._remove(d, cur, outcome, msg)
            self._restart_check(d, floor)
            self._advance(d, floor)
        self._log(f"{outcome} #{item.get('id')} {item.get('name')}: {msg}")

    # ------------------------------------------------------------ interface
    def observe(self, state: dict) -> None:
        self.state = state

    async def before_turn(self, state: dict, execute) -> bool:
        self.state = state
        me = self.player_id
        st = state.get("state_type")
        floor = self._floor(state)

        if st not in STACK_STATES:
            self.view = None
            with self._txn() as d:
                d["status"][me] = self._status_from(state)
                if st not in KEEP_STATES:  # out of combat: my leftovers can't happen
                    for it in self._mine(d):
                        self._remove(d, it, "fizzled", "its owner left combat")
                    if d.get("holder") == me:
                        self._set_holder(d, None)
                self._advance(d, floor)
            return False

        deadline = time.monotonic() + WAIT_SLICE
        while True:
            with self._txn() as d:
                d["status"][me] = self._status_from(state)
                self._note_arrival(d, state)
                self._advance(d, floor)
                top = d["stack"][-1] if d["stack"] else None
                resolve_mine = (top if top and top.get("status") == "resolving"
                                and top.get("owner") == me else None)
                mine_now = d.get("holder") == me
                if mine_now and d["stack"] and self.auto_pass and not self._can_respond(state, d):
                    self._log(f"auto-pass on #{top.get('id')} (nothing playable)")
                    self._do_pass(d, floor)
                    mine_now = False
                snapshot = json.loads(json.dumps(d))
            if resolve_mine:
                await self._resolve(resolve_mine, state, execute, floor)
                return True  # game state changed: let the loop fetch it again
            if mine_now:
                self.view = snapshot
                return False
            if time.monotonic() >= deadline:
                return True
            await asyncio.sleep(POLL_SECONDS)

    def offered_tools(self, state: dict, tools: list) -> list:
        if (state.get("state_type") not in STACK_STATES or not self.view
                or self.view.get("holder") != self.player_id):
            return tools
        if self.view["stack"]:
            # Responding: play, use a potion, or pass. No ending the turn mid-stack.
            return [t for t in tools if _base(t.name) in STACKABLE] + [STACK_PASS_TOOL]
        return tools

    def intercept(self, tool_name: str, tool_input: dict, state: dict) -> str | None:
        if state.get("state_type") not in STACK_STATES:
            return None
        base = _base(tool_name)
        if base not in STACKABLE | {"stack_pass", "combat_end_turn"}:
            return None  # e.g. discard_potion happens directly
        floor = self._floor(state)
        with self._txn() as d:
            if d.get("holder") != self.player_id:
                return _result("error", "You don't have priority right now. Wait for your turn to act.")
            if base == "stack_pass":
                if not d["stack"]:
                    return _result("error", "The stack is empty, so there is nothing to pass on. "
                                            "Play a card, use a potion, or end your turn.")
                top_id = d["stack"][-1].get("id")
                self._do_pass(d, floor)
                msg = f"You passed priority on #{top_id}."
            elif base == "combat_end_turn":
                if not d["stack"]:
                    return None  # really end the turn; after_action records it
                self._do_pass(d, floor)
                msg = ("The stack isn't empty, so this counted as passing priority. You can end "
                       "your turn once the stack is empty.")
            else:
                item, err = self._build_item(d, tool_name, tool_input, state, floor)
                if err:
                    return _result("error", err)
                d["stack"].append(item)
                d["next_id"] += 1
                d["passes"] = [self.player_id]  # the player who acts doesn't get asked again
                self._set_holder(d, self._next_after(d, self.player_id, floor, skip=d["passes"]))
                self._advance(d, floor)
                desc = self._describe(item)
                msg = (f"Added to the stack: {desc.rstrip('.')}. It has NOT happened yet. It resolves once "
                       f"every other player passes, after anything they put on top of it.")
        self._log(msg)
        return _result("ok", msg)

    def after_action(self, tool_name: str, tool_input: dict, result_text: str, state: dict) -> None:
        if (state.get("state_type") in STACK_STATES and _base(tool_name) == "combat_end_turn"
                and _ok(result_text)):
            floor = self._floor(state)
            with self._txn() as d:
                d["done"][self.player_id] = [floor, self._round(state)]
                if d.get("holder") == self.player_id:
                    self._set_holder(d, self._next_after(d, self.player_id, floor))
                self._advance(d, floor)
            self._log("ended turn; out of the rotation until next round")

    def prompt_block(self) -> str:
        d = self.view
        if (self.state.get("state_type") not in STACK_STATES or not d
                or d.get("holder") != self.player_id):
            return ""
        floor, rnd = self._floor(self.state), self._round(self.state)
        lines = [
            "## The stack (co-op priority)",
            "Your actions don't happen when you choose them. A card you play or a potion you "
            "use goes onto a shared stack, and only resolves after every other player has had "
            "the chance to respond. The stack is last in, first out: the newest item resolves "
            "first. Teammates can't talk to you; the stack shows what they are about to do.",
        ]
        if d["stack"]:
            lines.append("Stack, from the top (resolves first) down:")
            for n, it in enumerate(reversed(d["stack"]), 1):
                lines.append(f"  {n}. {self._describe(it)}")
            lines.append(
                "You have priority. Respond by playing a card or using a potion: it goes on top "
                "and resolves BEFORE everything listed above. For example, apply Vulnerable or "
                "Weak before a teammate's attack lands, or block or heal before the danger. Or "
                "call stack_pass to let the top item resolve. You can't end your turn while the "
                "stack has items.")
        else:
            lines.append(
                "The stack is empty and you have priority. Play a card or use a potion (it goes "
                "on the stack, and teammates may respond before it resolves), or end your turn "
                "if you are done.")
        mine = self._mine(d)
        if mine:
            names = ", ".join(f"#{x.get('id')} {x.get('name')}" for x in mine)
            lines.append(f"Your items waiting on the stack: {names} ({self._committed_energy(d)} "
                         f"energy committed). Those cards still show in your hand until they "
                         f"resolve; don't play them again.")
        recent = [r for r in d["resolved"] if r.get("floor") == floor and r.get("round") == rnd][-6:]
        if recent:
            lines.append("Already resolved this turn (oldest first):")
            for r in recent:
                who = "you" if r.get("owner") == self.player_id else f"P{r.get('owner')}"
                tgt = f" -> {r['target_label']}" if r.get("target_label") else ""
                extra = f" ({r.get('message')})" if r.get("outcome") == "fizzled" else ""
                lines.append(f"  - {who} {r.get('name')}{tgt}: {r.get('outcome')}{extra}")
        mates = []
        for pid in self._order(d):
            if pid == self.player_id:
                continue
            s = d["status"].get(pid) or {}
            if s.get("floor") != floor:
                continue
            state_word = "ended turn" if self._is_done(d, pid) else "still acting"
            mates.append(f"  - P{pid} {s.get('character')}: HP {s.get('hp')}/{s.get('max_hp')}, {state_word}")
        if mates:
            lines.append("Teammates:")
            lines.extend(mates)
        return "\n".join(lines) + "\n\n"