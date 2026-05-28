#!/usr/bin/env python3
"""Inspect a font's GPOS table for a Mark-to-Mark anchor between two codepoints.

Default check: shadda (U+0651) as the base mark, fatha (U+064E) as the
combining mark in Amiri-Regular.ttf. We need to know whether the font has
this anchor pair at all and HOW it's reached:

  - Directly through a Type 6 (MkMk) lookup that the `mkmk` feature
    references for script `arab`. TMP's GPOS importer can usually pull
    this kind out.

  - Indirectly through a Type 8 chained-context lookup wrapping a Type 6
    inner. This is what TMP's importer historically does NOT follow, so
    the anchor pair exists in the font but never makes it into the TMP
    font asset — which matches the symptom of "UI Toolkit (HarfBuzz)
    renders shadda+fatha correctly, TMP overlaps them."

Usage:
  python tools/inspect-gpos-markmark.py [font.ttf] [base_cp_hex] [comb_cp_hex]

Defaults:
  font.ttf      unity6/Assets/Fonts/Amiri-Regular.ttf
  base_cp_hex   0651  (ARABIC SHADDA)
  comb_cp_hex   064E  (ARABIC FATHA)

Examples:
  python tools/inspect-gpos-markmark.py
  python tools/inspect-gpos-markmark.py unity6/Assets/Fonts/Amiri-Regular.ttf 0651 064F
  python tools/inspect-gpos-markmark.py other.ttf 0651 064B

Install:
  pip install fonttools
"""

from __future__ import annotations
import sys
from pathlib import Path
from typing import Optional

# Windows consoles default to cp1252; we print arrows and Arabic glyph names.
try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

try:
    from fontTools.ttLib import TTFont
except ImportError:
    print("fontTools is required:  pip install fonttools", file=sys.stderr)
    sys.exit(2)


DEFAULT_FONT = "unity6/Assets/Fonts/Amiri-Regular.ttf"
DEFAULT_BASE_CP = 0x0651  # SHADDA
DEFAULT_COMB_CP = 0x064E  # FATHA


# ---------------------------------------------------------------- helpers ----

def glyph_for_cp(font, cp: int) -> Optional[str]:
    return font.getBestCmap().get(cp)


def unwrap_extension(sub):
    """If `sub` is an Extension subtable (LookupType 9), return the real
    (type, subtable) it wraps. Otherwise return (sub.LookupType, sub)."""
    if getattr(sub, "ExtensionLookupType", None) is not None:
        return sub.ExtensionLookupType, sub.ExtSubTable
    return sub.LookupType, sub


def features_referencing(gpos, lookup_idx: int) -> list[tuple[str, str, str]]:
    """Return list of (script, language, feature_tag) tuples that reach this
    lookup directly through the FeatureList."""
    out = []
    feature_list = gpos.table.FeatureList
    for sr in gpos.table.ScriptList.ScriptRecord:
        script = sr.Script
        lang_systems = []
        if script.DefaultLangSys is not None:
            lang_systems.append(("(dflt)", script.DefaultLangSys))
        for lr in script.LangSysRecord:
            lang_systems.append((lr.LangSysTag, lr.LangSys))
        for lang_tag, ls in lang_systems:
            for fi in ls.FeatureIndex:
                fr = feature_list.FeatureRecord[fi]
                if lookup_idx in fr.Feature.LookupListIndex:
                    out.append((sr.ScriptTag, lang_tag, fr.FeatureTag))
    return out


def check_markmark_for_pair(st, base_mark_g: str, comb_mark_g: str) -> Optional[dict]:
    """For a MarkMarkPos subtable, return anchor info if (base_mark_g,
    comb_mark_g) is covered, else None.

    In OpenType MkMk:
      Mark1 = base mark (the one already on the letter — shadda)
      Mark2 = combining mark (the one stacking on top — fatha)
    """
    m1_cov = st.Mark1Coverage.glyphs
    m2_cov = st.Mark2Coverage.glyphs
    if base_mark_g not in m1_cov or comb_mark_g not in m2_cov:
        return None
    m1_idx = m1_cov.index(base_mark_g)
    m2_idx = m2_cov.index(comb_mark_g)
    m1_rec = st.Mark1Array.MarkRecord[m1_idx]
    klass = m1_rec.Class
    m1_anchor = m1_rec.MarkAnchor
    m2_anchors = st.Mark2Array.Mark2Record[m2_idx].Mark2Anchor
    if klass >= len(m2_anchors):
        return {"class": klass, "missing_mark2_anchor": True}
    m2_anchor = m2_anchors[klass]
    return {
        "class": klass,
        "mark1_anchor": (m1_anchor.XCoordinate, m1_anchor.YCoordinate) if m1_anchor else None,
        "mark2_anchor": (m2_anchor.XCoordinate, m2_anchor.YCoordinate) if m2_anchor else None,
    }


def inner_lookups_of_context(real_sub) -> list[int]:
    """Return the inner LookupListIndex values that a Context (Type 7) or
    ChainContext (Type 8) subtable invokes. Only Format 3 is supported here,
    which is what modern fonts (including Amiri) use."""
    if getattr(real_sub, "Format", None) != 3:
        return []
    # Field name varies across fontTools versions: PosLookupRecord (newer)
    # or SubstLookupRecord (older, name leftover from GSUB-style construction).
    records = (
        getattr(real_sub, "PosLookupRecord", None)
        or getattr(real_sub, "SubstLookupRecord", None)
        or []
    )
    return [r.LookupListIndex for r in records]


def walk(gpos, idx: int, base_g: str, comb_g: str, path: list, hits: list, visited: set):
    """Depth-first walk of a lookup tree. Records a hit whenever we find a
    Type 6 subtable that covers the (base, comb) pair; remembers the path we
    took to reach it (top-level lookup → chain → ...)."""
    key = (idx, tuple(path))
    if key in visited:
        return
    visited.add(key)

    lookup = gpos.table.LookupList.Lookup[idx]
    for sub_i, sub in enumerate(lookup.SubTable):
        real_type, real_sub = unwrap_extension(sub)
        if real_type == 6:
            res = check_markmark_for_pair(real_sub, base_g, comb_g)
            if res is not None:
                hits.append({
                    "lookup_idx": idx,
                    "subtable_idx": sub_i,
                    "path": list(path),
                    "result": res,
                })
        elif real_type in (7, 8):
            for inner_idx in inner_lookups_of_context(real_sub):
                walk(
                    gpos, inner_idx, base_g, comb_g,
                    path + [(idx, f"Type{real_type}-ctx")],
                    hits, visited,
                )


# ------------------------------------------------------------------- main ----

def main(argv: list[str]) -> int:
    font_path = Path(argv[1] if len(argv) > 1 else DEFAULT_FONT)
    base_cp = int(argv[2], 16) if len(argv) > 2 else DEFAULT_BASE_CP
    comb_cp = int(argv[3], 16) if len(argv) > 3 else DEFAULT_COMB_CP

    if not font_path.exists():
        print(f"font not found: {font_path}", file=sys.stderr)
        return 2

    font = TTFont(str(font_path))
    if "GPOS" not in font:
        print(f"{font_path.name} has no GPOS table — nothing to inspect.")
        return 1
    gpos = font["GPOS"]

    base_g = glyph_for_cp(font, base_cp)
    comb_g = glyph_for_cp(font, comb_cp)
    print(f"font:        {font_path}")
    print(f"base mark:   U+{base_cp:04X}  →  glyph name '{base_g}'")
    print(f"comb mark:   U+{comb_cp:04X}  →  glyph name '{comb_g}'")
    if not base_g or not comb_g:
        print("one of the codepoints is not in the font's cmap — cannot proceed.")
        return 1
    print()

    # Big picture: every GPOS lookup, its type(s), and the features that
    # reference it. Type 9 (Extension) entries are unwrapped so the printed
    # types are the *real* lookup types.
    print("=== GPOS lookups ===")
    lookup_types: dict[int, list[int]] = {}
    for i, lookup in enumerate(gpos.table.LookupList.Lookup):
        types = []
        for sub in lookup.SubTable:
            rt, _ = unwrap_extension(sub)
            types.append(rt)
        lookup_types[i] = types
        refs = features_referencing(gpos, i)
        type_str = "/".join(str(t) for t in types) if types else "?"
        if refs:
            head = ", ".join(f"{s}/{l}/{f}" for s, l, f in refs[:3])
            if len(refs) > 3:
                head += f" +{len(refs) - 3}"
        else:
            head = "(not referenced by any feature directly)"
        print(f"  #{i:3d}  type={type_str:<10}  used-by={head}")
    print()

    # The actual diagnostic question: is there an anchor pair for
    # (base_mark, comb_mark) reachable through any GPOS lookup, direct or
    # contextually wrapped?
    print(f"=== Searching for Mark-to-Mark anchor pair ({base_g}, {comb_g}) ===")
    hits: list[dict] = []
    visited: set = set()
    for i in range(len(gpos.table.LookupList.Lookup)):
        walk(gpos, i, base_g, comb_g, [], hits, visited)

    if not hits:
        print("  NO anchor pair found in any Type-6 lookup, direct or contextual.")
        print()
        print("  Conclusion: the font does not define a Mark-to-Mark adjustment for")
        print("  this pair. TMP can't import what isn't there; UI Toolkit/HarfBuzz")
        print("  wouldn't be able to render it correctly either. Re-check your")
        print("  codepoints and the feature you actually want.")
        return 1

    # Group hits by direct (top-level lookup is the Type 6 itself, path
    # empty) vs. indirect (reached only via context lookup).
    direct = [h for h in hits if not h["path"]]
    indirect = [h for h in hits if h["path"]]

    if direct:
        print(f"  ✓ DIRECT match — {len(direct)} hit(s):")
        for h in direct:
            print_hit(h, gpos)
    if indirect:
        print(f"  ✓ INDIRECT (contextually wrapped) — {len(indirect)} hit(s):")
        for h in indirect:
            print_hit(h, gpos)

    # Verdict on TMP importability:
    print()
    direct_with_feature = [
        h for h in direct
        if features_referencing(gpos, h["lookup_idx"])
    ]
    if direct_with_feature:
        print("Verdict:  the anchor IS reachable through a direct Type-6 lookup that's")
        print("          referenced by a feature (e.g. mkmk). TMP's importer should")
        print("          have picked this up. If TMP doesn't render it, the failure")
        print("          is in TMP's enumeration of the lookup's coverage / classes,")
        print("          not in the lookup's accessibility from a feature.")
    elif indirect:
        print("Verdict:  the anchor is ONLY reachable through a contextually wrapped")
        print("          lookup (Type 7 or 8 → inner Type 6). TMP's GPOS importer")
        print("          historically does NOT recurse through context lookups, which")
        print("          would fully explain why the anchor is in the font but not in")
        print("          the TMP font asset — and why HarfBuzz (UI Toolkit) handles it")
        print("          correctly while TMP doesn't.")
    else:
        print("Verdict:  anchor exists in a Type-6 lookup that's not referenced by any")
        print("          feature anywhere in the GPOS table — it's effectively dead.")
        print("          TMP won't see it; HarfBuzz won't apply it either. Suspect a")
        print("          font-build issue.")
    return 0


def print_hit(h: dict, gpos) -> None:
    path_str = (
        " → ".join(f"#{idx} ({tag})" for idx, tag in h["path"]) + f" → #{h['lookup_idx']}"
        if h["path"] else f"#{h['lookup_idx']}"
    )
    res = h["result"]
    print(f"    path:        {path_str}")
    print(f"    subtable:    {h['subtable_idx']}")
    print(f"    mark class:  {res.get('class')}")
    print(f"    base anchor: {res.get('mark1_anchor')}")
    print(f"    comb anchor: {res.get('mark2_anchor')}")
    refs = features_referencing(gpos, h["lookup_idx"])
    if refs:
        print(f"    features:    {', '.join(f'{s}/{l}/{f}' for s, l, f in refs)}")
    else:
        print(f"    features:    (none directly — reached only through context)")
    print()


if __name__ == "__main__":
    sys.exit(main(sys.argv))
