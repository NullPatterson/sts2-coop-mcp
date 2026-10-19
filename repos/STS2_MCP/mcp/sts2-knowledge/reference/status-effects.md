---
id: status-effects-glossary
type: reference
source: https://sts2.wiki/status-effects/
---

# Status Effects & Keyword Glossary

Definitions for the buffs, debuffs, and game terms referenced throughout `strategy/`
and `characters/*/builds/`. Scoped to genuinely reusable status effects and mechanical
terms — not a full card-by-card Power list (the wiki source this was built from tracks
260 total entries, but most of those are one-off effects tied to a single specific card;
per the note in `strategy/general-strategy.md`, call the MCP wiki tool for a specific
card's exact current text rather than looking for it here).

## Combat resources

- **Energy** — spent to play cards from hand; most turn planning starts with how to
  spend it efficiently.
- **Block** — prevents incoming damage until your next turn; the main short-term
  defensive stat.
- **Strength** (stacking, can go negative) — adds flat bonus damage to Attacks per stack.
- **Dexterity** (stacking, can go negative) — increases Block gained from cards per stack.
- **Focus** (stacking, can go negative) — increases the effectiveness of channeled Orbs;
  central to Defect (see `characters/defect/builds/orbs.md`).
- Cards can also grant **Temporary Strength/Dexterity/Focus** — the stat is gained but
  expires at the end of the current turn rather than persisting.

## Common debuffs

- **Vulnerable** — the affected creature takes 50% more damage from Attacks.
- **Weak** — the affected creature deals 25% less damage with Attacks.
- **Frail** — the affected creature gains 25% less Block from cards (Weak's defensive mirror).
- **Poison** — the affected creature loses HP equal to its Poison stacks **at the start
  of its turn**, then the stack is reduced by 1.
- **Confused** — card costs are randomized (0–3 Energy) each time a card is drawn.
- **Debilitate** (a card/relic-granted effect, not a base keyword) — doubles the
  effectiveness of Vulnerable and Weak for a set number of turns; worth knowing since
  several builds reference stacking Vulnerable/Weak multipliers.

## Common buffs

- **Buffer** — prevents the next instance of HP loss entirely (a full negation of one
  loss event, not a percentage reduction).
- **Regen** — heals HP **at the end of your turn**, then the stack is reduced by 1.
- **Intangible** — reduces all damage taken and HP loss to 1 for the current turn only.
- **Vigor** — your next Attack deals bonus damage (consumed after that one Attack).
- **Thorns** — deals damage back to any attacker that hits you.
- **Plating** — grants Block **at the end of your turn**, then the stack is reduced by 1
  at the start of your next turn (so it's a delayed, recurring Block source, not
  immediate Block).
- **Artifact** — negates debuffs applied to you (a counter that's consumed as it blocks them).

## Card keywords

- **Exhaust** — the card is removed from the fight after being played, rather than
  going to the discard pile.
- **Retain** — the card stays in hand at end of turn instead of being discarded.
- **Innate** — guarantees the card is in your opening hand whenever it's in your draw
  pile at the start of combat.
- **Ethereal** — if still in hand at end of turn, the card is automatically Exhausted.

## Orb terms (Defect)

- **Channel** — places an Orb into the first empty Orb Slot. If there's no empty slot,
  the leftmost Orb is automatically Evoked to make room — worth planning around rather
  than triggering by accident.
- **Evoke** — consumes the rightmost Orb, triggering its one-time Evoke effect. Orb
  management is largely about timing when to add new Orbs versus forcing an Evoke.
- **Lightning** — passive: damage to a random enemy each turn. Evoke: a larger one-time
  hit to a random enemy.
- **Frost** — passive: Block each turn. Evoke: a larger one-time Block gain.
- **Dark** — passive: its stored Evoke damage grows every turn it isn't triggered. Evoke:
  deals its full stored damage to the lowest-HP enemy.
- **Plasma** — passive: Energy each turn. Evoke: a larger one-time Energy gain.
- (A **Glass** Orb type was documented in earlier build research — high AoE passive that
  decays, big AoE on Evoke — but wasn't listed in this wiki's current Orb reference
  table. Verify via the wiki tool if you need its exact current numbers; it may have
  been reworked or renamed.)

## Character-unique terms

- **Sly** (Silent) — if a Sly card is discarded from your hand during your turn, it's
  played for free automatically. The mechanical basis of Silent's strongest archetype
  (see `characters/silent/builds/sly.md`).
- **Star** (Regent) — the actual in-game term is singular "Star," not "Star Energy" as
  earlier drafts of this knowledge base called it — a persistent resource that
  accumulates via effects like Genesis ("gain Star at the start of your turn") and gets
  spent on Regent's payoff cards. See `characters/regent/builds/stars.md`.
- **Forge** / **Sovereign Blade** (Regent) — Forge-keyword cards strengthen the
  Sovereign Blade, a retained weapon card. See `characters/regent/builds/forge.md`.
- **Doom** (Necrobinder) — at the end of an enemy's turn, if its Doom is at least equal
  to its current HP, it dies. See `characters/necrobinder/builds/doom.md`.
- **Soul** (Necrobinder) — a card subtype used to cycle the deck quickly (draw-then-Exhaust
  pattern); several Powers key off "whenever you play a Soul." See
  `characters/necrobinder/builds/souls.md`.
- **Summon** (Necrobinder) — strengthens Osty, Necrobinder's companion, who fights and
  tanks independently. See `characters/necrobinder/builds/osty.md`.
- **Afflictions** — a newer STS2 mechanic where enemies can apply negative modifiers
  directly to specific cards in your deck (one documented example: an afflicted card
  costs 1 additional Energy when played). Still being finalized as a system as of this
  writing — treat any specific Affliction's exact effect as something to verify via the
  wiki tool rather than assume.
