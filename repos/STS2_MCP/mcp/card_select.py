"""Harness-side help for card selection screens, so models don't stall on them.

Two kinds of screen:
- card_select (grid): upgrade / remove / transform / "choose N cards" screens,
  from events, rest sites, shops, relics, and in combat when a card makes you
  pick from your draw / discard / exhaust pile. Tools: deck_select_card (it
  TOGGLES) and deck_confirm_selection. The mod doesn't report which cards are
  selected, so this helper tracks the agent's own picks. Upgrade / transform
  screens add a preview that needs its own confirm. screen_type "choose"
  ("choose a card") picks immediately and has nothing to confirm.
- hand_select: in combat, a card effect asking you to pick cards from your
  hand (discard, exhaust, upgrade, ...). Tools: combat_select_card and
  combat_confirm_selection. The game reports selected_cards, and a selected
  card leaves the "cards" list, so the remaining indexes shift.

When the harness confirms by itself (no model call), in order:
1. A preview is showing and confirm is enabled (grid screens).
2. Confirm was DISABLED when the screen opened and is enabled now, after at
   least one pick: the game itself is saying the selection is complete. This
   is the main rule; it doesn't depend on the prompt's wording. (A screen is
   identified by its prompt and mode only, never by which cards are
   selected, so (un)selecting can't make it look like a new screen.) (Except when
   the prompt says "up to N" and fewer than N are picked: the model may
   still add cards.)
3. The prompt states a count ("Choose 2 cards", "a card", "up to 3") and that
   many are selected, with confirm enabled.
4. Safety net: the model made two failed selection attempts in a row on the
   screen while confirm is enabled.
5. hand_select only: no selectable cards are left and confirm is enabled.
Screens where confirm is enabled from the start ("any number of cards")
are left to the model unless rule 3 or 4 applies.

The model also gets a note with the prompt, what's selected so far and when to
confirm, and a selection that would un-select a card (grid) or go over a
stated count is refused with an explanation instead of reaching the game.
"""

import json
import re

NUMBER_WORDS = {"a": 1, "an": 1, "one": 1, "two": 2, "three": 3, "four": 4, "five": 5,
                "six": 6, "seven": 7, "eight": 8, "nine": 9, "ten": 10}
UP_TO_RE = re.compile(r"\bup\s+to\s+(\d+|\w+)\b", re.IGNORECASE)
COUNT_RE = re.compile(r"\b(\d+|a|an|one|two|three|four|five|six|seven|eight|nine|ten)\s+cards?\b",
                      re.IGNORECASE)
MAX_AUTO_CONFIRMS = 3   # per screen (selection, preview, one retry); then the model decides
STUCK_AFTER = 2         # failed selection attempts in a row before the safety net confirms


def _base(name: str) -> str:
    return name[3:] if name.startswith("mp_") else name


def _num(word: str):
    return int(word) if word.isdigit() else NUMBER_WORDS.get(word.lower())


def required_count(prompt: str):
    """(n, exact) from the screen prompt, e.g. "Choose 2 cards to Remove." ->
    (2, True); "Choose up to 3 cards" -> (3, False); unknown -> (None, False)."""
    prompt = prompt or ""
    m = UP_TO_RE.search(prompt)
    if m and _num(m.group(1)):
        return _num(m.group(1)), False
    m = COUNT_RE.search(prompt)
    if m and _num(m.group(1)):
        return _num(m.group(1)), True
    return None, False


def _ok(text: str) -> bool:
    try:
        parsed = json.loads(text)
    except (TypeError, json.JSONDecodeError):
        return True
    return not (isinstance(parsed, dict) and parsed.get("status") == "error")


class CardSelectHelper:
    def __init__(self):
        self._reset(None)

    def _reset(self, key) -> None:
        self.key = key
        self.picked: list[tuple[int, str]] = []  # grid screens: (index, name) this agent selected
        self.auto_confirms = 0
        self.confirm_at_start = None  # can_confirm when the screen was first seen with nothing picked
        self.failed_selects = 0
        self.last_reason = ""
        self.last_pick = None  # hand screens: (index, name) of the last successful selection

    # ---------------------------------------------------------------- view
    def _view(self, state: dict) -> dict | None:
        """One shape for both screen kinds, or None when neither is up."""
        st = state.get("state_type")
        if st == "hand_select" and isinstance(state.get("hand_select"), dict):
            hs = state["hand_select"]
            cards = [c for c in hs.get("cards") or [] if isinstance(c, dict)]
            selected = [c.get("name") for c in hs.get("selected_cards") or [] if isinstance(c, dict)]
            return {"kind": "hand", "label": f"Hand selection ({hs.get('mode')})",
                    "prompt": hs.get("prompt") or "", "cards": cards, "selected": selected,
                    "can_confirm": bool(hs.get("can_confirm")), "preview": False,
                    "select": "combat_select_card", "confirm": "combat_confirm_selection",
                    # Nothing that changes when cards are (un)selected may go in the key:
                    # a key change resets confirm_at_start, which disables rule 2.
                    "key": ("hand", hs.get("prompt"), hs.get("mode"))}
        if st == "card_select" and isinstance(state.get("card_select"), dict):
            cs = state["card_select"]
            cards = [c for c in cs.get("cards") or [] if isinstance(c, dict)]
            kind = "choose" if cs.get("screen_type") == "choose" else "grid"
            return {"kind": kind, "label": f"Card selection ({cs.get('screen_type')})",
                    "prompt": cs.get("prompt") or "", "cards": cards,
                    "selected": [name for _, name in self.picked],
                    "can_confirm": bool(cs.get("can_confirm")), "can_skip": cs.get("can_skip"),
                    "preview": bool(cs.get("preview_showing")),
                    "select": "deck_select_card", "confirm": "deck_confirm_selection",
                    "key": (cs.get("screen_type"), cs.get("prompt"),
                            tuple(c.get("id") for c in cards))}
        return None

    def observe(self, state: dict) -> None:
        """Start fresh whenever a different selection screen is up."""
        v = self._view(state)
        if v is None:
            if self.key is not None:
                self._reset(None)
            return
        if v["key"] != self.key:
            self._reset(v["key"])
            v = self._view(state)  # picked was just cleared
        if self.confirm_at_start is None and not v["selected"]:
            self.confirm_at_start = v["can_confirm"]

    def _complete(self, v: dict) -> str | None:
        """Why the selection counts as finished, or None (see module docstring)."""
        if v["preview"] and v["can_confirm"]:
            return "preview showing"
        if not v["can_confirm"]:
            return None
        n, exact = required_count(v["prompt"])
        k = len(v["selected"])
        picked_something = k >= 1 or self.last_pick is not None
        if picked_something and self.confirm_at_start is False \
                and not (n is not None and not exact and k < n):
            return "the game enabled confirm after the picks"
        if n is not None and k >= n:
            return f"{k} of {n} card(s) selected"
        if self.failed_selects >= STUCK_AFTER:
            return f"{self.failed_selects} failed selection attempts in a row"
        if v["kind"] == "hand" and not v["cards"]:
            return "no selectable cards left"
        return None

    # ---------------------------------------------------------- harness acts
    def auto_action(self, state: dict, valid_tools: list):
        """A confirm the harness should make without asking the model, or None."""
        v = self._view(state)
        if v is None or v["kind"] == "choose" or self.auto_confirms >= MAX_AUTO_CONFIRMS:
            return None
        confirm = next((t.name for t in valid_tools if _base(t.name) == v["confirm"]), None)
        reason = self._complete(v) if confirm else None
        if not reason:
            return None
        self.auto_confirms += 1
        self.last_reason = reason
        return confirm, {}

    # --------------------------------------------------------- model prompt
    def note(self, state: dict) -> str:
        v = self._view(state)
        if v is None:
            return ""
        if v["kind"] == "choose":
            return (f"Card choice: \"{v['prompt']}\" Pick ONE card with deck_select_card; it "
                    f"takes effect immediately and there is nothing to confirm. "
                    + ("You may skip with deck_cancel_selection.\n" if v.get("can_skip") else "\n"))
        n, exact = required_count(v["prompt"])
        want = (f"exactly {n}" if exact else f"up to {n}") if n is not None else \
            "the number of cards the prompt asks for"
        sel = v["selected"]
        lines = [f"{v['label']}: \"{v['prompt']}\"",
                 f"Select {want} card(s) with {v['select']}, then call {v['confirm']}. "
                 f"Selected so far ({len(sel)}): {', '.join(sel) or 'none yet'}."]
        if v["kind"] == "hand":
            lines.append("Selecting a card can shift the indexes of the others: use the "
                         "indexes in the current state below. Never select the same card twice: "
                         "if it is still listed, selecting it again UN-selects it.")
        else:
            lines.append(f"{v['select']} TOGGLES a card: calling it on a card you already "
                         f"selected UN-selects it. Only select cards you haven't selected yet.")
        if v["can_confirm"] and sel:
            lines.append(f"The game will accept this selection now: call {v['confirm']} unless "
                         f"you deliberately want to select more.")
        elif v["can_confirm"]:
            lines.append(f"Confirm is available: call {v['confirm']} once you're done selecting.")
        return "\n".join(lines) + "\n"

    # -------------------------------------------------------- tool calls
    def intercept(self, tool_name: str, tool_input: dict, state: dict) -> str | None:
        """Refuse selections that would undo or overshoot the selection."""
        v = self._view(state)
        if v is None or v["kind"] == "choose" or v["preview"] or _base(tool_name) != v["select"]:
            return None
        if v["kind"] == "hand" and self.last_pick is not None:
            idx = tool_input.get("card_index")
            name = next((c.get("name") for c in v["cards"] if c.get("index") == idx), None)
            if (idx, name) == self.last_pick and name in v["selected"]:
                return json.dumps({"status": "error", "message": (
                    f"[{idx}] {name} is the card you just selected, and it is still selected; "
                    f"selecting it again would UN-select it. Call {v['confirm']} to confirm the "
                    f"selection, or select a different card.")})
        if v["kind"] == "grid":
            idx = tool_input.get("card_index")
            names = dict(self.picked)
            if idx in names:
                return json.dumps({"status": "error", "message": (
                    f"[{idx}] {names[idx]} is already selected; selecting it again would "
                    f"UN-select it. Select a different card, or call {v['confirm']} if you're done.")})
        n, exact = required_count(v["prompt"])
        k = len(v["selected"])
        if n is not None and k >= n:
            return json.dumps({"status": "error", "message": (
                f"You've already selected {k} card(s) ({', '.join(v['selected'])}), which is all "
                f"this asks for. Call {v['confirm']}.")})
        return None

    def record(self, tool_name: str, tool_input: dict, result_text: str, state: dict) -> None:
        v = self._view(state)
        if v is None:
            return
        base = _base(tool_name)
        if base == v["select"]:
            if not _ok(result_text):
                self.failed_selects += 1
                return
            self.failed_selects = 0
            idx = tool_input.get("card_index")
            name = next((c.get("name") for c in v["cards"] if c.get("index") == idx), "?")
            if v["kind"] == "grid":
                if idx not in dict(self.picked):
                    self.picked.append((idx, name))
            else:
                self.last_pick = (idx, name)
        elif base in (v["confirm"], "deck_cancel_selection") and _ok(result_text):
            # Screen done: forget it, so a following selection with the same prompt
            # (e.g. two discards in a row) starts fresh.
            self._reset(None)