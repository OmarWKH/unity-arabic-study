#!/usr/bin/env python3
"""Inspect a font's GSUB table for a Ligature Substitution covering a
specific input codepoint sequence.

Default check: <U+0651 SHADDA, U+064E FATHA> → ? in Amiri-Regular.ttf.
Expected outcome for properly-built Arabic fonts: a substitution producing
the glyph for U+FC60 (ARABIC LIGATURE SHADDA WITH FATHA ISOLATED FORM)
or an internal equivalent.

Two failure modes the script distinguishes between:

  - Direct Type-4 (LigatureSubst) lookup, referenced by `rlig` / `ccmp` /
    `liga` under script `arab`. TMP's GSUB importer should pick this up;
    if shadda+fatha overlap in TMP, this is the one to suspect of being
    correctly imported but somehow not applied at render time (feature
    enable, gating, or shaper-pass ordering).

  - Type-6 (ChainedContextSubst) wrapping a Type-4 Ligature. TMP's
    importer historically does not recurse into chained-context wrappers,
    which would explain TMP missing the substitution while HarfBuzz
    (UI Toolkit) applies it correctly.

Usage:
  python tools/inspect-gsub-ligature.py [font.ttf] [cp1_hex cp2_hex ...]

Defaults:
  font.ttf   tmp-preview/Assets/Fonts/Amiri-Regular.ttf
  sequence   0651 064E   (SHADDA + FATHA)

Examples:
  python tools/inspect-gsub-ligature.py
  python tools/inspect-gsub-ligature.py tmp-preview/Assets/Fonts/Amiri-Regular.ttf 0651 064F
  python tools/inspect-gsub-ligature.py tmp-preview/Assets/Fonts/Amiri-Regular.ttf 0644 0644 0647

Install:
  pip install fonttools
"""

from __future__ import annotations
import sys
from pathlib import Path
from typing import Optional

try:
    from fontTools.ttLib import TTFont
except ImportError:
    print("fontTools is required:  pip install fonttools", file=sys.stderr)
    sys.exit(2)

# Windows consoles default to cp1252; we print arrows and Arabic glyph names.
try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass


DEFAULT_FONT = "tmp-preview/Assets/Fonts/Amiri-Regular.ttf"
DEFAULT_SEQUENCE = [0x0651, 0x064E]  # SHADDA, FATHA


# Reverse cmap so we can resolve an output glyph name back to a codepoint
# when reporting hits. Built lazily per font.
def reverse_cmap(font) -> dict[str, list[int]]:
    out: dict[str, list[int]] = {}
    for cp, gname in font.getBestCmap().items():
        out.setdefault(gname, []).append(cp)
    return out


def glyph_for_cp(font, cp: int) -> Optional[str]:
    return font.getBestCmap().get(cp)


def unwrap_extension(sub):
    """If `sub` is an Extension subtable (LookupType 7 in GSUB), return the
    real (type, subtable). Otherwise (type, sub)."""
    if getattr(sub, "ExtensionLookupType", None) is not None:
        return sub.ExtensionLookupType, sub.ExtSubTable
    return sub.LookupType, sub


def features_referencing(gsub, lookup_idx: int) -> list[tuple[str, str, str]]:
    out = []
    feature_list = gsub.table.FeatureList
    for sr in gsub.table.ScriptList.ScriptRecord:
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


def check_ligature_subtable(st, sequence_gnames: list[str]) -> Optional[dict]:
    """For a LigatureSubst (Type 4) subtable, return a hit dict if there's a
    ligature matching the input glyph-name sequence. Else None."""
    if not sequence_gnames:
        return None
    first = sequence_gnames[0]
    rest = sequence_gnames[1:]
    ligs = getattr(st, "ligatures", None)
    if not ligs:
        return None
    candidates = ligs.get(first)
    if not candidates:
        return None
    for lig in candidates:
        # lig.Component is the REST of the input sequence (first is implicit
        # via the coverage / dict key).
        if list(lig.Component) == rest:
            return {"out_glyph": lig.LigGlyph, "components": [first] + list(lig.Component)}
    return None


def inner_lookups_of_context(real_sub) -> list[int]:
    """Inner LookupListIndex values invoked by a Context (Type 5) or
    ChainedContext (Type 6) GSUB subtable. Format 3 only — what modern
    fonts use."""
    if getattr(real_sub, "Format", None) != 3:
        return []
    records = (
        getattr(real_sub, "SubstLookupRecord", None)
        or getattr(real_sub, "PosLookupRecord", None)  # belt-and-suspenders
        or []
    )
    return [r.LookupListIndex for r in records]


def walk(gsub, idx: int, seq: list[str], path: list, hits: list, visited: set):
    """Recursively walk a GSUB lookup tree looking for a Type-4 subtable
    whose ligatures cover the input glyph sequence. Type-7 (Extension)
    transparently unwraps; Type-5 and Type-6 recurse into their inner
    lookup records."""
    key = (idx, tuple(path))
    if key in visited:
        return
    visited.add(key)

    lookup = gsub.table.LookupList.Lookup[idx]
    for sub_i, sub in enumerate(lookup.SubTable):
        real_type, real_sub = unwrap_extension(sub)
        if real_type == 4:
            res = check_ligature_subtable(real_sub, seq)
            if res is not None:
                hits.append({
                    "lookup_idx": idx,
                    "subtable_idx": sub_i,
                    "path": list(path),
                    "result": res,
                })
        elif real_type in (5, 6):
            for inner_idx in inner_lookups_of_context(real_sub):
                walk(
                    gsub, inner_idx, seq,
                    path + [(idx, f"Type{real_type}-ctx")],
                    hits, visited,
                )


def fmt_glyph(font, rev: dict[str, list[int]], gname: str) -> str:
    cps = rev.get(gname, [])
    if not cps:
        return f"'{gname}' (no codepoint)"
    if len(cps) == 1:
        return f"'{gname}' U+{cps[0]:04X}"
    return f"'{gname}' U+{cps[0]:04X} +{len(cps) - 1}"


def main(argv: list[str]) -> int:
    args = argv[1:]
    font_path = Path(args[0]) if args and not all(c in "0123456789abcdefABCDEF" for c in args[0]) else Path(DEFAULT_FONT)
    if args and font_path == Path(args[0]):
        args = args[1:]
    if args:
        try:
            seq_cps = [int(a, 16) for a in args]
        except ValueError as ex:
            print(f"bad codepoint argument: {ex}", file=sys.stderr)
            return 2
    else:
        seq_cps = list(DEFAULT_SEQUENCE)

    if not font_path.exists():
        print(f"font not found: {font_path}", file=sys.stderr)
        return 2

    font = TTFont(str(font_path))
    if "GSUB" not in font:
        print(f"{font_path.name} has no GSUB table — nothing to inspect.")
        return 1
    gsub = font["GSUB"]
    rev = reverse_cmap(font)

    seq_gnames: list[str] = []
    print(f"font:        {font_path}")
    print("sequence:")
    for cp in seq_cps:
        g = glyph_for_cp(font, cp)
        print(f"  U+{cp:04X}  →  '{g}'")
        if g is None:
            print(f"codepoint U+{cp:04X} not in cmap — cannot proceed.")
            return 1
        seq_gnames.append(g)
    print()

    # Big picture: every GSUB lookup with its real type(s) and the features
    # that reference it. Extension entries are unwrapped.
    print("=== GSUB lookups ===")
    for i, lookup in enumerate(gsub.table.LookupList.Lookup):
        types = []
        for sub in lookup.SubTable:
            rt, _ = unwrap_extension(sub)
            types.append(rt)
        refs = features_referencing(gsub, i)
        type_str = "/".join(str(t) for t in types) if types else "?"
        if refs:
            head = ", ".join(f"{s}/{l}/{f}" for s, l, f in refs[:3])
            if len(refs) > 3:
                head += f" +{len(refs) - 3}"
        else:
            head = "(not referenced by any feature directly)"
        print(f"  #{i:3d}  type={type_str:<10}  used-by={head}")
    print()

    # Search for the substitution.
    seq_pretty = " + ".join(fmt_glyph(font, rev, g) for g in seq_gnames)
    print(f"=== Searching for Ligature Substitution ({seq_pretty}) ===")
    hits: list[dict] = []
    visited: set = set()
    for i in range(len(gsub.table.LookupList.Lookup)):
        walk(gsub, i, seq_gnames, [], hits, visited)

    if not hits:
        print("  NO ligature substitution found, direct or contextual.")
        print()
        print("  Conclusion: the font does not collapse this glyph sequence into")
        print("  a single ligature glyph. Either the visual stacking is handled")
        print("  another way (Mark-to-Base anchor heights, GPOS), or the sequence")
        print("  isn't one the font supports.")
        return 1

    direct = [h for h in hits if not h["path"]]
    indirect = [h for h in hits if h["path"]]

    if direct:
        print(f"  ✓ DIRECT match — {len(direct)} hit(s):")
        for h in direct:
            print_hit(h, font, gsub, rev)
    if indirect:
        print(f"  ✓ INDIRECT (contextually wrapped) — {len(indirect)} hit(s):")
        for h in indirect:
            print_hit(h, font, gsub, rev)

    print()
    direct_with_feature = [
        h for h in direct
        if features_referencing(gsub, h["lookup_idx"])
    ]
    if direct_with_feature:
        print("Verdict:  the substitution IS reachable through a direct Type-4")
        print("          ligature lookup that's referenced by a feature. TMP's GSUB")
        print("          importer should have picked this up. If TMP still doesn't")
        print("          apply it, suspect the feature isn't enabled on the font")
        print("          asset's m_ActiveFontFeatures, or TMP's shaper isn't running")
        print("          the lookup on this glyph sequence (a TMP bug rather than a")
        print("          coverage issue).")
    elif indirect:
        print("Verdict:  the substitution is ONLY reachable through a contextually")
        print("          wrapped lookup (Type 5 or 6 → inner Type 4). TMP's GSUB")
        print("          importer historically does NOT recurse through context")
        print("          lookups, which fully explains why HarfBuzz (UI Toolkit)")
        print("          renders the cluster correctly while TMP doesn't — TMP")
        print("          never sees the substitution, leaves both glyphs separate,")
        print("          and both attach to the base letter at the same Mark-to-")
        print("          Base anchor, hence the overlap.")
    else:
        print("Verdict:  the substitution exists in a Type-4 lookup that's not")
        print("          referenced by any feature directly — it's effectively dead.")
        print("          Suspect a font-build issue.")
    return 0


def print_hit(h: dict, font, gsub, rev: dict[str, list[int]]) -> None:
    path_str = (
        " → ".join(f"#{idx} ({tag})" for idx, tag in h["path"]) + f" → #{h['lookup_idx']}"
        if h["path"] else f"#{h['lookup_idx']}"
    )
    res = h["result"]
    components = " + ".join(fmt_glyph(font, rev, g) for g in res["components"])
    out_glyph = res["out_glyph"]
    out_label = fmt_glyph(font, rev, out_glyph)
    print(f"    path:        {path_str}")
    print(f"    subtable:    {h['subtable_idx']}")
    print(f"    substitutes: {components}")
    print(f"    output:      {out_label}")
    refs = features_referencing(gsub, h["lookup_idx"])
    if refs:
        print(f"    features:    {', '.join(f'{s}/{l}/{f}' for s, l, f in refs)}")
    else:
        print(f"    features:    (none directly — reached only through context)")
    print()


if __name__ == "__main__":
    sys.exit(main(sys.argv))
