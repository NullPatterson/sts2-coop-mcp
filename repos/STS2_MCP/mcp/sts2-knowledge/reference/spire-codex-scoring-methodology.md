---
id: spire-codex-scoring-methodology
type: reference
source: https://spire-codex.com/leaderboards/scoring
---

# How Spire Codex's Community Score Works

Spire Codex (spire-codex.com) is a fan-run database that aggregates community-submitted
STS2 run data and derives two independent ratings for every card, relic, and potion:
**Codex Score** (0–100, win-rate based) and **Codex Elo** (cards only, preference-based).
This file explains what each number means and how it's computed, so a model consuming
`cards_scores.json` / `relics_scores.json` / `potions_scores.json` (see the puller
script) can weigh the numbers correctly instead of treating them as a flat "goodness"
rating.

## Codex Score: what it measures

Codex Score answers one narrow question: **does this entity's presence correlate with
winning runs, in the community-submitted data?** 50 is neutral (roughly the average run's
win rate at Ascension 0). 100 is the strongest positive signal observed; 0 the weakest.
It is explicitly **not** a verdict on card quality — see the biases section below.

### Tier bands

| Score range | Tier | Label | Meaning |
|---|---|---|---|
| 90–100 | S | Top tier | Out-of-distribution win rate sustained over hundreds of picks. |
| 78–89 | A | Strong | Reliably wins above baseline across a large sample. |
| 65–77 | B | Solid | Above-average win rate; a safe pick when nothing better is offered. |
| 50–64 | C | Average | The middle of the curve — most entities live here. |
| 35–49 | D | Below average | Below baseline; often niche/situational, sometimes just high-exposure. |
| 0–34 | F | Underperforming | Bottom of the signal — frequently a staple dragged down by being in nearly every run, not necessarily bad. |

### The formula

Two stages: Bayesian shrinkage (so a 5-pick perfect record can't outrank a 500-pick
reliable one), then a linear map onto 0–100.

```
baseline = total_wins / total_runs        # the global win rate across all submitted runs
shrunk   = (wins + baseline * 50) / (picks + 50)
delta    = shrunk - baseline
raw      = (delta / 0.15 + 1) * 50
score    = clamp(raw, 0, 100)             # rounded to an integer
```

- **Prior weight = 50.** Every entity starts as if it already had 50 picks at the
  baseline win rate. Real picks accumulate against that prior, so a card needs real
  volume before its score moves far from 50 (neutral) in either direction.
- **Scale = ±15 percentage points.** A win rate 15pp above baseline maps to a score of
  100; scores saturate beyond that gap.
- **Clamp.** The 0–100 cap is a floor/ceiling, not a claim of exact value — a card at
  100 is "at least this good," not "exactly this good."

### Worked examples (50% baseline in all rows)

| Picks | Wins | Win % | Score | Note |
|---|---|---|---|---|
| 1000 | 700 | 70% | 100 (S) | Massive sample, elite |
| 100 | 70 | 70% | 94 (S) | High-N, strong |
| 500 | 280 | 56% | 68 (B) | Mid-N, solid |
| 5 | 5 | 100% | 65 (B) | **Small-N perfect record — note this scores lower than the 500-pick 56% row above.** Sample size matters more than raw win rate. |
| 50 | 25 | 50% | 50 (C) | Average performer |
| 5 | 0 | 0% | 35 (D) | Small sample, no wins yet |
| 200 | 60 | 30% | 0 (F) | High sample, genuinely underperforming |

The key lesson for interpreting the JSON output: **check the pick/sample count before
trusting a score.** A card with `codex_score: 100` and only a handful of recorded picks
is not the same kind of signal as one with `codex_score: 100` and tens of thousands of
picks.

## Codex Elo: the other half (cards only)

Score is confounded by *who* picks a card and what deck it lands in. Codex Elo sidesteps
that by ignoring win/loss entirely and instead modeling **revealed preference**: every
card-reward screen is a head-to-head where the card taken "beats" the cards skipped. A
Bradley-Terry model is fit over millions of these decisions, producing a rating for "when
offered, which card do players actually want?"

- **Skill-agnostic** — a pick decision doesn't depend on whether the player is skilled,
  unlike win rate.
- **Anchored ~1500, ~400 per decade** — same scale logic as chess Elo.
- **Cards only** — reward screens don't offer relics or potions, so Elo doesn't exist for
  those two entity types.
- **Base and Upgraded ("+") cards get separate Elo ratings.** The base card's Elo comes
  from reward-screen picks. The "+" version's Elo comes from a different signal: the
  rest-site Smith (upgrade) decision — when you upgrade a card, it "beats" the other
  upgradeable cards in your deck you didn't choose. Same Bradley-Terry approach, different
  underlying decisions.

**Read Score and Elo together.** High on both = unambiguously strong. A gap between them
usually signals a build-around card (low Score because most decks that see it don't
have the support online, high Elo because players correctly want it when offered) or a
situational pick (opposite pattern).

## What the score is *not* — biases to keep in mind

- **Exposure/pick-frequency bias.** Cards that sit in the deck longer or get picked in
  nearly every run (starters, commons, staples) absorb more of every loss they were
  present for. A staple scoring low (F-tier) often means "high exposure," not "bad card."
- **Survivorship bias.** Late-game rares/uncommons are only offered in runs that already
  went deep — runs that were, on average, already winning. Their win rate is inflated by
  already being in a healthy run before the pick.
- **Not a personal recommendation.** Score reflects "what wins for the average
  submitter," with no knowledge of your specific deck, character, ascension, or existing
  relics. A C-tier card can still be the correct pick for your situation.
- **Not normalized by ascension or character.** A relic strong at A0 and mediocre at A10
  gets one blended number; same for a relic that's great on one character and weak on
  another (per-character breakdowns exist on Spire Codex's site but aren't part of the
  single blended score).
- **Submitter-pool bias.** The data reflects runs real people bothered to submit —
  disproportionately wins, disproportionately from ranked-mode players. The baseline win
  rate is computed from the same pool, so this mostly cancels out for *relative* ranking,
  but absolute win rates skew higher than the average player's.
- **Refreshes every 30 minutes.** Scores are cached; a very recent submission may not be
  reflected yet.

## Practical guidance for RAG use

When a build file or card entry cites a `codex_score`, treat it as one input alongside
the qualitative strategy notes elsewhere in this knowledge base — not a standalone
verdict. Prefer citing `codex_elo` alongside `codex_score` for cards when both are
available, since the gap between them is itself informative (see above). Always keep the
`sample_size`/pick count next to any score your agent surfaces to a person, for the same
reason the worked examples table does.
