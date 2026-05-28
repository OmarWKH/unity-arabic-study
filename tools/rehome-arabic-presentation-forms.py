"""Rehome a font's base Arabic glyphs into the Presentation Forms-B codepoints.

For each Presentation Forms-B (and curated Presentation Forms-A) codepoint
the font doesn't already cover, look up the codepoint's "base letter" via
Unicode decomposition, and — if the font HAS that base letter — add a cmap
entry mapping the PF codepoint to the same glyph the base letter uses.

The result: any text that uses presentation forms (e.g., what RTLTMPro
emits) renders SOME glyph instead of tofu. The rendered glyph is the
*isolated* shape, not the contextually-correct shape — meaning initial /
medial / final forms all visually look like the isolated. This is
"second-best": legible, not pretty. The intent is to unblock fonts that
have everything else right but lack PF-B coverage.

Outputs a new TTF alongside the source with a configurable suffix
(default: `-pfb-rehomed`). Original is unchanged.

Usage:
  python tools/rehome-arabic-presentation-forms.py FONT.ttf
  python tools/rehome-arabic-presentation-forms.py FONT.ttf --out OUT.ttf
  python tools/rehome-arabic-presentation-forms.py FONT.ttf --dry-run
"""

from __future__ import annotations
import argparse
import sys
import unicodedata
from pathlib import Path
from typing import Optional

try:
    from fontTools.ttLib import TTFont
except ImportError:
    print("fontTools is required:  pip install fonttools", file=sys.stderr)
    sys.exit(2)

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass


# Same ranges as the scorer. We rehome:
#   - Presentation Forms-B  (U+FE70..FEFC)    — the main RTLTMPro output
#   - The six shadda+harakat ligatures        (U+FC5E..FC63)
# We deliberately skip the rest of Presentation Forms-A because those are
# letter-specific ligatures (lam-alef etc.) where rehoming to a single base
# would be misleading.
REHOME_TARGETS = list(range(0xFE70, 0xFEFD)) + list(range(0xFC5E, 0xFC64))


def base_letter_of(cp: int) -> Optional[int]:
    """Mirror of the scorer's base_letter_of — for a PF codepoint, return
    the first Arabic-block codepoint in its decomposition."""
    if 0x0600 <= cp <= 0x06FF:
        return cp
    try:
        decomp = unicodedata.decomposition(chr(cp))
    except (ValueError, OverflowError):
        return None
    if not decomp:
        return None
    parts = decomp.split()
    if parts and parts[0].startswith("<"):
        parts = parts[1:]
    arabic_block = None
    first = None
    for tok in parts:
        try: v = int(tok, 16)
        except ValueError: continue
        if first is None: first = v
        if 0x0600 <= v <= 0x06FF and arabic_block is None:
            arabic_block = v
    return arabic_block if arabic_block is not None else first


def rehome(font: TTFont, dry_run: bool = False) -> dict:
    """Add cmap entries for missing PF codepoints, pointing at the glyph
    used by the codepoint's base Arabic letter. Returns a stats dict."""
    cmap_best = font.getBestCmap()
    added = []     # list of (pf_cp, base_cp, glyph_name)
    no_base = []   # list of (pf_cp, base_cp_or_none) — base missing from font
    already_present = []  # list of pf_cp

    for pf_cp in REHOME_TARGETS:
        if pf_cp in cmap_best:
            already_present.append(pf_cp); continue
        base_cp = base_letter_of(pf_cp)
        if base_cp is None or base_cp not in cmap_best:
            no_base.append((pf_cp, base_cp)); continue
        glyph = cmap_best[base_cp]
        added.append((pf_cp, base_cp, glyph))

    if not dry_run and added:
        # Apply changes: write into every cmap subtable that's a unicode
        # mapping (platformID=0 or platformID=3 with appropriate encoding).
        # We need to also add entries to the appropriate subtable for the
        # codepoints we want to add. The "best cmap" is what shapers pick.
        # We add to ALL unicode subtables so all shapers see the change.
        cmap_table = font["cmap"]
        modified_subtables = 0
        for sub in cmap_table.tables:
            if not sub.isUnicode():
                continue
            # Some subtables only cover BMP (format 4). FE70-FEFC and
            # FC5E-FC63 are both within BMP, so format 4 subtables suffice.
            for pf_cp, _, glyph_name in added:
                if pf_cp not in sub.cmap:
                    sub.cmap[pf_cp] = glyph_name
            modified_subtables += 1
        if modified_subtables == 0:
            # Fall back: create a new format 4 subtable. Rare for modern fonts.
            print("warning: no unicode cmap subtables found; rehoming had no effect",
                  file=sys.stderr)

    return {
        "added": added,
        "no_base": no_base,
        "already_present": already_present,
    }


def main(argv):
    p = argparse.ArgumentParser(description="Rehome base Arabic glyphs into PF-B codepoints.")
    p.add_argument("font", help="source TTF/OTF")
    p.add_argument("--out", default=None,
                   help="output path (default: <stem>-pfb-rehomed<ext>)")
    p.add_argument("--suffix", default="-pfb-rehomed",
                   help="filename suffix when --out not given (default: -pfb-rehomed)")
    p.add_argument("--dry-run", action="store_true",
                   help="report what would be added, don't write a new font")
    p.add_argument("--quiet", action="store_true", help="suppress per-codepoint listing")
    args = p.parse_args(argv[1:])

    src = Path(args.font)
    if not src.exists():
        print(f"font not found: {src}", file=sys.stderr); return 1

    font = TTFont(str(src))
    stats = rehome(font, dry_run=args.dry_run)

    print(f"source: {src}", file=sys.stderr)
    print(f"  already-covered PF codepoints: {len(stats['already_present'])}",
          file=sys.stderr)
    print(f"  rehome-able (base in font):    {len(stats['added'])}",
          file=sys.stderr)
    print(f"  not rehome-able (base missing): {len(stats['no_base'])}",
          file=sys.stderr)

    if not args.quiet:
        if stats["added"]:
            print(f"\nadded {len(stats['added'])} PF → base mappings:", file=sys.stderr)
            for pf, base, glyph in stats["added"]:
                ch = chr(pf) if 0 <= pf <= 0x10FFFF else "?"
                name = unicodedata.name(chr(pf), "?")
                print(f"  U+{pf:04X} '{ch}'  →  U+{base:04X} '{chr(base)}'  (glyph {glyph})  {name}",
                      file=sys.stderr)
        if stats["no_base"]:
            print(f"\nskipped {len(stats['no_base'])} PF codepoints (base missing from font):",
                  file=sys.stderr)
            for pf, base in stats["no_base"][:20]:
                name = unicodedata.name(chr(pf), "?")
                base_str = f"U+{base:04X}" if base is not None else "(no decomposition)"
                print(f"  U+{pf:04X}  base={base_str}  {name}", file=sys.stderr)
            if len(stats["no_base"]) > 20:
                print(f"  ... and {len(stats['no_base']) - 20} more",
                      file=sys.stderr)

    if args.dry_run:
        print("\n(dry run — no file written)", file=sys.stderr)
        return 0

    if not stats["added"]:
        print("\nnothing to do — font already has all rehome-able PF codepoints",
              file=sys.stderr)
        return 0

    out = Path(args.out) if args.out else src.with_name(src.stem + args.suffix + src.suffix)
    font.save(str(out))
    print(f"\nwrote {out}  ({out.stat().st_size} bytes)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
