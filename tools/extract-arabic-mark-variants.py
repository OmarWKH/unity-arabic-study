#!/usr/bin/env python3
"""Extract Arabic mark-variant Mark-to-Mark adjustments from a font.

This walks GSUB to find contextual single substitutions (Type-6
ChainedContext wrapping Type-1 Single) that replace a harakat with a
variant glyph — typically a "lifted" version designed to stack above
shadda. For each substitution found, looks up both the original mark's
and the variant mark's Mark-to-Base anchor (in GPOS Type 4), and computes
the (x, y) delta between them. Emits a JSON list of synthetic
Mark-to-Mark records that, when injected into a TMP font asset's feature
table, replicate the effect HarfBuzz produces — and that TMP's importer
fails to extract because it doesn't recurse through chained-context
wrappers.

Output schema (JSON, flat for Unity JsonUtility compatibility):

  {
    "schemaVersion": 1,
    "extractor": "arabic-mark-variants",
    "extractedAtIso": "2026-05-24T...",
    "sourceFont": ".../Amiri-Regular.ttf",
    "sourceFontFamily": "Amiri",
    "records": [
      {
        "kind": "mark_to_mark",
        "baseMarkGlyphID": 97,
        "baseMarkName": "uni0651",
        "baseMarkCodepoint": 1617,
        "combiningMarkGlyphID": 84,
        "combiningMarkName": "uni064E",
        "combiningMarkCodepoint": 1614,
        "baseAnchorX": 0,
        "baseAnchorY": 0,
        "combiningAdjustmentX": 0,
        "combiningAdjustmentY": 51,
        "sourceNote": "..."
      }
    ],
    "warnings": ["..."]
  }

Usage:
  python tools/extract-arabic-mark-variants.py FONT.ttf
  python tools/extract-arabic-mark-variants.py FONT.ttf --out out.json

When invoked from Unity, --out is omitted and the JSON is streamed on
stdout. Stderr is reserved for human-readable status messages so Unity
can either ignore it or surface it in the Console.
"""

from __future__ import annotations
import argparse
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

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


# Harakat (Arabic diacritics) that we expect to see as substitution inputs.
# These are the glyphs whose variant forms get swapped in by Type-6 chains
# in well-built Arabic fonts.
HARAKAT_CODEPOINTS = {
    0x064B, 0x064C, 0x064D,  # tanwin: fathatan, dammatan, kasratan
    0x064E, 0x064F, 0x0650,  # fatha, damma, kasra
    0x0651,                  # shadda
    0x0652,                  # sukun
    0x0653, 0x0654, 0x0655,  # madda above, hamza above, hamza below
    0x0670,                  # superscript alef
}


def unwrap(sub):
    """If `sub` is an OpenType Extension subtable (LookupType 7 in GSUB,
    LookupType 9 in GPOS), return the real (type, subtable). Otherwise
    return the subtable's declared type and itself."""
    if getattr(sub, "ExtensionLookupType", None) is not None:
        return sub.ExtensionLookupType, sub.ExtSubTable
    return sub.LookupType, sub


def features_referencing(table, lookup_idx):
    """Return (script_tag, lang_tag, feature_tag) tuples for every feature
    that points at this lookup directly through the FeatureList."""
    out = []
    feature_list = table.table.FeatureList
    for sr in table.table.ScriptList.ScriptRecord:
        script = sr.Script
        lang_systems = []
        if script.DefaultLangSys is not None:
            lang_systems.append(("dflt", script.DefaultLangSys))
        for lr in script.LangSysRecord:
            lang_systems.append((lr.LangSysTag, lr.LangSys))
        for lang_tag, ls in lang_systems:
            for fi in ls.FeatureIndex:
                fr = feature_list.FeatureRecord[fi]
                if lookup_idx in fr.Feature.LookupListIndex:
                    out.append((sr.ScriptTag, lang_tag, fr.FeatureTag))
    return out


def find_mark_to_base_anchor(gpos, glyph_name):
    """Find the first GPOS Type-4 Mark-to-Base anchor record for `glyph_name`
    (treating it as a mark glyph). Returns a dict with x/y/class/lookup, or
    None if no record exists.

    Note: this is the anchor on the MARK glyph — the point on the mark that
    coincides with the base letter's anchor when the mark gets positioned."""
    for i, lookup in enumerate(gpos.table.LookupList.Lookup):
        for sub in lookup.SubTable:
            t, real = unwrap(sub)
            if t != 4:
                continue
            if glyph_name in real.MarkCoverage.glyphs:
                idx = real.MarkCoverage.glyphs.index(glyph_name)
                rec = real.MarkArray.MarkRecord[idx]
                anchor = rec.MarkAnchor
                return {
                    "x": anchor.XCoordinate,
                    "y": anchor.YCoordinate,
                    "class": rec.Class,
                    "lookup": i,
                }
    return None


def collect_type1_lookups(gsub):
    """For every GSUB Type-1 Single substitution lookup in the font,
    return a dict { lookup_idx -> { input_glyph_name -> output_glyph_name } }.
    These are the inner lookups that Type-6 chained-context lookups invoke
    when substituting variant glyphs."""
    out = {}
    for i, lookup in enumerate(gsub.table.LookupList.Lookup):
        for sub in lookup.SubTable:
            t, real = unwrap(sub)
            if t != 1:
                continue
            mapping = getattr(real, "mapping", None)
            if not mapping:
                continue
            out.setdefault(i, {}).update(mapping)
    return out


def walk_chained_contexts(gsub, type1_lookups, interesting_features, want_script):
    """Yield substitution descriptors for every Type-6 ChainedContext lookup
    in GSUB that's referenced by an interesting feature for the given script
    and that invokes a Type-1 inner lookup mapping a harakat.

    Each yielded item describes (input_glyph -> output_glyph) plus the
    surrounding context (backtrack / lookahead glyphs)."""
    for i, lookup in enumerate(gsub.table.LookupList.Lookup):
        refs = features_referencing(gsub, i)
        ref_features = {f for _, _, f in refs}
        if not (interesting_features & ref_features):
            continue
        if want_script and not any(s == want_script for s, _, _ in refs):
            continue

        for sub_i, sub in enumerate(lookup.SubTable):
            t, real = unwrap(sub)
            if t != 6:
                continue
            if getattr(real, "Format", None) != 3:
                # Format 1 / 2 use class-based matching; rarer for the
                # substitutions we care about. Skip with a warning emitted
                # by the caller if needed.
                continue

            backtrack = [list(cov.glyphs) for cov in (real.BacktrackCoverage or [])]
            input_cov = [list(cov.glyphs) for cov in (real.InputCoverage or [])]
            lookahead = [list(cov.glyphs) for cov in (real.LookAheadCoverage or [])]
            inner_records = getattr(real, "SubstLookupRecord", None) or []

            for srec in inner_records:
                inner_idx = srec.LookupListIndex
                mapping = type1_lookups.get(inner_idx)
                if not mapping:
                    continue
                seq_idx = srec.SequenceIndex
                if seq_idx >= len(input_cov):
                    continue
                # Each glyph in input_cov[seq_idx] is a candidate input that
                # the Type-1 inner lookup might substitute. The inner lookup
                # only acts on glyphs in its own coverage, so we intersect.
                for input_g in input_cov[seq_idx]:
                    if input_g not in mapping:
                        continue
                    output_g = mapping[input_g]
                    yield {
                        "input_glyph": input_g,
                        "output_glyph": output_g,
                        "outer_lookup": i,
                        "inner_lookup": inner_idx,
                        "subtable": sub_i,
                        "feature_tags": sorted(ref_features),
                        "scripts": sorted({s for s, _, _ in refs}),
                        "backtrack": backtrack,
                        "input_at_seq_idx": list(input_cov[seq_idx]),
                        "lookahead": lookahead,
                    }


def main(argv):
    p = argparse.ArgumentParser(
        description="Extract Arabic mark-variant Mark-to-Mark records from a font.",
    )
    p.add_argument("font", help="path to .ttf or .otf file")
    p.add_argument("--out", default=None, help="write JSON to this file (default: stdout)")
    p.add_argument("--script", default="arab", help="OpenType script tag to filter by (default: arab; '' to disable)")
    p.add_argument(
        "--features",
        default="rlig,ccmp,liga",
        help="comma-separated feature tags whose Type-6 lookups to walk (default: rlig,ccmp,liga)",
    )
    args = p.parse_args(argv[1:])

    font_path = Path(args.font)
    if not font_path.exists():
        print(f"font not found: {font_path}", file=sys.stderr)
        return 2

    font = TTFont(str(font_path))
    if "GSUB" not in font:
        print("font has no GSUB table — nothing to extract", file=sys.stderr)
        return 1
    if "GPOS" not in font:
        print("font has no GPOS table — anchors not available", file=sys.stderr)
        return 1

    cmap = font.getBestCmap()
    name_to_cps = {}
    for cp, name in cmap.items():
        name_to_cps.setdefault(name, []).append(cp)

    shadda_name = cmap.get(0x0651)
    if not shadda_name:
        print("font has no shadda (U+0651) — extractor will produce no records", file=sys.stderr)

    type1_lookups = collect_type1_lookups(font["GSUB"])
    interesting_features = set(args.features.split(","))

    records = []
    warnings = []
    seen_pairs = set()

    for sub in walk_chained_contexts(font["GSUB"], type1_lookups,
                                     interesting_features, args.script or None):
        input_g = sub["input_glyph"]
        output_g = sub["output_glyph"]
        input_cps = name_to_cps.get(input_g, [])

        # Filter: input must be a harakat (the case we know how to model).
        if not any(cp in HARAKAT_CODEPOINTS for cp in input_cps):
            continue

        input_anchor = find_mark_to_base_anchor(font["GPOS"], input_g)
        output_anchor = find_mark_to_base_anchor(font["GPOS"], output_g)

        if input_anchor is None:
            warnings.append(f"no Mark-to-Base anchor for input '{input_g}' — skipped")
            continue
        if output_anchor is None:
            warnings.append(
                f"no Mark-to-Base anchor for variant '{output_g}' (substituted from '{input_g}') — skipped"
            )
            continue

        # delta = (variant_anchor - original_anchor) in font design units.
        # When the variant attaches to the base, it's positioned so its
        # anchor lands at the base's. Likewise for the original. The
        # difference between the two anchors is the visual offset between
        # variant and original, which is what we need to add to the
        # original mark to make it sit where the variant would.
        delta_x = output_anchor["x"] - input_anchor["x"]
        delta_y = output_anchor["y"] - input_anchor["y"]

        # Identify the trigger. For Arabic harakat variants the canonical
        # case is "after shadda"; we check the backtrack coverage to
        # confirm. If shadda isn't there we still record but flag a warning.
        backtrack_flat = [g for level in sub["backtrack"] for g in level]
        is_after_shadda = shadda_name in backtrack_flat
        if not is_after_shadda:
            warnings.append(
                f"substitution {input_g} -> {output_g} not in shadda context "
                f"(backtrack={backtrack_flat}); skipped — only post-shadda variants modelled"
            )
            continue

        # Dedup: an Amiri-style font often has the same substitution wired
        # into multiple subtables of the same outer lookup. The downstream
        # Mark-to-Mark record is the same either way.
        pair_key = (font.getGlyphID(shadda_name), font.getGlyphID(input_g))
        if pair_key in seen_pairs:
            continue
        seen_pairs.add(pair_key)

        records.append({
            "kind": "mark_to_mark",
            "baseMarkGlyphID": font.getGlyphID(shadda_name),
            "baseMarkName": shadda_name,
            "baseMarkCodepoint": 0x0651,
            "combiningMarkGlyphID": font.getGlyphID(input_g),
            "combiningMarkName": input_g,
            "combiningMarkCodepoint": input_cps[0],
            "baseAnchorX": 0.0,
            "baseAnchorY": 0.0,
            "combiningAdjustmentX": float(delta_x),
            "combiningAdjustmentY": float(delta_y),
            "sourceNote": (
                f"derived from GSUB Type-1 {input_g} -> {output_g} inside "
                f"Type-6 ChainedContext lookup #{sub['outer_lookup']} "
                f"(inner #{sub['inner_lookup']}, features={sub['feature_tags']}, "
                f"scripts={sub['scripts']}); "
                f"input anchor=({input_anchor['x']},{input_anchor['y']}), "
                f"variant anchor=({output_anchor['x']},{output_anchor['y']})"
            ),
        })

    output = {
        "schemaVersion": 1,
        "extractor": "arabic-mark-variants",
        "extractedAtIso": datetime.now(timezone.utc).isoformat(),
        "sourceFont": str(font_path),
        "sourceFontFamily": font["name"].getBestFamilyName() if "name" in font else "",
        "records": records,
        "warnings": warnings,
    }
    json_text = json.dumps(output, indent=2, ensure_ascii=False)

    if args.out:
        Path(args.out).write_text(json_text, encoding="utf-8")
        print(f"wrote {len(records)} records to {args.out}  ({len(warnings)} warnings)",
              file=sys.stderr)
    else:
        sys.stdout.write(json_text)
        sys.stdout.write("\n")
        sys.stdout.flush()
        print(f"emitted {len(records)} records  ({len(warnings)} warnings)",
              file=sys.stderr)

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
