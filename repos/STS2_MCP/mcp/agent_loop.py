#!/usr/bin/env python3
"""Autonomous STS2 agent loop.

Connects to ONE server.py instance (one game client) and drives it in a
loop: read state -> filter tools to what's valid for that state_type ->
let the LLM pick one -> execute -> repeat.

Run one of these per client (per port) for a fully autonomous N-player
co-op session. Nothing here is co-op-specific beyond which state_type
values you branch on (mp_* vs sp_* tool names) -- wire up whichever your
lobby is actually using.

This is a starting skeleton, not production code: no retry/backoff, no
persistence across crashes, minimal error handling.
"""

import argparse
import asyncio
import json
import random
import re
import sys
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client
from knowledge import SEARCH_TOOL, search_knowledge, preload

# States where the search_knowledge tool is offered: decisions about
# cards/relics/potions, plus map and rest-site planning. Combat is left out
# on purpose (a lookup every turn is slow).
LOOKUP_STATES = {
    "card_reward", "rewards", "shop", "bundle_select", "event",
    "map", "rest_site", "relic_select", "treasure", "card_select",
}
MAX_SEARCH_ROUNDS = 2

# Character IDs offered at character select; must match the IDs the menu lists.
CHARACTERS = ["IRONCLAD", "SILENT", "DEFECT", "NECROBINDER", "REGENT"]

# The run must start at this ascension; the loop stops if the game reports otherwise.
EXPECTED_ASCENSION = 0

# Error text meaning "not your turn yet" rather than a model mistake.
WAIT_ERRORS = ("Not in play phase", "enemy turn")


class GameUnreachable(RuntimeError):
    """server.py could not reach the STS2_MCP mod (wrong --port, game closed, ...)."""

# agent_loop.py is expected to live in the same directory as server.py
# (e.g. STS2_MCP/mcp/). Resolving this from the script's own location means
# it works regardless of which directory you launch it from.
MCP_SERVER_DIR = Path(__file__).resolve().parent

# ---------------------------------------------------------------------------
# Map each state_type to the subset of tool names that are actually valid
# there. Keeping this tight is the main lever against invalid/hallucinated
# tool calls -- the model can't pick a tool it isn't offered.
#
# Verified against the real server.py tool list via MCP Inspector as of
# this writing. Remaining gaps are marked TODO below (card_reward's skip
# button).
# ---------------------------------------------------------------------------
STATE_TOOL_MAP: dict[str, list[str]] = {
    # Confirmed against the real server.py tool list via MCP Inspector.
    # menu_select covers main menu, character_select, and game_over's
    # return-to-menu -- its own description confirms all three.
    "menu": ["menu_select"],
    "game_over": ["menu_select"],

    # event_choose_option covers Neow, regular events, AND clicking
    # "Proceed" after an event resolves -- NOT proceed_to_map, despite
    # what the name might suggest. Confirmed via the tool's own description.
    "event": ["event_choose_option", "event_advance_dialogue"],

    # map_choose_node, not proceed_to_map -- this was the original bug.
    # proceed_to_map is for rewards/rest_site/shop/fake_merchant instead.
    "map": ["map_choose_node"],

    # Combat: real tool names are combat_play_card / combat_end_turn, not
    # play_card / end_turn (also a fix from the original guessed map).
    "monster": ["combat_play_card", "use_potion", "discard_potion", "combat_end_turn"],
    "elite": ["combat_play_card", "use_potion", "discard_potion", "combat_end_turn"],
    "boss": ["combat_play_card", "use_potion", "discard_potion", "combat_end_turn"],

    # In-combat card-selection overlay (exhaust/discard prompts, etc.).
    # Distinct from the out-of-combat "card_select" overlay below, which
    # uses the deck_* tools.
    "hand_select": ["combat_select_card", "combat_confirm_selection"],

    # Post-combat rewards screen: claim gold/potion/relic immediately,
    # or proceed once done. Card rewards open a separate card_reward screen.
    "rewards": ["rewards_claim", "proceed_to_map"],
    "card_reward": ["rewards_pick_card", "rewards_skip_card"],

    "bundle_select": ["bundle_select", "bundle_confirm_selection", "bundle_cancel_selection"],

    # Confirmed via proceed_to_map's own description: "Works from: rewards
    # screen, rest site, shop, fake merchant." Does NOT work for events.
    # Tool names below are taken from server.py's function list.
    "rest_site": ["rest_choose_option", "proceed_to_map"],
    "shop": ["shop_purchase", "proceed_to_map"],
    "fake_merchant": ["shop_purchase", "proceed_to_map"],

    # Treasure room. UNVERIFIED: proceed_to_map's docstring may not list the
    # treasure room as a screen it works from -- check server.py (~line 364)
    # if the agent gets stuck after claiming the relic.
    "treasure": ["treasure_claim_relic", "proceed_to_map"],

    # Out-of-combat deck card selection (upgrade / remove / transform / choose-a-card).
    "card_select": ["deck_select_card", "deck_confirm_selection", "deck_cancel_selection"],

    # Boss relic choice.
    "relic_select": ["relic_select", "relic_skip"],

    # Crystal Sphere minigame: only offer "proceed" so the agent doesn't
    # waste time clicking cells blindly. Add the other two tools to play it.
    "crystal_sphere": ["crystal_sphere_proceed"],

    # Multiplayer: this map holds singleplayer names. In a co-op run they are
    # translated by resolve_tool_names() (mp_ prefix, map_choose_node ->
    # mp_map_vote), so there is no second copy to maintain.
}

# game_over is no longer terminal by itself: the loop returns to the main menu
# and starts another run until --runs is reached (see run_agent_loop).
TERMINAL_STATES: set[str] = set()  # add any state that should always stop the loop

SYSTEM_PROMPT = """You are playing Slay the Spire 2. You will be shown the
current game state and a list of tools valid for that state. Call exactly
one game tool per turn (A search_knowledge lookup beforehand doesn't count). 
Always call a tool -- never just respond with text.

## Your goal
Progress through the game by defeating enemies. Losing all HP will
normally result in a loss. During combat, evaluate what benefits and
penalties are received from not playing cards, and weigh what will be
best to keep your HP higher while defeating enemies. Example: an enemy
is about to attack for 7 damage, and you could either block all of it or,
if it has low enough health, defeat it outright instead. It is likely
more advantageous to go ahead and defeat it rather than block. You can
strengthen your deck by acquiring cards from combat rewards and the
shops.

## Multi-step flows -- these require TWO tool calls across TWO turns, not one
- **Character select**: first call menu_select with the character's ID
  (e.g. "IRONCLAD") to choose them. This does NOT start the run by
  itself. On your NEXT turn, once that character is selected, call
  menu_select again with option "embark" (or "confirm" if embark isn't
  offered) to actually launch the run. Do not keep re-selecting a
  character you've already chosen -- check the state to see which
  character is currently selected, then move on to embark/confirm.
- **Bundle select**: call bundle_select with a bundle's index to preview
  it. This does NOT choose it yet. On your NEXT turn, call
  bundle_confirm_selection to actually add that bundle's cards to your
  deck. If you'd rather look at a different bundle after previewing one,
  call bundle_cancel_selection first, then bundle_select a different
  index. Don't call bundle_select on the same or a different index
  repeatedly without ever confirming -- that never actually adds cards
  to your deck.

## Tool arguments
Tool arguments must use the exact IDs, indices, or values shown in the
current state text -- never invent a value like "0" for an option that
isn't actually a plain index. Read each tool's own description carefully:
some args are 0-based indices into a specific list (e.g. card_index into
the hand shown in state), others are literal ID strings (e.g. option
must be a character ID like "IRONCLAD" or a control like "embark", not
a number). When unsure which a tool expects, match it against the
Options/values actually listed in the current state. If your hand has no
playable cards left (check the Hand list in the state), or you have no
cards you can afford with remaining energy, you MUST call combat_end_turn
-- do not attempt combat_play_card with a card_index that doesn't exist
in the current hand.

## Looking things up
In card_reward, rewards, shop, bundle_select and event states you also have a
search_knowledge tool with community stats (tier, win rate, pick rate) for
cards, relics and potions. Before choosing, look up every option on offer in
ONE call, using the exact names from the state. A lookup does not take a game
action: after the results come back you must still call a game tool. Treat the
stats as one input alongside your current deck, HP and situation. Skipping is
often the right pick."""

# ---------------------------------------------------------------------------
# Provider abstraction. Two backends supported out of the box:
#   - "anthropic": Claude via the Anthropic API.
#   - "openai": any OpenAI-compatible /v1/chat/completions endpoint --
#     this covers vLLM, SGLang, Ollama, and most self-hosted serving stacks,
#     so it's the one to use for Qwen or any other open model you're serving
#     yourself (e.g. via vLLM on your HPC GPU nodes).
#
# IMPORTANT for vLLM + Qwen: tool calling must be explicitly enabled when you
# launch the server, or tool_calls will come back empty even if the model
# tried to call something in its raw text:
#   vllm serve Qwen/Qwen3-8B --enable-auto-tool-choice --tool-call-parser hermes
# ---------------------------------------------------------------------------


def _tool_schema(t) -> dict:
    """JSON schema for an MCP tool. The mcp SDK has exposed this as
    `inputSchema` (camelCase) in some versions and `input_schema` in others,
    so accept either."""
    schema = getattr(t, "inputSchema", None) or getattr(t, "input_schema", None)
    return schema or {"type": "object", "properties": {}}


def _is_network_error(e: Exception) -> bool:
    """True for connection/timeout/HTTP-client errors worth retrying; programming
    errors (AttributeError, KeyError, ...) must NOT be swallowed."""
    if isinstance(e, (ConnectionError, TimeoutError, OSError)):
        return True
    return type(e).__module__.split(".")[0] in ("openai", "httpx", "httpx2", "httpcore", "httpcore2")


def to_anthropic_tools(mcp_tools: list) -> list[dict]:
    return [
        {
            "name": t.name,
            "description": t.description or "",
            "input_schema": _tool_schema(t),
        }
        for t in mcp_tools
    ]


def to_openai_tools(mcp_tools: list) -> list[dict]:
    return [
        {
            "type": "function",
            "function": {
                "name": t.name,
                "description": t.description or "",
                "parameters": _tool_schema(t),
            },
        }
        for t in mcp_tools
    ]


class AnthropicBackend:
    def __init__(self, model: str):
        import anthropic
        self.client = anthropic.Anthropic()  # reads ANTHROPIC_API_KEY from env
        self.model = model

    def pick_tool(self, system: str, user_content: str, mcp_tools: list, allow_search=False) -> tuple[str, dict] | None:
        response = self.client.messages.create(
            model=self.model,
            max_tokens=1024,
            system=system,
            tools=to_anthropic_tools(mcp_tools),
            tool_choice={"type": "any"},  # force a tool call, no plain-text turns
            messages=[{"role": "user", "content": user_content}],
        )
        tool_use = next((b for b in response.content if b.type == "tool_use"), None)
        if tool_use is None:
            return None
        return tool_use.name, tool_use.input


class OpenAICompatibleBackend:
    """Works against any OpenAI-compatible endpoint: vLLM, SGLang, Ollama, etc.
    Use this for Qwen or any other self-hosted model."""

    def __init__(self, model: str, base_url: str, api_key: str = "not-needed"):
        import openai
        self.client = openai.OpenAI(base_url=base_url, api_key=api_key,
                                    max_retries=3, timeout=120)
        self.model = model

    def pick_tool(self, system, user_content, mcp_tools, allow_search=False):
        game_tools = to_openai_tools(mcp_tools)
        messages = [
            {"role": "system", "content": system},
            {"role": "user", "content": user_content},
        ]
        for round_ in range(MAX_SEARCH_ROUNDS + 1):
            # On the last round the search tool is withheld, so the model must act.
            offer_search = allow_search and round_ < MAX_SEARCH_ROUNDS
            response = self.client.chat.completions.create(
                model=self.model,
                tools=game_tools + ([SEARCH_TOOL] if offer_search else []),
                tool_choice="required",
                messages=messages,
            )
            message = response.choices[0].message
            if not message.tool_calls:
                return None

            searches = [c for c in message.tool_calls if c.function.name == "search_knowledge"]
            if not searches:
                call = message.tool_calls[0]
                # arguments comes back as a JSON string, not a dict -- parse it
                return call.function.name, json.loads(call.function.arguments)

            # Model asked for a lookup: answer every tool call in the message, then re-ask.
            messages.append(message.model_dump(exclude_none=True))
            for call in message.tool_calls:
                if call.function.name == "search_knowledge":
                    try:
                        args = json.loads(call.function.arguments)
                    except json.JSONDecodeError:
                        args = {}
                    items = args.get("items") or args.get("queries") or []
                    topics = args.get("topics") or []
                    print(f"[search] items={items} topics={topics}", file=sys.stderr)
                    content = search_knowledge(items, topics)
                else:
                    content = "Not executed. Review the lookup results, then call one game tool."
                messages.append({"role": "tool", "tool_call_id": call.id, "content": content})
        return None


def build_backend(provider: str, model: str, base_url: str | None):
    if provider == "anthropic":
        return AnthropicBackend(model)
    elif provider == "openai":
        if not base_url:
            raise ValueError("--base-url is required for --provider openai "
                              "(e.g. http://localhost:8000/v1 for a local vLLM server)")
        return OpenAICompatibleBackend(model, base_url)
    else:
        raise ValueError(f"unknown provider: {provider}")


def _post_menu_select_sync(port: int, option: str, multiplayer: bool) -> str:
    import urllib.request
    path = "multiplayer" if multiplayer else "singleplayer"
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}/api/v1/{path}",
        data=json.dumps({"action": "menu_select", "option": option}).encode(),
        headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=10) as r:
            return r.read().decode()
    except Exception as e:  # noqa: BLE001  (HTTPError bodies carry the mod's message)
        body = getattr(e, "read", lambda: b"")().decode(errors="replace")
        return json.dumps({"status": "error", "error": f"{type(e).__name__}: {body or e}"})


async def post_menu_select(port: int, option: str, multiplayer: bool) -> str:
    """Call the mod's HTTP API directly. In a co-op run the MCP server's
    menu_select tool only reaches the singleplayer endpoint, which refuses
    (HTTP 409) while a multiplayer run is active."""
    return await asyncio.to_thread(_post_menu_select_sync, port, option, multiplayer)


# ---------------------------------------------------------------------------
# Multiplayer naming. In a co-op run the server exposes mp_* twins of most
# in-run tools (mp_combat_play_card, mp_rewards_claim, ...). The exceptions
# are listed here; anything with no twin (menu_select) keeps its plain name.
# Menus are served by the singleplayer endpoint even in a co-op lobby, so
# menu/game_over states always use plain names.
# ---------------------------------------------------------------------------
MP_TOOL_OVERRIDES = {"map_choose_node": "mp_map_vote"}
MP_PLAIN_STATES = {"menu", "game_over", "overlay", "unknown"}


def resolve_tool_names(state_type: str, server_names: set[str], in_mp_run: bool) -> list[str]:
    """STATE_TOOL_MAP names for this state, translated to what the server
    actually exposes. Names that don't resolve are dropped (and reported once
    by the startup check)."""
    out = []
    for n in STATE_TOOL_MAP.get(state_type, []):
        if in_mp_run and state_type not in MP_PLAIN_STATES:
            cand = MP_TOOL_OVERRIDES.get(n, "mp_" + n)
            if cand in server_names:
                out.append(cand)
                continue
        if n in server_names:
            out.append(n)
    return out


async def combat_has_affordable_card(session: ClientSession, mp: bool) -> bool | None:
    """Best-effort check: does the current hand have at least one card the
    player can both play (marked with a checkmark in the game's own
    markdown rendering) and afford with current Energy?

    Uses the markdown state format rather than JSON, since the exact JSON
    field names for hand/energy haven't been confirmed against the real
    schema (only state_type has, via matching known state_type values
    seen in play) -- markdown's layout IS confirmed, from actual pasted
    game output: an "Energy: X/Y" line, and hand lines like
    "- [0] **Strike** (1 energy) [Attack] check_mark - Deal 6 damage.".

    Returns True/False when it can confidently parse both pieces, or None
    if the expected patterns aren't found (e.g. screen layout differs from
    what's been seen so far) -- callers should NOT enforce anything on
    None, and just fall back to trusting the model as before, rather than
    accidentally blocking a valid end_turn based on a failed parse.
    """
    text = ""
    for state_tool_name in (("mp_get_game_state", "get_game_state") if mp else ("get_game_state",)):
        result = await session.call_tool(state_tool_name, {"format": "markdown"})
        text = result.content[0].text
        if re.search(r"Energy:\s*\d+", text):
            break

    energy_match = re.search(r"Energy:\s*(\d+)\s*/\s*\d+", text)
    if not energy_match:
        return None
    energy = int(energy_match.group(1))

    # Hand lines look like: "- [0] **Strike** (1 energy) [Attack] \u2713 - Deal 6 damage."
    # The checkmark (\u2713) appears only on cards the game itself marks
    # playable; capture (cost, checkmark-or-not) pairs for each hand line.
    hand_line_pattern = re.compile(r"^-\s*\[\d+\].*?\((\d+)\s*energy\).*$", re.MULTILINE)
    for line_match in hand_line_pattern.finditer(text):
        line = line_match.group(0)
        cost = int(line_match.group(1))
        is_playable = "\u2713" in line
        if is_playable and cost <= energy:
            return True
    return False


async def get_state_and_tools(
    session: ClientSession, all_tools: list, mp: bool
) -> tuple[dict, list]:
    """Fetch the state and the tools valid for it.

    In --multiplayer mode, ask the multiplayer endpoint first; if it doesn't
    return a usable state (we're in a menu or lobby, where the server answers
    on the singleplayer endpoint), fall back to the plain endpoint.
    """
    candidates = ("mp_get_game_state", "get_game_state") if mp else ("get_game_state",)
    state, in_mp_run = None, False
    for state_tool_name in candidates:
        result = await session.call_tool(state_tool_name, {"format": "json"})
        state_text = result.content[0].text
        if not state_text or not state_text.strip():
            print(f"[debug] {state_tool_name} returned empty content. "
                  f"Full result object: {result!r}", file=sys.stderr)
        if "Cannot connect" in state_text:
            print(f"[fatal] server.py could not reach the STS2_MCP mod: {state_text}\n"
                  f"        Check that the game is running with the mod loaded and that "
                  f"--port is the MOD's port (not your SSH tunnel's port).", file=sys.stderr)
            raise GameUnreachable(state_text)
        try:
            parsed = json.loads(state_text)
        except json.JSONDecodeError:
            parsed = None
        usable = isinstance(parsed, dict) and "state_type" in parsed
        if usable:
            state, in_mp_run = parsed, state_tool_name.startswith("mp_")
            break
        if state_tool_name == candidates[-1]:
            print(f"[debug] {state_tool_name} returned non-JSON text:\n{state_text!r}",
                  file=sys.stderr)
            raise json.JSONDecodeError("no usable state", state_text, 0)
        # else: mp endpoint refused (not in a co-op run yet) -> try the plain one

    # In co-op runs the mod reports the game-over screen as a generic overlay.
    if (state.get("state_type") == "overlay"
            and (state.get("overlay") or {}).get("screen_type") == "NGameOverScreen"):
        state["state_type"] = "game_over"

    state_type = state.get("state_type", "unknown")
    valid_names = resolve_tool_names(state_type, {t.name for t in all_tools}, in_mp_run)
    filtered = [t for t in all_tools if t.name in valid_names]

    return state, filtered


async def run_agent_loop(port: int, mp: bool, max_turns: int, backend, character: str,
                         mp_role: str | None = None, max_runs: int = 1,
                         pick_character=None) -> None:
    """max_runs: finished runs (game_over screens) to play before stopping;
    0 = keep going forever. pick_character: optional callable returning the
    character for each new run (default: keep `character`)."""
    server_params = StdioServerParameters(
        command="uv",
        args=["run", "--directory", str(MCP_SERVER_DIR), "python", "server.py", "--port", str(port)],
    )

    # Safety net: if the model repeats a failing action too many times in a
    # row (e.g. trying to play a card with an empty hand), force a sane
    # fallback rather than looping forever. Currently only handles the
    # combat case (force end_turn); other stuck states just get a longer
    # sleep and a warning so you notice, since there's no universally safe
    # fallback action outside combat.
    MAX_CONSECUTIVE_FAILURES = 3
    consecutive_failures = 0

    # Rolling memory of recent actions and their results. Each pick_tool
    # call is otherwise completely stateless -- the model only sees the
    # current game state, with no memory of what it did last turn. That
    # makes multi-step flows (character select -> embark, bundle_select ->
    # bundle_confirm_selection) unreliable, since the model can't tell
    # which step it's already completed. Feeding back a short history
    # fixes that without needing full conversation history.
    RECENT_ACTIONS_TO_KEEP = 5
    recent_actions: list[tuple[str, bool]] = []

    # Fallback for state_types with NO entry in STATE_TOOL_MAP at all (as
    # opposed to the consecutive-failure fallback above, which is for a
    # mapped state where the model keeps picking a bad action). If the
    # same unmapped state_type persists for several turns in a row, the
    # game is genuinely waiting on an action we haven't wired up yet.
    #
    # This is a best-effort guess, not a confirmed fix: combat_select_card's
    # own tool description named "deck_select_card" as its out-of-combat
    # sibling (for exactly this kind of relic-triggered card-selection
    # overlay), but that name has NOT been confirmed in Inspector's tool
    # list the way everything else in STATE_TOOL_MAP has been. It searches
    # the full tool list for a name containing "select_card" that isn't
    # one of the already-mapped combat/rewards ones, tries it with
    # card_index=0 on the first stuck turn, then looks for a matching
    # "confirm"-named tool on the next stuck turn. If nothing matching
    # exists, it says so explicitly rather than guessing further.
    #
    # TODO: replace this whole block once you've confirmed the real tool
    # name(s) in Inspector -- add a proper STATE_TOOL_MAP entry for
    # whatever state_type this screen actually reports, same as every
    # other entry, and this fallback becomes unnecessary for this case
    # (though it stays useful as a general safety net for the next
    # not-yet-mapped screen you hit).
    UNMAPPED_STUCK_THRESHOLD = 3
    unmapped_state_streak_type: str | None = None
    unmapped_state_streak_count = 0
    unmapped_fallback_stage = 0  # 0=nothing tried, 1=select tried, 2=confirm tried
    unmapped_guessed_select_tool: str | None = None

    async with stdio_client(server_params) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()
            tools_result = await session.list_tools()
            all_tools = tools_result.tools
            known_mapped_names = {name for names in STATE_TOOL_MAP.values() for name in names}

            # Startup check: flag any name in STATE_TOOL_MAP that server.py
            # doesn't actually provide, and list real tools nobody maps.
            available = {t.name for t in all_tools}
            for st, names in STATE_TOOL_MAP.items():
                missing = [n for n in names if n not in available]
                if missing:
                    print(f"[map] {st}: tool(s) not found: {missing}", file=sys.stderr)
                if mp and st not in MP_PLAIN_STATES:
                    no_twin = [n for n in names
                               if MP_TOOL_OVERRIDES.get(n, "mp_" + n) not in available]
                    if no_twin:
                        print(f"[map] {st}: no mp_ twin for {no_twin}; plain name is used "
                              f"in co-op runs", file=sys.stderr)
            unmapped_tools = sorted(n for n in available - known_mapped_names
                                    if not n.startswith("mp_"))
            print(f"[map] server tools not in any state: {unmapped_tools}", file=sys.stderr)

            ascension_checked = False
            seen_menu_blobs: set[str] = set()
            runs_done = 0
            game_over_counted = False
            menu_return_failures = 0

            for turn in range(max_turns):
                state, valid_tools = await get_state_and_tools(session, all_tools, mp)
                state_type = state.get("state_type", "unknown")

                # Ascension guard: every in-run state carries run.ascension. Check it
                # once per run and stop rather than burn a run at the wrong level.
                if "run" in state and not ascension_checked:
                    ascension_checked = True
                    asc = (state.get("run") or {}).get("ascension")
                    print(f"[client:{port}] run started at ascension {asc}")
                    if asc is not None and asc != EXPECTED_ASCENSION:
                        print(f"[client:{port}] STOPPING: ascension is {asc}, expected "
                              f"{EXPECTED_ASCENSION}. Set it in the game's character-select "
                              f"screen, abandon this run, and restart the harness.",
                              file=sys.stderr)
                        break
                elif "run" not in state:
                    ascension_checked = False  # back at the menu: check the next run too
                    # Discovery aid: show any menu state that mentions ascension, so you
                    # can see whether the mod offers an option to change it.
                    blob = json.dumps(state, indent=2)
                    if "ascension" in blob.lower() and blob not in seen_menu_blobs and len(seen_menu_blobs) < 5:
                        seen_menu_blobs.add(blob)
                        print(f"[menu] this menu state mentions ascension:\n{blob}", file=sys.stderr)

                if state_type in TERMINAL_STATES:
                    print(f"[client:{port}] terminal state '{state_type}', stopping.")
                    break

                if state_type == "game_over":
                    if not game_over_counted:
                        game_over_counted = True
                        runs_done += 1
                        print(f"[client:{port}] run {runs_done} finished "
                              f"(game_over). Summary: {json.dumps(state)[:300]}")
                    if max_runs and runs_done >= max_runs:
                        print(f"[client:{port}] reached --runs {max_runs}, stopping.")
                        break
                    # Deterministic return to the menu: game_over only offers
                    # main_menu, so there is nothing for the model to decide.
                    in_coop = state.get("game_mode") == "multiplayer" or (
                        mp and "game_mode" not in state)
                    res_text = await post_menu_select(port, "main_menu", in_coop)
                    print(f"[client:{port}] game_over -> main_menu: {res_text[:150]}")
                    if '"status": "error"' in res_text or '"error"' in res_text:
                        menu_return_failures += 1
                        if menu_return_failures >= 5:
                            print(f"[client:{port}] could not leave game_over after 5 tries; "
                                  f"stopping.", file=sys.stderr)
                            break
                    else:
                        menu_return_failures = 0
                        # fresh slate for the next run
                        recent_actions.clear()
                        consecutive_failures = 0
                        ascension_checked = False
                        if pick_character is not None:
                            character = pick_character()
                            print(f"[client:{port}] next run character: {character}")
                    await asyncio.sleep(3)
                    continue
                game_over_counted = False

                if not valid_tools:
                    # These two have no actions at all, so never guess for them.
                    if state_type == "overlay":
                        print(f"[client:{port}] WARNING: unhandled overlay on screen -- "
                              f"it needs manual interaction in the game window.", file=sys.stderr)
                        await asyncio.sleep(3)
                        continue
                    if state_type == "unknown":
                        await asyncio.sleep(1)
                        continue

                    if state_type == unmapped_state_streak_type:
                        unmapped_state_streak_count += 1
                    else:
                        unmapped_state_streak_type = state_type
                        unmapped_state_streak_count = 1
                        unmapped_fallback_stage = 0

                    # Only wait through the full threshold before the FIRST
                    # guess (stage 0). Once committed to the guess-based
                    # flow (stage 1, waiting to confirm), proceed on the
                    # very next turn instead of waiting through the
                    # threshold again -- this was a bug that left the
                    # confirm step stalled for several extra turns.
                    if unmapped_fallback_stage == 0 and unmapped_state_streak_count < UNMAPPED_STUCK_THRESHOLD:
                        print(f"[client:{port}] no mapped tools for state_type "
                              f"'{state_type}' -- skipping turn. (TODO: extend STATE_TOOL_MAP)")
                        await asyncio.sleep(1)
                        continue

                    # Stuck long enough -- attempt the best-effort guess.
                    prefix = "mp_" if mp else ""
                    if unmapped_fallback_stage == 0:
                        candidates = [t for t in all_tools
                                      if t.name not in known_mapped_names
                                      and "select_card" in t.name
                                      and t.name.startswith(prefix if mp else "")]
                        if not candidates:
                            print(f"[client:{port}] stuck {unmapped_state_streak_count} turns on "
                                  f"unmapped state_type '{state_type}', and no unmapped "
                                  f"*select_card* tool exists to guess with. Check Inspector "
                                  f"for this state's actual tool once accessible, and add it "
                                  f"to STATE_TOOL_MAP. Skipping turn.")
                            await asyncio.sleep(1)
                            continue
                        guess = candidates[0]
                        print(f"[client:{port}] GUESSING (unconfirmed): calling {guess.name}"
                              f"(card_index=0) after {unmapped_state_streak_count} stuck turns "
                              f"on state_type '{state_type}'. Verify this is correct once "
                              f"Inspector is accessible.")
                        tool_name, tool_input = guess.name, {"card_index": 0}
                        unmapped_guessed_select_tool = guess.name
                        unmapped_fallback_stage = 1
                    else:
                        # Prefer a targeted guess derived from the actual
                        # select-tool name used, following the confirmed
                        # naming pattern (combat_select_card ->
                        # combat_confirm_selection). Only fall back to a
                        # blind "any tool with confirm in its name" search
                        # if that derived name doesn't exist -- the blind
                        # search previously grabbed combat_confirm_selection
                        # for a non-combat screen, which is scoped to
                        # actual combat and correctly rejected it.
                        derived_name = None
                        if unmapped_guessed_select_tool and unmapped_guessed_select_tool.endswith("_select_card"):
                            base = unmapped_guessed_select_tool[: -len("_select_card")]
                            derived_name = f"{base}_confirm_selection"

                        guess_tool = next((t for t in all_tools if t.name == derived_name), None) if derived_name else None
                        if guess_tool is None:
                            print(f"[client:{port}] no tool named '{derived_name}' exists -- "
                                  f"falling back to a blind search for any confirm-like tool "
                                  f"(less reliable; verify in Inspector when accessible).")
                            candidates = [t for t in all_tools if "confirm" in t.name.lower()
                                          and t.name != "combat_confirm_selection"
                                          and t.name != "mp_combat_confirm_selection"
                                          and t.name.startswith(prefix if mp else "")]
                            if not candidates:
                                print(f"[client:{port}] no confirm-like tool found to follow up "
                                      f"the earlier guess. Skipping turn.")
                                await asyncio.sleep(1)
                                continue
                            guess_tool = candidates[0]

                        print(f"[client:{port}] GUESSING (unconfirmed): calling {guess_tool.name}() "
                              f"to confirm the earlier card selection.")
                        tool_name, tool_input = guess_tool.name, {}
                        unmapped_fallback_stage = 2
                        unmapped_state_streak_count = 0  # give it a fresh look after this

                    print(f"[client:{port}] turn {turn}: {tool_name}({tool_input})")
                    result = await session.call_tool(tool_name, tool_input)
                    result_text = result.content[0].text
                    print(f"[client:{port}]   -> {result_text[:200]}")
                    await asyncio.sleep(0.5)
                    continue

                # Stuck-loop override: too many consecutive failures, and
                # we're in a state where combat_end_turn is a safe forced
                # fallback -- take it directly instead of asking the model
                # again, since it's clearly not self-correcting.
                end_turn_name = next((t.name for t in valid_tools
                                      if t.name in ("mp_combat_end_turn", "combat_end_turn")),
                                     "combat_end_turn")
                if (consecutive_failures >= MAX_CONSECUTIVE_FAILURES
                        and end_turn_name in [t.name for t in valid_tools]):
                    print(f"[client:{port}] {consecutive_failures} consecutive failures -- "
                          f"forcing {end_turn_name} instead of asking the model again.")
                    tool_name, tool_input = end_turn_name, {}
                else:
                    # Affordable-card awareness: NOT a hard block anymore.
                    # Removing combat_end_turn from what's offered was
                    # correctly flagged as dangerous -- there are real
                    # reasons to end turn while holding playable cards
                    # (enemies whose intent punishes playing more cards
                    # a given turn, relics/cards that reward unused
                    # energy or a full hand at end of turn, avoiding a bad
                    # trade). A blanket "affordable card exists -> must
                    # play" rule would override exactly the judgment calls
                    # that make this a strategy game. Instead, surface the
                    # fact as information in the prompt and let the model
                    # weigh it against enemy intent and relics/cards visible
                    # in the state -- same as a human player would.
                    offered_tools = valid_tools
                    affordable_note = ""
                    if state_type in ("monster", "elite", "boss") and any(
                        t.name == end_turn_name for t in valid_tools
                    ):
                        affordable = await combat_has_affordable_card(session, mp)
                        if affordable is True:
                            affordable_note = (
                                "\nNote: you have at least one card in hand you can currently "
                                "afford to play. That does not automatically mean you should "
                                "-- check enemy intents and any relevant relic/card text in the "
                                "state below first. Some enemies punish playing more cards in a "
                                "turn, and some relics/cards reward ending turn with energy or "
                                "cards unused. Weigh this like a human player would, rather than "
                                "playing just because you can afford to.\n"
                            )

                    history_block = ""
                    if recent_actions:
                        last_was_error = recent_actions[-1][1]
                        error_note = ""
                        if last_was_error:
                            error_note = (
                                "\nYour MOST RECENT action returned an error (see last line "
                                "above). Do NOT repeat that exact call -- read the error "
                                "message and either fix the argument (e.g. use a valid index/ID "
                                "from the current state below) or pick a different, valid "
                                "action instead.\n"
                            )
                        history_block = (
                            "Your recent actions this run (most recent last) -- use this "
                            "to tell whether you already completed step 1 of a multi-step "
                            "flow like character select or bundle select, so you don't "
                            "repeat it instead of confirming:\n"
                            + "\n".join(text for text, _ in recent_actions)
                            + error_note + "\n"
                        )
                    # Pick the character in code: LLMs are poor at "choose randomly"
                    # and anchor on whatever example the prompt shows. "run" is
                    # absent only on the real menu (popups can show up mid-run).
                    character_note = ""
                    if state_type == "menu" and "run" not in state:
                        character_note = (
                            f"Your assigned character for this run is {character}. "
                            f"At character select, pick exactly {character} with menu_select "
                            f"(use the ID as listed in the options, not any other character), "
                            f"then embark.\n"
                        )
                    if mp and mp_role and state_type in ("menu", "game_over") and "run" not in state:
                        if mp_role == "host":
                            character_note += (
                                "CO-OP: you are the HOST. From the main menu open the multiplayer "
                                "host option and create the lobby; the other players join you. "
                                "Do not ready up / embark until the lobby roster shows every player.\n")
                        else:
                            character_note += (
                                "CO-OP: you are a JOINING player. From the main menu open the "
                                "multiplayer join option, refresh if no host is listed, and join the "
                                "host's lobby (join_0 if there is one entry). Then pick your "
                                "character and ready up.\n")
                    try:
                        picked = backend.pick_tool(
                            system=SYSTEM_PROMPT,
                            user_content=f"{character_note}{history_block}{affordable_note}"
                                         f"Current state (state_type={state_type}):\n"
                                         f"{json.dumps(state, indent=2)}",
                            mcp_tools=offered_tools,
                            allow_search=state_type in LOOKUP_STATES,
                        )
                    except Exception as e:
                        if not _is_network_error(e):
                            raise  # a real bug: don't hide it behind a retry loop
                        # Dropped tunnel / server hiccup: pause instead of crashing the run.
                        print(f"[client:{port}] model call failed ({type(e).__name__}): {e}. "
                              f"Retrying in 10s.", file=sys.stderr)
                        await asyncio.sleep(10)
                        continue
                    if picked is None:
                        print(f"[client:{port}] model returned no tool call, skipping turn.")
                        continue
                    tool_name, tool_input = picked

                print(f"[client:{port}] turn {turn}: {tool_name}({tool_input})")
                result = await session.call_tool(tool_name, tool_input)
                result_text = result.content[0].text
                print(f"[client:{port}]   -> {result_text[:200]}")

                try:
                    _parsed_for_history = json.loads(result_text)
                    _was_error = isinstance(_parsed_for_history, dict) and _parsed_for_history.get("status") == "error"
                except json.JSONDecodeError:
                    _was_error = False

                # "Not your turn yet" errors mean wait, not a model mistake: don't
                # count them as failures or add them to the history, so the
                # forced-end-turn safety net can't fire during enemy turns.
                if _was_error and any(s in result_text for s in WAIT_ERRORS):
                    await asyncio.sleep(2)
                    continue
                if tool_name.endswith("combat_end_turn") and not _was_error:
                    await asyncio.sleep(3)  # let the enemy turn play out

                # Errors get the full message (usually short and the most
                # useful thing for the model to see); successes get
                # truncated since full state dumps aren't needed twice.
                shown_result = result_text if _was_error else result_text[:150]
                recent_actions.append(
                    (f"- {tool_name}({tool_input}) -> {shown_result}", _was_error)
                )
                recent_actions = recent_actions[-RECENT_ACTIONS_TO_KEEP:]

                # Reuses _was_error computed above (for recent_actions) rather
                # than re-parsing result_text a second time.
                if _was_error:
                    consecutive_failures += 1
                    if consecutive_failures >= MAX_CONSECUTIVE_FAILURES and end_turn_name not in [t.name for t in valid_tools]:
                        print(f"[client:{port}] WARNING: {consecutive_failures} consecutive "
                              f"failures in state '{state_type}' with no safe fallback action "
                              f"available -- this may be genuinely stuck. Sleeping longer.")
                        await asyncio.sleep(5)
                else:
                    consecutive_failures = 0

                # TODO: brief pause between turns if you want to watch it play,
                # or a longer pause / a "wait for state_type to change" check
                # so you're not hammering get_game_state faster than the game
                # actually updates.
                await asyncio.sleep(0.5)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, required=True, help="This client's STS2_MCP port.")
    parser.add_argument("--multiplayer", action="store_true",
                         help="Use mp_* state/tool naming instead of singleplayer.")
    parser.add_argument("--mp-role", choices=["host", "join"], default=None,
                         help="Co-op lobby role (only used with --multiplayer).")
    parser.add_argument("--runs", type=int, default=0,
                         help="Runs to play before stopping (default 0 = keep going forever).")
    parser.add_argument("--max-turns", type=int, default=None,
                         help="Total action budget (default: 500 for a single run, unlimited otherwise).")
    parser.add_argument("--provider", type=str, default="anthropic",
                         choices=["anthropic", "openai"],
                         help="'anthropic' for Claude, 'openai' for any OpenAI-compatible "
                              "endpoint (vLLM, SGLang, Ollama, etc.) -- use this for Qwen.")
    parser.add_argument("--model", type=str, default="claude-sonnet-4-6",
                         help="Model name/ID as your provider expects it, e.g. "
                              "'Qwen/Qwen3-8B' for a vLLM-served model.")
    parser.add_argument("--base-url", type=str, default=None,
                         help="Required for --provider openai, e.g. http://localhost:8000/v1 "
                              "or your HPC-tunneled vLLM endpoint.")
    parser.add_argument("--character", type=str, default="random",
                         help="Character ID to play (e.g. SILENT), or 'random' (default).")
    args = parser.parse_args()

    character = (random.choice(CHARACTERS) if args.character.lower() == "random"
                 else args.character.upper())
    print(f"Assigned character: {character}")

    preload()  # load the embedder/index now so a missing file fails fast and the first lookup isn't slow
    backend = build_backend(args.provider, args.model, args.base_url)

    unreachable = False
    try:
        max_turns = args.max_turns if args.max_turns is not None else (500 if args.runs == 1 else 10**9)
        fixed = args.character.lower() != "random"
        asyncio.run(run_agent_loop(args.port, args.multiplayer, max_turns, backend, character,
                                  args.mp_role, args.runs,
                                  None if fixed else (lambda: random.choice(CHARACTERS))))
    except* GameUnreachable:
        unreachable = True  # message was already printed where it was raised
    if unreachable:
        sys.exit(1)


if __name__ == "__main__":
    main()