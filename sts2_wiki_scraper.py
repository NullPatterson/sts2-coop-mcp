#!/usr/bin/env python3
"""
sts2_wiki_scraper.py

Scrapes sts2.wiki's card/relic/potion databases and generates one Markdown
file per item, in the same shape as the hand-written examples in
sts2-knowledge/ (characters/ironclad/cards/BASH.md, relics/BURNING_BLOOD.md,
potions/FIRE_POTION.md). Also (re)writes manifest.json with an id -> path
entry for every item generated, merging with whatever is already there.

INSTALL
    pip install requests beautifulsoup4

USAGE
    # First run: sanity-check selectors before generating hundreds of files
    python sts2_wiki_scraper.py --which cards --debug

    # Generate everything into an existing knowledge base
    python sts2_wiki_scraper.py --out ./sts2-knowledge --which cards relics potions

IMPORTANT — READ BEFORE RUNNING AT SCALE
    This was written without being able to inspect sts2.wiki's actual HTML
    from the environment that wrote it (network egress there is allowlisted
    to package registries only, not general websites). The parsing logic
    below is a best-effort reconstruction based on the *rendered text* of
    the pages, not the real DOM, so selectors are very likely to need a
    small adjustment.

    Before generating the full run:
      1. Run with --debug on a single category (e.g. `--which cards`).
         This prints the raw HTML around the first few items instead of
         writing files.
      2. Compare that HTML to the regexes/selectors in parse_card_block /
         parse_relic_block / parse_potion_block below and adjust — the
         fields you're extracting (name, type, cost, rarity, effect text)
         are clearly labeled in each function.
      3. Re-run without --debug once a handful of files look right; spot
         check a few generated .md files against the live wiki page before
         trusting the full batch.

    All the actual scraping/parsing logic is isolated in the three
    parse_*_block() functions so fixes stay local and don't touch the
    file-writing or manifest logic below them.
"""

import argparse
import json
import re
import time
from pathlib import Path

import requests
from bs4 import BeautifulSoup

BASE = "https://sts2.wiki"
HEADERS = {"User-Agent": "sts2-knowledge-builder/1.0 (personal RAG tool; contact: n/a)"}
SLEEP_BETWEEN_REQUESTS = 1.0  # be polite to the wiki's server

CHARACTER_SLUGS = {"ironclad", "silent", "regent", "necrobinder", "defect"}
NON_CHARACTER_FOLDERS = {"colorless", "curse", "status", "event", "quest", "unknown"}


def slugify_id(name: str) -> str:
    """'Ashen Strike' -> 'ASHEN_STRIKE' (matches the game's own ID convention)."""
    return re.sub(r"[^a-zA-Z0-9]+", "_", name.strip()).strip("_").upper()


def get_soup(url: str) -> BeautifulSoup:
    r = requests.get(url, headers=HEADERS, timeout=30)
    r.raise_for_status()
    time.sleep(SLEEP_BETWEEN_REQUESTS)
    return BeautifulSoup(r.text, "html.parser")


# ---------------------------------------------------------------------------
# CARDS
# ---------------------------------------------------------------------------

def clean_text(s: str) -> str:
    """Collapse the whitespace left behind by the upgraded-text token spans."""
    return re.sub(r"\s+", " ", s).strip()


def discover_card_pages(soup: BeautifulSoup) -> int:
    pages = {1}
    for a in soup.select("a[href*='/cards/page/']"):
        m = re.search(r"/cards/page/(\d+)/", a.get("href", ""))
        if m:
            pages.add(int(m.group(1)))
    return max(pages)


def parse_card_block(article) -> dict | None:
    """
    Confirmed against real markup (2026): each card is
    <article class="card-builder" data-color="..." data-rarity="..." data-type="...">
    with img.card-builder__texture (src carries character/slug), separate
    .card-builder__name--base / --upgraded spans, .card-builder__cost, and
    .card-builder__text--base / --upgraded divs (each holding one <p> per line;
    the upgraded div wraps individual words in .card-builder__text-token spans
    for diff-highlighting, hence the clean_text() whitespace collapse below).
    """
    img = article.select_one("img.card-builder__texture")
    if img is None:
        return None
    src = img.get("src", "")
    m = re.search(r"/card-portraits/([a-z0-9_]+)/([a-z0-9_]+)\.(webp|png)", src)
    if not m:
        return None
    character, slug = m.group(1), m.group(2)

    card_type = article.get("data-type", "unknown")
    rarity = article.get("data-rarity", "unknown")

    name_el = article.select_one(".card-builder__name--base")
    name = clean_text(name_el.get_text()) if name_el else slug.replace("_", " ").title()

    cost_el = article.select_one(".card-builder__cost")
    cost = clean_text(cost_el.get_text()) if cost_el else "?"

    base_el = article.select_one(".card-builder__text--base")
    base_effect = clean_text(" ".join(p.get_text(" ") for p in base_el.select("p"))) if base_el else ""

    upgraded_el = article.select_one(".card-builder__text--upgraded")
    upgraded_effect = clean_text(" ".join(p.get_text(" ") for p in upgraded_el.select("p"))) if upgraded_el else ""

    return {
        "id": slugify_id(name),
        "name": name,
        "character": character,
        "card_type": card_type,
        "cost": cost,
        "rarity": rarity,
        "base_effect": base_effect,
        "upgraded_effect": upgraded_effect,
    }


def scrape_cards(out_dir: Path, debug: bool = False) -> list[dict]:
    first_soup = get_soup(f"{BASE}/cards/")
    total_pages = discover_card_pages(first_soup)
    print(f"[cards] {total_pages} page(s) detected")

    items: list[dict] = []
    for page in range(1, total_pages + 1):
        url = f"{BASE}/cards/" if page == 1 else f"{BASE}/cards/page/{page}/"
        soup = first_soup if page == 1 else get_soup(url)
        articles = soup.select("article.card-builder")
        print(f"[cards] page {page}: {len(articles)} card entries found")

        for i, article in enumerate(articles):
            if debug and page == 1 and i < 3:
                print("\n----- DEBUG: parsed result for item", i, "-----")
                print(parse_card_block(article))
                continue
            parsed = parse_card_block(article)
            if parsed:
                items.append(parsed)

        if debug:
            break  # only need page 1 to tune selectors

    if not debug:
        write_card_files(items, out_dir)
    return items


def render_card_md(it: dict) -> str:
    return f"""---
id: {it['id']}
name: {it['name']}
character: {it['character']}
type: card
card_type: {it['card_type']}
rarity: {it['rarity']}
cost: {it['cost']}
---

# {it['name']}

**Base:** {it['base_effect'] or '(needs manual fill-in — see source page)'}
**Upgraded ({it['name']}+):** {it['upgraded_effect'] or '(needs manual fill-in)'}

**Strategic notes:** _TODO — add synergy / priority notes._

**Synergizes with:** _TODO_
"""


def write_card_files(items: list[dict], out_dir: Path):
    manifest = load_manifest(out_dir)
    written = 0
    for it in items:
        character = it["character"]
        if character in CHARACTER_SLUGS:
            folder = out_dir / "characters" / character / "cards"
        else:
            folder = out_dir / "cards" / "colorless"
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / f"{it['id']}.md"
        path.write_text(render_card_md(it), encoding="utf-8")
        manifest[it["id"]] = {
            "path": str(path.relative_to(out_dir)),
            "type": "card",
            "character": character,
        }
        written += 1
    save_manifest(out_dir, manifest)
    print(f"[cards] wrote {written} files")


# ---------------------------------------------------------------------------
# RELICS  (and POTIONS, same shape — both are single-page listings)
# ---------------------------------------------------------------------------

def parse_relic_block(article) -> dict | None:
    """
    Confirmed against real markup (2026): each relic is
    <article class="relic-card" data-character="..." data-rarity="..." ...>
    with img.relic-card__image, h2.relic-card__title, and
    p.relic-card__description (effect text lives in both its title attribute
    and its text content — either is fine to read).
    """
    img = article.select_one("img.relic-card__image")
    if img is None:
        return None
    src = img.get("src", "")
    m = re.search(r"/media/relics/([a-z0-9_]+)\.(png|webp)", src)
    if not m:
        return None
    slug = m.group(1)

    character = article.get("data-character", "Colorless").lower()
    rarity = article.get("data-rarity", "unknown").lower()

    name_el = article.select_one(".relic-card__title")
    name = clean_text(name_el.get_text()) if name_el else slug.replace("_", " ").title()

    desc_el = article.select_one(".relic-card__description")
    effect = clean_text(desc_el.get("title") or desc_el.get_text()) if desc_el else ""

    return {
        "id": slugify_id(name),
        "name": name,
        "character": character,
        "rarity": rarity,
        "effect": effect,
    }


def scrape_relics(out_dir: Path, debug: bool = False) -> list[dict]:
    soup = get_soup(f"{BASE}/relics/")
    articles = soup.select("article.relic-card")
    print(f"[relics] {len(articles)} relic entries found")

    items = []
    for i, article in enumerate(articles):
        if debug and i < 3:
            print("\n----- DEBUG: parsed result for item", i, "-----")
            print(parse_relic_block(article))
            continue
        parsed = parse_relic_block(article)
        if parsed:
            items.append(parsed)

    if not debug:
        write_relic_files(items, out_dir)
    return items


def render_relic_md(it: dict) -> str:
    return f"""---
id: {it['id']}
name: {it['name']}
character: {it['character']}
type: relic
rarity: {it['rarity']}
---

# {it['name']}

**Effect:** {it['effect'] or '(needs manual fill-in — see source page)'}

**Strategic notes:** _TODO — add strategic notes._

**Notable interactions:** _TODO_
"""


def write_relic_files(items: list[dict], out_dir: Path):
    folder = out_dir / "relics"
    folder.mkdir(parents=True, exist_ok=True)
    manifest = load_manifest(out_dir)
    for it in items:
        path = folder / f"{it['id']}.md"
        path.write_text(render_relic_md(it), encoding="utf-8")
        manifest[it["id"]] = {"path": str(path.relative_to(out_dir)), "type": "relic", "character": it["character"]}
    save_manifest(out_dir, manifest)
    print(f"[relics] wrote {len(items)} files")


def parse_potion_block(article) -> dict | None:
    """
    Confirmed against real markup (2026): each potion is
    <article class="potion-card" data-character="..." data-type="...">
    Note the naming quirk: data-type actually holds the rarity tier
    (Common/Uncommon/Rare), not a mechanical potion type — confirmed from a
    live debug run, not guessed. Otherwise mirrors relic-card: img, h2 title,
    p description (effect text in both its title attribute and text content).
    """
    img = article.select_one("img.potion-card__image")
    if img is None:
        return None
    src = img.get("src", "")
    m = re.search(r"/media/potions/([a-z0-9_]+)\.(png|webp)", src)
    if not m:
        return None
    slug = m.group(1)

    character = article.get("data-character", "Colorless").lower()
    rarity = article.get("data-type", "unknown").lower()  # yes, data-type = rarity for potions

    name_el = article.select_one(".potion-card__title")
    name = clean_text(name_el.get_text()) if name_el else slug.replace("_", " ").title()

    desc_el = article.select_one(".potion-card__description")
    effect = clean_text(desc_el.get("title") or desc_el.get_text()) if desc_el else ""

    return {
        "id": slugify_id(name),
        "name": name,
        "character": character,
        "rarity": rarity,
        "effect": effect,
    }


def scrape_potions(out_dir: Path, debug: bool = False) -> list[dict]:
    soup = get_soup(f"{BASE}/potions/")
    articles = soup.select("article.potion-card")
    print(f"[potions] {len(articles)} potion entries found")

    items = []
    for i, article in enumerate(articles):
        if debug and i < 3:
            print("\n----- DEBUG: parsed result for item", i, "-----")
            print(parse_potion_block(article))
            continue
        parsed = parse_potion_block(article)
        if parsed:
            items.append(parsed)

    if not debug:
        write_potion_files(items, out_dir)
    return items


def render_potion_md(it: dict) -> str:
    return f"""---
id: {it['id']}
name: {it['name']}
character: {it['character']}
type: potion
rarity: {it['rarity']}
---

# {it['name']}

**Effect:** {it['effect'] or '(needs manual fill-in — see source page)'}

**Strategic notes:** _TODO_

**When to use:** _TODO_
"""


def write_potion_files(items: list[dict], out_dir: Path):
    folder = out_dir / "potions"
    folder.mkdir(parents=True, exist_ok=True)
    manifest = load_manifest(out_dir)
    for it in items:
        path = folder / f"{it['id']}.md"
        path.write_text(render_potion_md(it), encoding="utf-8")
        manifest[it["id"]] = {"path": str(path.relative_to(out_dir)), "type": "potion", "character": it["character"]}
    save_manifest(out_dir, manifest)
    print(f"[potions] wrote {len(items)} files")


# ---------------------------------------------------------------------------
# manifest helpers
# ---------------------------------------------------------------------------

def load_manifest(out_dir: Path) -> dict:
    path = out_dir / "manifest.json"
    if path.exists():
        return json.loads(path.read_text())
    return {}


def save_manifest(out_dir: Path, manifest: dict):
    (out_dir / "manifest.json").write_text(json.dumps(manifest, indent=2, sort_keys=True), encoding="utf-8")


# ---------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default="./sts2-knowledge", help="output knowledge-base root (default: ./sts2-knowledge)")
    ap.add_argument("--which", nargs="+", default=["cards", "relics", "potions"],
                     choices=["cards", "relics", "potions"])
    ap.add_argument("--debug", action="store_true",
                     help="print raw HTML for the first few items instead of writing files, for selector tuning")
    args = ap.parse_args()

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    if "cards" in args.which:
        scrape_cards(out_dir, debug=args.debug)
    if "relics" in args.which:
        scrape_relics(out_dir, debug=args.debug)
    if "potions" in args.which:
        scrape_potions(out_dir, debug=args.debug)


if __name__ == "__main__":
    main()