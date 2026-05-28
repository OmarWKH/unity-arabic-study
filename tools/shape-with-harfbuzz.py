#!/usr/bin/env python3
"""Shape a sequence of codepoints through HarfBuzz and print the resulting
glyph stream with positioning data. This is the same shaper UI Toolkit uses,
so its output is the "correct" reference rendering we want to match.

The output table for each input shows, per output glyph:
  - the output glyph ID (HarfBuzz calls it 'codepoint' in glyph_info — that's
    the GLYPH index, not the Unicode codepoint, despite the misleading name)
  - the glyph name from the font
  - the Unicode codepoint(s) the glyph maps to via cmap, if any
  - x/y_offset, x/y_advance (font design units)
  - the running pen position after this glyph

Compare:
  - Did the glyph count change between input and output? (GSUB ligation /
    decomposition fired.)
  - Are there non-zero y_offsets on the mark glyphs? (GPOS lifted them.)
  - Does the same input in reverse codepoint order produce the same output?
    (Unicode normalization / shaping reordered combining marks by canonical
    combining class.)

Usage:
  python tools/shape-with-harfbuzz.py [font.ttf] [cp_hex cp_hex ...]

Defaults (runs all three test sequences when called with no args):
  font.ttf   unity6/Assets/Fonts/Amiri-Regular.ttf
  sequences  base+shadda+fatha   /   base+fatha+shadda   /   shadda+fatha alone

When you supply codepoints explicitly, only that sequence is shaped:
  python tools/shape-with-harfbuzz.py unity6/Assets/Fonts/Amiri-Regular.ttf 0628 0651 064E
  python tools/shape-with-harfbuzz.py unity6/Assets/Fonts/Amiri-Regular.ttf 0628 064E 0651

Install:
  pip install uharfbuzz fonttools
"""

from __future__ import annotations
import sys
from pathlib import Path
from typing import Optional

try:
    import uharfbuzz as hb
except ImportError:
    print("uharfbuzz is required:  pip install uharfbuzz", file=sys.stderr)
    sys.exit(2)
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


DEFAULT_FONT = "unity6/Assets/Fonts/Amiri-Regular.ttf"

# Default test set when called with no codepoint args.
DEFAULT_SEQUENCES: list[tuple[str, list[int]]] = [
    ("base + shadda + fatha   (user's case)", [0x0628, 0x0651, 0x064E]),
    ("base + fatha + shadda   (reverse mark order)", [0x0628, 0x064E, 0x0651]),
    ("shadda + fatha          (no base — marks alone)", [0x0651, 0x064E]),
    ("U+FC60 alone            (RTLTMPro's output, for contrast)", [0xFC60]),
    ("base + U+FC60           (what TMP renders when RTLTMPro is on)", [0x0628, 0xFC60]),
]


def make_lookups(font_path: str):
    """Build name + cmap lookups via fontTools so we can decorate the
    HarfBuzz glyph stream with human-readable labels."""
    ft = TTFont(font_path)
    glyph_order = ft.getGlyphOrder()
    gid_to_name = {i: name for i, name in enumerate(glyph_order)}
    gname_to_cps: dict[str, list[int]] = {}
    for cp, name in ft.getBestCmap().items():
        gname_to_cps.setdefault(name, []).append(cp)
    return gid_to_name, gname_to_cps


def fmt_cp(cp: int) -> str:
    try:
        ch = chr(cp)
    except (ValueError, OverflowError):
        ch = "?"
    return f"U+{cp:04X} '{ch}'"


def shape_and_report(font_path: str, label: str, codepoints: list[int],
                     gid_to_name: dict[int, str],
                     gname_to_cps: dict[str, list[int]]) -> None:
    with open(font_path, "rb") as f:
        font_data = f.read()
    blob = hb.Blob(font_data)
    face = hb.Face(blob)
    upem = face.upem
    font = hb.Font(face)

    buf = hb.Buffer()
    buf.add_codepoints(codepoints)
    buf.guess_segment_properties()
    # Capture what HarfBuzz auto-detected — useful when the buffer has no
    # base letter and direction can't be inferred.
    script = buf.script
    direction = buf.direction
    language = buf.language

    hb.shape(font, buf, {})  # no feature overrides; defaults match UI Toolkit

    infos = buf.glyph_infos
    positions = buf.glyph_positions

    print(f"--- {label} ---")
    print(f"  input ({len(codepoints)}): " + "  ".join(fmt_cp(c) for c in codepoints))
    print(f"  detected: script={script}  direction={direction}  language={language}  upem={upem}")
    print(f"  output ({len(infos)}):")
    print(f"    {'#':>2}  {'gid':>5}  {'glyph':<14}  {'codepoint':<14}  "
          f"{'xOff':>5} {'yOff':>5} {'xAdv':>5} {'yAdv':>5}  cluster")
    pen_x, pen_y = 0, 0
    for i, (info, pos) in enumerate(zip(infos, positions)):
        gid = info.codepoint
        gname = gid_to_name.get(gid, "?")
        cps = gname_to_cps.get(gname, [])
        cp_str = ", ".join(f"U+{cp:04X}" for cp in cps) if cps else "(no cmap)"
        print(f"    {i:>2}  {gid:>5}  {gname:<14}  {cp_str:<14}  "
              f"{pos.x_offset:>5} {pos.y_offset:>5} {pos.x_advance:>5} {pos.y_advance:>5}  c={info.cluster}")
        pen_x += pos.x_advance
        pen_y += pos.y_advance
    print(f"  final pen: ({pen_x}, {pen_y})")
    print()


def main(argv: list[str]) -> int:
    args = argv[1:]
    # First arg is the font path if it doesn't look like a hex codepoint.
    if args and not all(c in "0123456789abcdefABCDEF" for c in args[0]):
        font_path = Path(args[0])
        args = args[1:]
    else:
        font_path = Path(DEFAULT_FONT)

    if not font_path.exists():
        print(f"font not found: {font_path}", file=sys.stderr)
        return 2

    if args:
        try:
            cps = [int(a, 16) for a in args]
        except ValueError as ex:
            print(f"bad codepoint argument: {ex}", file=sys.stderr)
            return 2
        sequences = [(f"custom sequence  {' '.join(fmt_cp(c) for c in cps)}", cps)]
    else:
        sequences = DEFAULT_SEQUENCES

    gid_to_name, gname_to_cps = make_lookups(str(font_path))

    print(f"font: {font_path}")
    print()
    for label, cps in sequences:
        shape_and_report(str(font_path), label, cps, gid_to_name, gname_to_cps)

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
