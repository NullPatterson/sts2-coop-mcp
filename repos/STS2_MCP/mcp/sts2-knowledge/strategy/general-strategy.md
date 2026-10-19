---
id: general-strategy
type: strategy
scope: all-characters
---

# General Strategy (Slay the Spire 2)

Character-agnostic principles that apply before any specific build direction is chosen.
Use this as the default prior; character overview and build files override it when they
conflict.

## Deckbuilding fundamentals

- **Deck size vs. power, not deck size alone.** A smaller deck draws its good cards more
  often, but a small deck with no answer to a problem (no AoE, no reliable block) loses
  to variance. Thin the deck by skipping card rewards only once the deck already has its
  core answers; don't thin for its own sake.
- **Curve matters more than peak power.** A deck that can only function on a 4+ Energy
  turn will brick constantly. Prioritize at least one cheap (0-1 cost) source of damage
  and one cheap source of block before taking more expensive payoff cards.
- **Every deck needs three answers:** a way to deal with a single hard-hitting enemy, a
  way to deal with multiple weaker enemies (AoE or fast single-target repeated), and a
  way to not die to sustained/HP-loss pressure. If a deck is missing one of these by the
  Act 1 boss, treat filling it as higher priority than power upgrades.
- **Upgrades are not equal.** Upgrading a card that fixes a structural weakness (a block
  card, a scaling Power) is usually worth more than upgrading a card that was already
  fine. Early upgrades should target cards played every combat.

## Combat priorities (per-turn decision order)

1. Identify whether this turn needs to be defensive, offensive, or setup. Reading enemy
   intents (shown before they act) should drive this, not habit.
2. If an enemy's next attack would take HP below ~50%, prioritize block or debuffs
   (Weak) over marginal extra damage, unless the kill is available this turn.
3. Apply debuffs (Vulnerable, Weak) before committing to a full damage rotation, since
   they multiply everything played after them.
4. Don't over-invest resources (Energy, cards, HP-cost effects) into overkill — leftover
   damage on an already-dead enemy is wasted tempo that could have been block or setup
   for the next fight.
5. Track multi-turn setups (Stars, Doom, Orbs, scaling Powers) explicitly — these
   represent banked value and should factor into whether a turn "looks" inefficient but
   is actually on-plan.

## Map and route strategy

- Elites are the main source of relics and are where most non-boss deaths happen. Only
  fight an elite when the deck has both a defensive answer and a realistic damage plan
  for that specific fight; skip or reroute around elites that counter the current build
  (e.g., a Vulnerable-heavy build vs. an elite immune to debuffs).
  Adjust ascension awareness of pain: pathing conservatively out of Act 1 into stronger
  passive scaling is usually better than sprinting to Act 3 with a thin, undertuned deck.
  Rest sites: default to upgrading a key card over healing when HP is safely above ~60%
  and the deck has an obvious upgrade target; default to healing when HP is below that
  or the next known fight (elite, boss) is imminent.
- Prioritize unknown "?" rooms and shops early for relic/card economy; save rest-site
  healing for right before elites and bosses when possible.

## Relic and potion evaluation

- Rank a relic by how much it removes a structural weakness of the current deck (e.g., a
  relic granting draw for a deck that stalls) above relics that just add raw damage.
- Starter relics are usually build-defining — read the character overview file for how
  the class's starter relic should shape early picks.
- Potions are a resource to spend, not hoard. A potion saved past the fight where it
  would have mattered is a wasted potion. Use offensive potions to close out fights
  faster (reduces total damage taken) and defensive/utility potions proactively on
  elites and bosses rather than reactively at 10% HP.

## Ascension and difficulty scaling

- Higher ascension levels increase enemy HP/damage and remove safety nets (e.g., reduced
  starting HP, tougher elites). At higher ascensions, weight defensive answers and early
  stabilization more heavily than the build-path notes below assume by default.

## How to use the rest of this knowledge base

- `characters/<name>/overview.md` — starter kit, core loop, and when to lean into this character.
- `characters/<name>/builds/*.md` — named build paths with their key cards and relics
  described inline (goal, priority order, playstyle notes).
- `reference/spire-codex-scoring-methodology.md` + `cards_scores.json` /
  `relics_scores.json` / `potions_scores.json` — community win-rate-based rankings; read
  the methodology file before citing a score, since it explains real biases in the data.

## Exact card, relic, and potion effects: use the wiki tool, not this file

This knowledge base deliberately does **not** include a card/relic/potion gallery with
exact numbers. The MCP mod already exposes a wiki lookup tool (`/api/v1/wiki`) with
live, exactly-current game data — that tool is the authoritative source for precise
effect text, damage numbers, and costs, not anything written here.

Build files mention "key cards" by name with a one-line description of their role in the
archetype — treat that description as strategic framing, not as a guarantee of the
card's exact current numbers (costs and values can shift between patches). **If exact
numbers matter for a decision and you're not certain they're current, call the wiki tool
rather than relying on a memorized or approximate figure from these notes.** This applies
especially to anything that sounds like a specific number (damage dealt, Block gained,
Energy cost) — those are exactly the values most likely to have shifted since a build
file was written.
