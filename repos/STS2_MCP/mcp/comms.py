"""Agent-to-agent communication methods, selected with agent_loop.py --comms.

Every method implements the same interface, so agent_loop.py never needs to
know which one is active:

    observe(state)          called every turn with the current game state
    prompt_block() -> str   text added to this turn's prompt ("" = nothing)
    side_tools() -> list    extra tools (OpenAI format) the model may call this
                            turn WITHOUT using up its game action
    handle_tool(name, args) -> str
                            runs one of those side tools, returns the result
                            text the model sees
    send(message) / receive() -> list[str]
                            plain message passing, for methods that want it
    close()                 release files/sockets on shutdown

Methods:
    none         agents never talk (default)
    blackboard   shared board file; agents post intents and their hand
    shared-file, direct, broker
                 placeholders: warn once, then behave like "none"

To add a method: subclass Comms, override what you need, and register it in
_METHODS at the bottom. It then appears automatically as a --comms choice.
"""

import json
import sys
import time
from pathlib import Path

try:
    import fcntl  # Linux/macOS: lock the board file while appending
except ImportError:  # Windows: appends of one short line are still close to atomic
    fcntl = None

DEFAULT_BOARD_PATH = Path(__file__).resolve().parent / ".blackboard" / "board.jsonl"
COMBAT_STATES = {"monster", "elite", "boss", "hand_select"}


class Comms:
    """Base class. Default behaviour is silent: no messages, no extra tools."""

    name = "none"

    def __init__(self, player_id: str, **options):
        self.player_id = player_id  # e.g. the client's port, used to label posts
        self.options = options      # method-specific settings

    def observe(self, state: dict) -> None:
        pass

    def prompt_block(self) -> str:
        incoming = self.receive()
        if not incoming:
            return ""
        return ("Messages from teammates (oldest first):\n"
                + "\n".join(f"- {m}" for m in incoming) + "\n")

    def side_tools(self) -> list[dict]:
        return []

    def handle_tool(self, name: str, args: dict) -> str:
        return f"Unknown comms tool {name!r}."

    def send(self, message: str) -> None:
        pass

    def receive(self) -> list[str]:
        return []

    def close(self) -> None:
        pass


class NoComms(Comms):
    """Agents play independently and never talk."""
    name = "none"


# ---------------------------------------------------------------------------
# Blackboard
# ---------------------------------------------------------------------------

BOARD_POST_TOOL = {
    "type": "function",
    "function": {
        "name": "board_post",
        "description": (
            "Post to the shared co-op blackboard that your teammates read before they act. "
            "Say what you intend to do (which enemy you're attacking, whether you're blocking "
            "or setting up) and anything you want from them. Your HP, energy, current hand "
            "and potions are attached automatically, so don't list your whole hand. Mention a card only "
            "when it matters to them, e.g. 'I can apply Vulnerable to the Cultist'. "
            "This does NOT take a game action: you must still call a game tool afterwards."
        ),
        "parameters": {
            "type": "object",
            "properties": {
                "intent": {
                    "type": "string",
                    "description": "Your plan for this turn or room, one short sentence.",
                },
                "note": {
                    "type": "string",
                    "description": "Optional: a suggestion, request or reply to a teammate's post.",
                },
            },
            "required": ["intent"],
        },
    },
}


def _snapshot(state: dict) -> dict:
    """Facts about this player, taken from the game state rather than the model."""
    p = state.get("player") or {}
    snap = {
        "character": p.get("character"),
        "hp": p.get("hp"), "max_hp": p.get("max_hp"),
        "block": p.get("block"),
        "energy": p.get("energy"), "max_energy": p.get("max_energy"),
    }
    hand = p.get("hand")
    if isinstance(hand, list):
        snap["hand"] = [
            f"{c.get('name')}({c.get('cost')})" + ("" if c.get("can_play", True) else "x")
            for c in hand if isinstance(c, dict)
        ]
    potions = p.get("potions")
    if isinstance(potions, list):
        snap["potions"] = [x.get("name") for x in potions if isinstance(x, dict)]
    return {k: v for k, v in snap.items() if v is not None}


def _format_snapshot(s: dict) -> str:
    parts = []
    if "hp" in s:
        parts.append(f"HP {s['hp']}/{s.get('max_hp', '?')}")
    if s.get("block"):
        parts.append(f"Block {s['block']}")
    if "energy" in s:
        parts.append(f"Energy {s['energy']}/{s.get('max_energy', '?')}")
    if "hand" in s:
        parts.append("Hand: " + (", ".join(s["hand"]) or "empty"))
    if s.get("potions"):
        parts.append("Potions: " + ", ".join(s["potions"]))
    return " | ".join(parts)


class BlackboardComms(Comms):
    """A shared JSONL file every agent on this machine reads and appends to.

    - Each turn the board's recent posts for the current floor are shown to the
      model (its own posts are marked "you").
    - The model can call board_post (a side tool, not a game action) to share
      its intent; the harness attaches its real HP/energy/hand.
    - Posting is limited per combat round (or per room outside combat) so the
      board doesn't fill up with a post for every card played.

    Options: board_path (str), max_shown (int, default 10),
             posts_per_round (int, default 2).
    """

    name = "blackboard"

    def __init__(self, player_id: str, board_path: str | None = None,
                 max_shown: int = 10, posts_per_round: int = 2, **options):
        super().__init__(player_id, **options)
        self.path = Path(board_path) if board_path else DEFAULT_BOARD_PATH
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.touch(exist_ok=True)
        self.max_shown = max_shown
        self.posts_per_round = posts_per_round
        self.run_start = time.time()  # posts older than our current run are ignored
        self.state: dict = {}
        self.in_run = False
        self._posted: dict[tuple, int] = {}
        print(f"[board] {self.player_id} using blackboard {self.path}", file=sys.stderr)

    # -- context -------------------------------------------------------------
    def observe(self, state: dict) -> None:
        self.state = state
        in_run = "run" in state
        if not in_run and self.in_run:
            self.run_start = time.time()  # back at the menu: next run starts a clean board
            self._posted.clear()
        self.in_run = in_run

    def _floor(self):
        return (self.state.get("run") or {}).get("floor")

    def _round(self):
        return (self.state.get("battle") or {}).get("round")

    def _slot(self) -> tuple:
        """Posting budget key: one per combat round, or per room outside combat."""
        st = self.state.get("state_type")
        if st in COMBAT_STATES:
            return (self._floor(), "combat", self._round())
        return (self._floor(), st)

    # -- file I/O ------------------------------------------------------------
    def _append(self, post: dict) -> None:
        line = json.dumps(post, ensure_ascii=False) + "\n"
        with open(self.path, "a", encoding="utf-8") as f:
            if fcntl:
                fcntl.flock(f, fcntl.LOCK_EX)
            f.write(line)
            f.flush()
            if fcntl:
                fcntl.flock(f, fcntl.LOCK_UN)

    def _read(self, tail: int = 300) -> list[dict]:
        try:
            lines = self.path.read_text(encoding="utf-8").splitlines()[-tail:]
        except FileNotFoundError:
            return []
        posts = []
        for ln in lines:
            try:
                posts.append(json.loads(ln))
            except json.JSONDecodeError:
                continue  # a half-written line from another agent; skip it
        return posts

    def _relevant(self) -> list[dict]:
        floor = self._floor()
        posts = [p for p in self._read()
                 if p.get("ts", 0) >= self.run_start
                 and (floor is None or p.get("floor") == floor)]
        return posts[-self.max_shown:]

    # -- interface -----------------------------------------------------------
    def side_tools(self) -> list[dict]:
        if not self.in_run or self._posted.get(self._slot(), 0) >= self.posts_per_round:
            return []
        return [BOARD_POST_TOOL]

    def handle_tool(self, name: str, args: dict) -> str:
        if name != "board_post":
            return super().handle_tool(name, args)
        slot = self._slot()
        if self._posted.get(slot, 0) >= self.posts_per_round:
            return "You've already posted enough this round. Call one game tool now."
        intent = str(args.get("intent") or "").strip()
        if not intent:
            return "Post rejected: 'intent' is required. Call one game tool now."
        post = {
            "ts": time.time(),
            "player": self.player_id,
            "floor": self._floor(),
            "round": self._round(),
            "state_type": self.state.get("state_type"),
            "intent": intent[:300],
            "note": str(args.get("note") or "").strip()[:300],
            "snapshot": _snapshot(self.state),
        }
        self._append(post)
        self._posted[slot] = self._posted.get(slot, 0) + 1
        print(f"[board] {self.player_id} posted: {post['intent']}"
              + (f" | {post['note']}" if post["note"] else ""), file=sys.stderr)
        return "Posted to the blackboard. Now call one game tool."

    def receive(self) -> list[str]:
        out = []
        for p in self._relevant():
            who = "you" if p.get("player") == self.player_id else f"P{p.get('player')}"
            char = (p.get("snapshot") or {}).get("character")
            where = f"round {p['round']}" if p.get("round") is not None else (p.get("state_type") or "")
            head = f"[{who}{' ' + char if char else ''}, {where}]"
            body = f"intent: {p.get('intent')}"
            if p.get("note"):
                body += f" | note: {p['note']}"
            snap = _format_snapshot(p.get("snapshot") or {})
            out.append(f"{head} {body}" + (f" ({snap})" if snap else ""))
        return out

    def send(self, message: str) -> None:
        """Programmatic post (no model involved), e.g. for harness announcements."""
        self.handle_tool("board_post", {"intent": message})

    def prompt_block(self) -> str:
        if not self.in_run:
            return ""
        lines = self.receive()
        can_post = bool(self.side_tools())
        header = f"## Co-op blackboard (floor {self._floor()})\n"
        body = ("\n".join(lines) if lines else "(no posts yet this floor)") + "\n"
        tail = ("Read your teammates' intents before acting and coordinate (e.g. focus the "
                "same enemy, cover a teammate who is low). "
                + ("You may call board_post first to share your own plan; it is not a game "
                   "action.\n" if can_post else "You've used your posts for now.\n"))
        return header + body + tail + "\n"


# ---------------------------------------------------------------------------
# Placeholders
# ---------------------------------------------------------------------------

class _Placeholder(Comms):
    """Shared behaviour for methods that aren't written yet: warn once, then no-op."""

    def __init__(self, player_id: str, **options):
        super().__init__(player_id, **options)
        print(f"[comms] '{self.name}' is not implemented yet; running with no "
              f"communication.", file=sys.stderr)


class SharedFileComms(_Placeholder):
    """TODO: agents append to / read from a shared file (e.g. a JSONL log)."""
    name = "shared-file"


class DirectComms(_Placeholder):
    """TODO: agents talk to each other directly (e.g. sockets between processes)."""
    name = "direct"


class BrokerComms(_Placeholder):
    """TODO: agents talk through a central broker (e.g. run_multiplayer.py relays)."""
    name = "broker"


_METHODS: dict[str, type[Comms]] = {
    cls.name: cls for cls in (NoComms, BlackboardComms, SharedFileComms, DirectComms, BrokerComms)
}
COMMS_METHODS = list(_METHODS)


def build_comms(method: str, player_id: str, **options) -> Comms:
    try:
        cls = _METHODS[method]
    except KeyError:
        raise ValueError(f"unknown comms method {method!r}; choose from {COMMS_METHODS}") from None
    options = {k: v for k, v in options.items() if v is not None}
    return cls(player_id, **options)