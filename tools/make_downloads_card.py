"""Draws the downloads card for the README from the live Thunderstore count.

Usage:
    python tools/make_downloads_card.py --out out/downloads.svg
    python tools/make_downloads_card.py --out card.svg --downloads 5458 --version 0.7.2   (no network)

Standard library only, so a scheduled job can run it with no install step.
If Thunderstore cannot be reached the script exits with an error and writes nothing,
so the last good card stays in place.
"""
import argparse
import json
import sys
import urllib.request
from datetime import datetime, timezone

NAMESPACE = "MentalizePez"
PACKAGE = "REPOPathfinder"
FIRST_RELEASED = "Apr 2026"  # the package's creation date on Thunderstore; it never changes
METRICS_URL = "https://thunderstore.io/api/v1/package-metrics/{ns}/{pkg}/"

FONT = "-apple-system, BlinkMacSystemFont, 'Segoe UI', 'Noto Sans', Helvetica, Arial, sans-serif"
ACCENT = "#00e676"
INK = "#f4fbf7"
MUTED = "#9fb5a9"
DARK = "#062014"
MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]


def fetch_metrics():
    url = METRICS_URL.format(ns=NAMESPACE, pkg=PACKAGE)
    req = urllib.request.Request(url, headers={"User-Agent": "downloads-card/1.0", "Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as resp:
        data = json.load(resp)
    return int(data["downloads"]), str(data["latest_version"])


def esc(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def card(downloads, version, as_of):
    count = f"{downloads:,}"
    label = f"Thunderstore downloads · as of {as_of}"
    summary = f"{count} downloads on Thunderstore as of {as_of}. Latest version {version}. First released {FIRST_RELEASED}."
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 960 112" width="960" height="112" role="img" aria-label="{esc(summary)}">
<title>{esc(summary)}</title>
<defs>
<linearGradient id="bg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#070a08"/><stop offset="0.55" stop-color="#0b1711"/><stop offset="1" stop-color="#0f2b1d"/></linearGradient>
<radialGradient id="glow" cx="56" cy="56" r="220" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="{ACCENT}" stop-opacity="0.16"/><stop offset="1" stop-color="{ACCENT}" stop-opacity="0"/></radialGradient>
<clipPath id="clip"><rect width="960" height="112" rx="18"/></clipPath>
</defs>
<g clip-path="url(#clip)"><rect width="960" height="112" fill="url(#bg)"/><rect width="960" height="112" fill="url(#glow)"/></g>
<rect x="0.5" y="0.5" width="959" height="111" rx="17.5" fill="none" stroke="#ffffff" stroke-opacity="0.1"/>
<rect x="28" y="28" width="56" height="56" rx="14" fill="{ACCENT}" fill-opacity="0.12" stroke="{ACCENT}" stroke-opacity="0.42"/>
<path d="M56 42 V62 M47.5 54 L56 62.5 L64.5 54 M44 70 H68" fill="none" stroke="{ACCENT}" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"/>
<g font-family="{FONT}">
<text x="102" y="63" font-size="50" font-weight="700" fill="{INK}" letter-spacing="-0.5">{count}</text>
<text x="103" y="88" font-size="14.5" fill="{MUTED}">{esc(label)}</text>
<path d="M418 30 V82 M542 30 V82" stroke="#ffffff" stroke-opacity="0.12"/>
<text x="438" y="49" font-size="13.5" fill="{MUTED}">Latest version</text>
<text x="438" y="80" font-size="24" font-weight="700" fill="{INK}">{esc(version)}</text>
<text x="562" y="49" font-size="13.5" fill="{MUTED}">First released</text>
<text x="562" y="80" font-size="24" font-weight="700" fill="{INK}">{FIRST_RELEASED}</text>
<rect x="716" y="34" width="216" height="44" rx="22" fill="{ACCENT}"/>
<text x="814" y="61.5" font-size="15" font-weight="700" fill="{DARK}" text-anchor="middle">Get it on Thunderstore</text>
<path d="M902 56 H914 M909 51 L914 56 L909 61" fill="none" stroke="{DARK}" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/>
</g>
</svg>
"""


def main():
    ap = argparse.ArgumentParser(description="Draws the downloads card for the README from the live Thunderstore count.")
    ap.add_argument("--out", required=True, help="where to write the SVG")
    ap.add_argument("--downloads", type=int, help="use this count instead of asking Thunderstore")
    ap.add_argument("--version", help="use this version instead of asking Thunderstore")
    ap.add_argument("--date", help="date to print on the card, as YYYY-MM-DD (default: today, UTC)")
    args = ap.parse_args()

    if args.downloads is not None and args.version:
        downloads, version = args.downloads, args.version
    else:
        try:
            downloads, version = fetch_metrics()
        except Exception as err:  # network error, bad JSON, missing key: keep the old card
            print(f"Could not read the count from Thunderstore: {err}", file=sys.stderr)
            return 1

    day = datetime.strptime(args.date, "%Y-%m-%d") if args.date else datetime.now(timezone.utc)
    as_of = f"{MONTHS[day.month - 1]} {day.day}, {day.year}"
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write(card(downloads, version, as_of))
    print(f"wrote {args.out}: {downloads:,} downloads, version {version}, as of {as_of}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
