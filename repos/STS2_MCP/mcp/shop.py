"""Harness-side help for shops (state_type "shop" and the "fake_merchant" event).

The state already has everything needed: the player's gold (state.player.gold)
and, per item, price / can_afford / is_stocked (can_afford is the game's own
check against your gold). Models still get stuck: they miss the gold figure in
a large state, try item after item they can't afford, or keep buying a potion
with a full belt (the mod doesn't refuse that one, so it can fail silently).

What this does, per agent:
- Prompt note: your gold, what you can buy right now (index, name, price) and
  what's too expensive, plus whether your potion belt is full.
- Refuses shop_purchase for an item that's sold out, too expensive, a potion
  with a full belt, or an index that doesn't exist, telling the model what it
  can buy instead (or to leave). The game is not called for those.
- Leaves the shop by itself (proceed_to_map, no model call) when nothing is
  buyable any more, or after repeated purchases that failed or had no effect.
"""

import json

STUCK_AFTER = 3  # failed / no-effect purchases in a row before the harness leaves


def _base(name: str) -> str:
    return name[3:] if name.startswith("mp_") else name


def _ok(text: str) -> bool:
    try:
        parsed = json.loads(text)
    except (TypeError, json.JSONDecodeError):
        return True
    return not (isinstance(parsed, dict) and parsed.get("status") == "error")


def _name(item: dict) -> str:
    cat = item.get("category")
    if cat == "card_removal":
        return "Card removal service"
    return str(item.get(f"{cat}_name") or item.get("card_name") or item.get("relic_name")
               or item.get("potion_name") or cat)


class ShopHelper:
    def __init__(self):
        self._reset(None)

    def _reset(self, key) -> None:
        self.key = key
        self.failed = 0
        self.pending = None  # (gold before, item index) of a purchase that reported ok
        self.last_reason = ""

    # ---------------------------------------------------------------- view
    @staticmethod
    def _view(state: dict) -> dict | None:
        st = state.get("state_type")
        if st == "shop":
            shop = state.get("shop")
        elif st == "fake_merchant":
            shop = (state.get("fake_merchant") or {}).get("shop")
        else:
            return None
        if not isinstance(shop, dict):
            return None
        p = state.get("player") or {}
        potions = [x for x in p.get("potions") or [] if isinstance(x, dict)]
        max_slots = p.get("max_potion_slots")
        belt_full = isinstance(max_slots, int) and len(potions) >= max_slots
        items = [i for i in shop.get("items") or [] if isinstance(i, dict)]
        gold = p.get("gold")
        buyable, too_expensive = [], []
        for i in items:
            if i.get("is_stocked") is False:
                continue
            price = i.get("price")
            affordable = i.get("can_afford")
            if affordable is None and isinstance(gold, int) and isinstance(price, int):
                affordable = price <= gold
            if not affordable:
                too_expensive.append(i)
            elif i.get("category") == "potion" and belt_full:
                continue  # can't hold it
            else:
                buyable.append(i)
        return {"items": items, "gold": gold, "belt_full": belt_full,
                "buyable": buyable, "too_expensive": too_expensive,
                "can_proceed": shop.get("can_proceed", True),
                "key": (st, (state.get("run") or {}).get("floor"))}

    @staticmethod
    def _fmt(items: list) -> str:
        return ", ".join(f"[{i.get('index')}] {_name(i)} ({i.get('category')}, {i.get('price')}g)"
                         for i in items) or "nothing"

    def observe(self, state: dict) -> None:
        v = self._view(state)
        if v is None:
            if self.key is not None:
                self._reset(None)
            return
        if v["key"] != self.key:
            self._reset(v["key"])
            return
        if self.pending is not None:
            gold_before, _idx = self.pending
            self.pending = None
            if isinstance(gold_before, int) and v["gold"] == gold_before:
                self.failed += 1  # reported ok, but no gold was spent: it didn't happen
            else:
                self.failed = 0

    # ---------------------------------------------------------- harness acts
    def auto_action(self, state: dict, valid_tools: list):
        """Leave the shop without asking the model when there's nothing left to do."""
        v = self._view(state)
        if v is None or not v["can_proceed"]:
            return None
        leave = next((t.name for t in valid_tools if _base(t.name) == "proceed_to_map"), None)
        if leave is None:
            return None
        if not v["buyable"]:
            self.last_reason = (f"nothing affordable left ({v['gold']} gold)"
                                + (", potion belt full" if v["belt_full"] else ""))
            return leave, {}
        if self.failed >= STUCK_AFTER:
            self.last_reason = f"{self.failed} purchases in a row failed or had no effect"
            return leave, {}
        return None

    # --------------------------------------------------------- model prompt
    def note(self, state: dict) -> str:
        v = self._view(state)
        if v is None:
            return ""
        lines = [f"Shop: you have {v['gold']} gold.",
                 f"You can buy now: {self._fmt(v['buyable'])}.",
                 f"Too expensive (more than your {v['gold']} gold): {self._fmt(v['too_expensive'])}."]
        if v["belt_full"]:
            lines.append("Your potion belt is full, so you can't buy potions.")
        lines.append("Only buy from the 'can buy now' list, and only what helps your deck. "
                     "Buying is optional: call proceed_to_map to leave when you're done.")
        return "\n".join(lines) + "\n"

    # -------------------------------------------------------- tool calls
    def intercept(self, tool_name: str, tool_input: dict, state: dict) -> str | None:
        v = self._view(state)
        if v is None or _base(tool_name) != "shop_purchase":
            return None
        idx = tool_input.get("item_index")
        item = next((i for i in v["items"] if i.get("index") == idx), None)
        options = (f"You can buy: {self._fmt(v['buyable'])}." if v["buyable"]
                   else "Nothing else is affordable: call proceed_to_map to leave.")
        if item is None:
            why = f"There is no shop item with index {idx!r}."
        elif item.get("is_stocked") is False:
            why = f"{_name(item)} is sold out."
        elif item in v["too_expensive"]:
            why = f"{_name(item)} costs {item.get('price')} gold and you have {v['gold']}."
        elif item.get("category") == "potion" and v["belt_full"]:
            why = f"Your potion belt is full, so you can't buy {_name(item)}."
        else:
            return None
        self.failed += 1
        return json.dumps({"status": "error", "message": f"{why} {options}"})

    def record(self, tool_name: str, tool_input: dict, result_text: str, state: dict) -> None:
        v = self._view(state)
        if v is None or _base(tool_name) != "shop_purchase":
            return
        if _ok(result_text):
            self.pending = (v["gold"], tool_input.get("item_index"))  # verified on next observe
        else:
            self.failed += 1