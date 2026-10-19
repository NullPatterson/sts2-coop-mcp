#!/usr/bin/env python3
"""
spire_codex_puller.py

Pulls community win-rate/pick-rate/score/Elo data for every STS2 card, relic, and
potion from spire-codex.com and writes it to JSON files in the shape described below,
for RAG or tool-call lookup use. Pair this with
reference/spire-codex-scoring-methodology.md, which explains what these numbers mean
and their biases before you surface them to a person or a model.

INSTALL
    pip install requests beautifulsoup4

USAGE
    # Cards: pulls the single big sortable table at /leaderboards/metrics (one page
    # load covers base + upgraded versions of every card, ~1100+ rows).
    python spire_codex_puller.py --which cards --out ./sts2-knowledge

    # Relics and potions: spire-codex.com's public REST API gives the base entity list;
    # this script then visits each entity's own detail page for its live Community
    # Performance panel (win rate / pick rate / Codex Score), the same panel shown at
    # https://spire-codex.com/cards/echo_form for example. Relics/potions don't get an
    # Elo rating (reward screens only offer cards), so codex_elo is omitted there.
    python spire_codex_puller.py --which relics potions --out ./sts2-knowledge

    # Everything:
    python spire_codex_puller.py --which cards relics potions --out ./sts2-knowledge

RATE LIMITS (see https://spire-codex.com/developers)
    - Unauthenticated REST API calls: 15/minute.
    - General website page loads (used here for the metrics table and detail pages):
      300/minute per IP, but be a good citizen — this script paces detail-page fetches
      to roughly 1/second by default (see --delay). Relics + potions together are a few
      hundred pages, so a full run takes several minutes; that's expected.
    - If you have a Spire Codex API key, pass it with --api-key to send it as the
      X-API-Key header on REST calls, bucketing you by identity instead of IP.

OUTPUT SHAPE
    cards_scores.json:
      { "ECHO_FORM": {"name": "Echo Form", "character": "defect", "upgraded": false,
                       "codex_score": 72, "tier": "B", "win_rate": 0.496,
                       "pick_rate": 0.655, "codex_elo": 1916, "sample_size": 57455},
        "ECHO_FORM_PLUS": {..., "upgraded": true, ...},
        ... }

    relics_scores.json / potions_scores.json: same shape minus codex_elo.

    IDs are upper-snake-case game IDs (matching the manifest.json convention used
    elsewhere in this knowledge base) so they can be joined directly against the
    card/relic/potion gallery files by filename.

IMPORTANT — READ BEFORE RUNNING AT SCALE
    Like sts2_wiki_scraper.py in this same knowledge base, this was written without
    being able to load spire-codex.com's raw HTML from the environment that wrote it
    (no general web egress there — see that script's docstring for the same caveat).
    The selectors below are a best-effort reconstruction from the *rendered* page text
    Claude could see via a fetch tool, not the live DOM. Run with --debug on a small
    sample first and compare against a live page before trusting a full run. All
    parsing lives in parse_card_metrics_row() / parse_detail_page(), so fixes stay local.
"""

import argparse
import json
import re
import time
from pathlib import Path

import requests
from bs4 import BeautifulSoup

BASE = "https://spire-codex.com"
HEADERS = {"User-Agent": "sts2-knowledge-builder/1.0 (personal RAG tool; contact: n/a)"}


def slugify_id(name: str) -> str:
    return re.sub(r"[^a-zA-Z0-9]+", "_", name.strip()).strip("_").upper()


def tier_for_score(score: int) -> str:
    if score >= 90:
        return "S"
    if score >= 78:
        return "A"
    if score >= 65:
        return "B"
    if score >= 50:
        return "C"
    if score >= 35:
        return "D"
    return "F"


def get_soup(url: str, api_key: str | None = None) -> BeautifulSoup:
    headers = dict(HEADERS)
    if api_key:
        headers["X-API-Key"] = api_key
    r = requests.get(url, headers=headers, timeout=30)
    r.raise_for_status()
    return BeautifulSoup(r.text, "html.parser")


def get_json(url: str, params: dict | None = None, api_key: str | None = None):
    headers = dict(HEADERS)
    if api_key:
        headers["X-API-Key"] = api_key
    r = requests.get(url, headers=headers, params=params, timeout=30)
    r.raise_for_status()
    return r.json()


# ---------------------------------------------------------------------------
# CARDS — pulled from the single /leaderboards/metrics table (base + upgraded
# rows for every card that's ever been offered in a reward screen).
# ---------------------------------------------------------------------------

def parse_card_metrics_row(cells: list[str], link_href: str) -> dict | None:
    """
    ADJUST HERE if this looks wrong (run with --debug first).

    `cells` is the row's text cells in order: [#, Card (name, possibly "Name+"),
    Score, Elo, Win%, Pick%, Seen, Runs, A1, A2, A3, W-L]. `link_href` is the card's
    detail-page URL (.../cards/<slug>), used to recover the canonical id + whether
    this row is the base or upgraded version (name ends in "+").
    """
    if len(cells) < 8:
        return None
    _, name_cell, score_s, elo_s, win_s, pick_s, seen_s, runs_s = cells[:8]

    upgraded = name_cell.strip().endswith("+")
    display_name = name_cell.strip().rstrip("+").strip()

    m = re.search(r"/cards/([a-z0-9_]+)", link_href)
    base_id = slugify_id(m.group(1)) if m else slugify_id(display_name)
    entity_id = f"{base_id}_PLUS" if upgraded else base_id

    def pct(s: str) -> float | None:
        s = s.strip()
        if s in ("", "·", "-"):
            return None
        return round(float(s.rstrip("%")) / 100, 4)

    def num(s: str) -> int | None:
        s = s.strip().replace(",", "")
        if s in ("", "·", "-"):
            return None
        return int(s)

    score = num(score_s)
    return {
        "id": entity_id,
        "name": display_name,
        "upgraded": upgraded,
        "codex_score": score,
        "tier": tier_for_score(score) if score is not None else None,
        "codex_elo": num(elo_s),
        "win_rate": pct(win_s),
        "pick_rate": pct(pick_s),
        "sample_size": num(runs_s),
    }


def scrape_card_metrics(out_dir: Path, api_key: str | None, debug: bool = False) -> dict:
    soup = get_soup(f"{BASE}/leaderboards/metrics", api_key=api_key)
    rows = soup.select("table tbody tr") or soup.select("table tr")
    print(f"[cards] {len(rows)} metrics rows found")

    results: dict[str, dict] = {}
    for i, row in enumerate(rows):
        cells = [c.get_text(strip=True) for c in row.select("td")]
        link = row.select_one("a[href*='/cards/']")
        href = link["href"] if link else ""
        if debug and i < 5:
            print("----- DEBUG raw row", i, "-----")
            print(cells, href)
            continue
        parsed = parse_card_metrics_row(cells, href)
        if parsed:
            entity_id = parsed.pop("id")
            results[entity_id] = parsed

    if not debug:
        (out_dir / "cards_scores.json").write_text(
            json.dumps(results, indent=2, sort_keys=True), encoding="utf-8"
        )
        print(f"[cards] wrote {len(results)} entries to cards_scores.json")
    return results


# ---------------------------------------------------------------------------
# RELICS / POTIONS — REST API for the base entity list, then each detail page's
# Community Performance panel for score/win rate/pick rate (no Elo: reward
# screens never offer relics or potions).
# ---------------------------------------------------------------------------

def list_entities(entity_type: str, api_key: str | None) -> list[dict]:
    """entity_type: 'relics' or 'potions'."""
    return get_json(f"{BASE}/api/{entity_type}", api_key=api_key)


def parse_detail_page(soup: BeautifulSoup) -> dict:
    """
    ADJUST HERE if this looks wrong (run with --debug first).

    Pulls the "Community performance" panel shown on every card/relic/potion detail
    page (see https://spire-codex.com/cards/echo_form): a "Win rate" figure, a "Pick
    rate" figure, and a "Codex Score" badge like "A88" (letter tier + number).
    """
    text = soup.get_text(separator="\n", strip=True)

    win_m = re.search(r"Win rate\s*\n?\s*([\d.]+)%", text)
    pick_m = re.search(r"Pick rate\s*\n?\s*([\d.]+)%", text)
    score_m = re.search(r"Codex Score\s*\n?\s*([SABCDF])(\d{1,3})", text)
    picks_m = re.search(r"([\d,]+)\s*picks", text)

    score = int(score_m.group(2)) if score_m else None
    return {
        "win_rate": round(float(win_m.group(1)) / 100, 4) if win_m else None,
        "pick_rate": round(float(pick_m.group(1)) / 100, 4) if pick_m else None,
        "codex_score": score,
        "tier": score_m.group(1) if score_m else (tier_for_score(score) if score is not None else None),
        "sample_size": int(picks_m.group(1).replace(",", "")) if picks_m else None,
    }


def scrape_detail_scores(
    entity_type: str, out_dir: Path, api_key: str | None, delay: float, debug: bool
) -> dict:
    entities = list_entities(entity_type, api_key)
    print(f"[{entity_type}] {len(entities)} entities from the API")

    results: dict[str, dict] = {}
    for i, e in enumerate(entities):
        entity_id = e.get("id") or slugify_id(e.get("name", ""))
        slug = e.get("id", entity_id).lower()
        url = f"{BASE}/{entity_type}/{slug}"

        if i % 10 == 0:
            print(f"[{entity_type}] {i}/{len(entities)}...")

        if debug and i < 3:
            soup = get_soup(url, api_key=api_key)
            print("----- DEBUG raw detail page", i, url, "-----")
            print(soup.get_text(separator="\n", strip=True)[:1500])
            continue

        try:
            soup = get_soup(url, api_key=api_key)
            perf = parse_detail_page(soup)
        except requests.RequestException as exc:
            print(f"[{entity_type}] skip {entity_id}: {exc}")
            continue

        results[slugify_id(entity_id)] = {
            "name": e.get("name", entity_id),
            "character": e.get("character") or e.get("pool") or "colorless",
            "rarity": e.get("rarity"),
            **perf,
        }
        time.sleep(delay)

    if not debug:
        (out_dir / f"{entity_type}_scores.json").write_text(
            json.dumps(results, indent=2, sort_keys=True), encoding="utf-8"
        )
        print(f"[{entity_type}] wrote {len(results)} entries to {entity_type}_scores.json")
    return results


# ---------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default="./sts2-knowledge", help="output directory (default: ./sts2-knowledge)")
    ap.add_argument("--which", nargs="+", default=["cards", "relics", "potions"],
                     choices=["cards", "relics", "potions"])
    ap.add_argument("--api-key", default=None, help="Spire Codex API key (optional, raises your rate limit)")
    ap.add_argument("--delay", type=float, default=1.0, help="seconds between detail-page fetches (relics/potions)")
    ap.add_argument("--debug", action="store_true",
                     help="print raw rows/pages for the first few items instead of writing files")
    args = ap.parse_args()

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    if "cards" in args.which:
        scrape_card_metrics(out_dir, args.api_key, debug=args.debug)
    if "relics" in args.which:
        scrape_detail_scores("relics", out_dir, args.api_key, args.delay, debug=args.debug)
    if "potions" in args.which:
        scrape_detail_scores("potions", out_dir, args.api_key, args.delay, debug=args.debug)


if __name__ == "__main__":
    main()