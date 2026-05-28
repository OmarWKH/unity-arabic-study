#!/usr/bin/env python3
"""Score one or more fonts on their suitability for Arabic text in TMP.

Each font is evaluated against a fixed set of criteria. Criteria are grouped
into three categories: glyph coverage, GPOS quality (weighted by TMP-
extractability — direct records score higher than contextually-wrapped
ones because TMP can't recurse through GSUB/GPOS Type-5/6/8 wrappers), and
GSUB shaping features.

Beyond a single overall score, the tool emits per-criterion completeness
data — exactly which codepoints / glyph pairs are present, which are
missing, with full Unicode names — so you can see at a glance what each
font would need to be usable.

Usage:
  python tools/score-arabic-font.py path/to/font.ttf
  python tools/score-arabic-font.py path/to/fonts/
  python tools/score-arabic-font.py font1.ttf font2.ttf font3.ttf

Output modes:
  default       JSON on stdout + markdown table + detailed per-font report on stderr
  --brief       JSON on stdout + markdown table on stderr (no per-font detail)
  --json-only   JSON on stdout, no stderr output

Install:
  pip install fonttools
"""

from __future__ import annotations
import argparse
import json
import sys
import unicodedata
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Iterable, Optional

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


# ================================================== constants & weights ====

# 28 base letters of the Arabic alphabet, plus hamza variants.
ARABIC_BASE_LETTERS = list(range(0x0621, 0x064B))  # U+0621..U+064A

# Harakat / diacritical marks.
HARAKAT = [
    0x064B, 0x064C, 0x064D,  # fathatan, dammatan, kasratan
    0x064E, 0x064F, 0x0650,  # fatha, damma, kasra
    0x0651,                  # shadda
    0x0652,                  # sukun
    0x0670,                  # superscript alef
]

# The six precomposed shadda+harakat ligature codepoints.
SHADDA_VOWEL_LIGATURES = list(range(0xFC5E, 0xFC64))

# Arabic Presentation Forms-B (FE70..FEFC).
PRESENTATION_FORMS_B = list(range(0xFE70, 0xFEFD))

# A short curated list of commonly-used Presentation Forms-A codepoints.
PRESENTATION_FORMS_A_COMMON = [
    0xFB50, 0xFB51,                 # ALEF WASLA initial
    0xFB52, 0xFB53,                 # BEH variant (BEEH)
    0xFBAA, 0xFBAB, 0xFBAC, 0xFBAD, # KEHEH 4-form
    0xFBFC, 0xFBFD, 0xFBFE, 0xFBFF, # FARSI YEH 4-form
]

# Required GSUB features for proper Arabic shaping.
ARABIC_SHAPING_FEATURES = ["init", "medi", "fina", "isol", "rlig", "ccmp"]

# Bias: "works without patching" — directness matters most.
WEIGHTS = {
    "base_arabic_coverage":           5.0,
    "presentation_forms_b":          20.0,
    "shadda_vowel_ligatures":        10.0,
    "presentation_forms_a_common":    5.0,
    "mark_to_base_directness":       10.0,
    "mark_to_mark_directness":       10.0,
    "shadda_vowel_mark_to_mark":     15.0,
    "harakat_mark_to_base_coverage": 15.0,
    "arabic_shaping_features":        5.0,
    "kerning_directness":             5.0,
}


# ============================================================ data model ===

@dataclass
class CriterionResult:
    name: str
    description: str
    score: float           # 0..1
    weight: float
    weighted_score: float  # score * weight
    summary: str = ""      # one-line "x/y present" headline
    details: dict = field(default_factory=dict)
    notes: list = field(default_factory=list)
    # report_lines is human-readable, multi-line, hand-formatted per criterion
    report_lines: list = field(default_factory=list)


@dataclass
class FontScore:
    font_path: str
    font_family: str
    font_subfamily: str
    overall_score: float   # 0..100  (as-is, no patching)
    post_rehome_score: float   # 0..100  (assume PF-B gap filled by copying base Arabic glyphs)
    verdict: str
    post_rehome_verdict: str
    criteria: list = field(default_factory=list)


# ============================================================= helpers =====

def cp_info(cp: int) -> dict:
    """Structured info about a single codepoint."""
    try:
        ch = chr(cp)
        name = unicodedata.name(ch, "")
    except (ValueError, OverflowError):
        ch = "?"; name = ""
    return {"codepoint": cp, "char": ch, "name": name, "hex": f"U+{cp:04X}"}


def cp_label(cp: int) -> str:
    """One-liner string for a codepoint, suitable for a list entry."""
    info = cp_info(cp)
    name = info["name"] or "(unnamed)"
    return f"{info['hex']}  '{info['char']}'  {name}"


def base_letter_of(cp: int) -> Optional[int]:
    """For a codepoint that's an Arabic presentation form (FB50-FDFF or
    FE70-FEFC), return the most relevant Arabic-block codepoint it
    decomposes to. For a base codepoint in the Arabic block, return it
    as-is. Returns None for everything else.

    Wrinkle: isolated-form harakat (e.g. U+FE76 ARABIC FATHA ISOLATED
    FORM) decompose to "<isolated> 0020 064E" — space + harakat — and
    likewise the shadda+vowel ligatures lead with U+0020. We want to
    skip the leading SPACE and pick the first Arabic-range codepoint
    that follows."""
    if 0x0600 <= cp <= 0x06FF:
        return cp
    if not (0xFB50 <= cp <= 0xFDFF or 0xFE70 <= cp <= 0xFEFC):
        return None
    try:
        decomp = unicodedata.decomposition(chr(cp))
    except (ValueError, OverflowError):
        return None
    if not decomp:
        return None
    parts = decomp.split()
    if parts and parts[0].startswith("<"):
        parts = parts[1:]
    if not parts:
        return None
    # Walk the decomposition components; prefer the first one in the
    # Arabic base block. If none qualify, fall back to the first part.
    arabic_block = None
    first = None
    for token in parts:
        try:
            v = int(token, 16)
        except ValueError:
            continue
        if first is None:
            first = v
        if 0x0600 <= v <= 0x06FF and arabic_block is None:
            arabic_block = v
    return arabic_block if arabic_block is not None else first


def form_label_of(cp: int) -> str:
    """The shape form encoded by a presentation form codepoint: isolated,
    final, initial, medial, or '' for unrecognised."""
    try:
        decomp = unicodedata.decomposition(chr(cp))
    except (ValueError, OverflowError):
        return ""
    if "<isolated>" in decomp: return "isolated"
    if "<final>"    in decomp: return "final"
    if "<initial>"  in decomp: return "initial"
    if "<medial>"   in decomp: return "medial"
    return ""


def unwrap(sub):
    """Unwrap an OpenType Extension subtable."""
    if getattr(sub, "ExtensionLookupType", None) is not None:
        return sub.ExtensionLookupType, sub.ExtSubTable
    return sub.LookupType, sub


def context_inner_lookups(real_sub) -> list:
    """Inner LookupListIndex values for a Type-5/6/7/8 context subtable."""
    if getattr(real_sub, "Format", None) != 3:
        return []
    recs = (
        getattr(real_sub, "SubstLookupRecord", None)
        or getattr(real_sub, "PosLookupRecord", None)
        or []
    )
    return [r.LookupListIndex for r in recs]


def classify_lookup_counts(table, direct_type: int, context_types: Iterable[int]):
    """Count direct and context-wrapped lookups of a given type."""
    n_direct = n_context = 0
    if table is None:
        return n_direct, n_context
    lookups = table.table.LookupList.Lookup
    context_set = set(context_types)
    direct_indexes = set()
    for i, lookup in enumerate(lookups):
        for sub in lookup.SubTable:
            t, _ = unwrap(sub)
            if t == direct_type:
                direct_indexes.add(i); break
    for i, lookup in enumerate(lookups):
        for sub in lookup.SubTable:
            t, real = unwrap(sub)
            if t in context_set:
                for inner in context_inner_lookups(real):
                    if inner in direct_indexes:
                        n_context += 1; break
                else: continue
                break
    n_direct = len(direct_indexes)
    return n_direct, n_context


def find_markbase_records(gpos):
    """Return [(mark_glyph_name, base_glyph_name, in_context_only)]."""
    out = []
    if gpos is None: return out
    lookups = gpos.table.LookupList.Lookup
    direct_indexes = set()
    for i, lookup in enumerate(lookups):
        for sub in lookup.SubTable:
            t, _ = unwrap(sub)
            if t == 4: direct_indexes.add(i)
    context_indexes = set()
    for i, lookup in enumerate(lookups):
        for sub in lookup.SubTable:
            t, real = unwrap(sub)
            if t in (7, 8):
                for inner in context_inner_lookups(real):
                    if inner in direct_indexes:
                        context_indexes.add(inner)
    purely_context = context_indexes - direct_indexes
    for i, lookup in enumerate(lookups):
        for sub in lookup.SubTable:
            t, real = unwrap(sub)
            if t != 4: continue
            in_ctx = i in purely_context
            for m in real.MarkCoverage.glyphs:
                for b in real.BaseCoverage.glyphs:
                    out.append((m, b, in_ctx))
    return out


def find_markmark_records(gpos):
    """Return [(base_mark_glyph, comb_mark_glyph, in_context_only)]."""
    out = []
    if gpos is None: return out
    lookups = gpos.table.LookupList.Lookup
    direct_indexes = set()
    for i, lookup in enumerate(lookups):
        for sub in lookup.SubTable:
            t, _ = unwrap(sub)
            if t == 6: direct_indexes.add(i)
    context_indexes = set()
    for i, lookup in enumerate(lookups):
        for sub in lookup.SubTable:
            t, real = unwrap(sub)
            if t in (7, 8):
                for inner in context_inner_lookups(real):
                    if inner in direct_indexes:
                        context_indexes.add(inner)
    purely_context = context_indexes - direct_indexes
    for i, lookup in enumerate(lookups):
        for sub in lookup.SubTable:
            t, real = unwrap(sub)
            if t != 6: continue
            in_ctx = i in purely_context
            for m1 in real.Mark1Coverage.glyphs:
                for m2 in real.Mark2Coverage.glyphs:
                    out.append((m1, m2, in_ctx))
    return out


# ============================================================= criteria ====

def _coverage_criterion(name, description, weight, expected_cps, cmap,
                        missing_advice=""):
    """Generic codepoint-coverage criterion factory."""
    present_cps = [cp for cp in expected_cps if cp in cmap]
    missing_cps = [cp for cp in expected_cps if cp not in cmap]
    score = len(present_cps) / max(1, len(expected_cps))
    cr = CriterionResult(
        name=name,
        description=description,
        score=score,
        weight=weight,
        weighted_score=score * weight,
        summary=f"{len(present_cps)}/{len(expected_cps)} codepoints present",
        details={
            "expected_count": len(expected_cps),
            "present_count": len(present_cps),
            "missing_count": len(missing_cps),
            "missing": [cp_info(cp) for cp in missing_cps],
            "present": [cp_info(cp) for cp in present_cps],
        },
    )
    cr.report_lines.append(cr.summary)
    if missing_cps:
        cr.report_lines.append(f"missing {len(missing_cps)}:")
        for cp in missing_cps:
            cr.report_lines.append(f"  {cp_label(cp)}")
        if missing_advice:
            cr.report_lines.append(missing_advice)
    return cr


def cr_base_arabic_coverage(font, cmap, _ctx) -> CriterionResult:
    return _coverage_criterion(
        "base_arabic_coverage",
        "28 base Arabic letters + harakat (U+0600 block, baseline)",
        WEIGHTS["base_arabic_coverage"],
        ARABIC_BASE_LETTERS + HARAKAT,
        cmap,
        missing_advice="font is missing baseline Arabic codepoints — not viable for Arabic text",
    )


def cr_presentation_forms_b(font, cmap, _ctx) -> CriterionResult:
    return _coverage_criterion(
        "presentation_forms_b",
        "Arabic Presentation Forms-B (U+FE70-FEFC) — what RTLTMPro emits",
        WEIGHTS["presentation_forms_b"],
        PRESENTATION_FORMS_B,
        cmap,
        missing_advice="RTLTMPro emits PF-B forms; missing ones will render as tofu unless rehomed from base Arabic block",
    )


def cr_shadda_vowel_ligatures(font, cmap, _ctx) -> CriterionResult:
    return _coverage_criterion(
        "shadda_vowel_ligatures",
        "Precomposed shadda+harakat ligatures (U+FC5E-FC63) — RTLTMPro shadda output",
        WEIGHTS["shadda_vowel_ligatures"],
        SHADDA_VOWEL_LIGATURES,
        cmap,
        missing_advice="RTLTMPro with PreserveShadda=off emits these; without them shadda+vowel will overlap",
    )


def cr_presentation_forms_a_common(font, cmap, _ctx) -> CriterionResult:
    return _coverage_criterion(
        "presentation_forms_a_common",
        "Common Arabic Presentation Forms-A ligatures (auxiliary)",
        WEIGHTS["presentation_forms_a_common"],
        PRESENTATION_FORMS_A_COMMON,
        cmap,
    )


def cr_mark_to_base_directness(font, cmap, ctx) -> CriterionResult:
    triples = ctx["markbase_triples"]
    direct = [t for t in triples if not t[2]]
    context = [t for t in triples if t[2]]
    total = len(triples)
    score = (len(direct) / total) if total > 0 else 0.0
    cr = CriterionResult(
        name="mark_to_base_directness",
        description="Direct Mark-to-Base records / total (incl. contextually-wrapped)",
        score=score,
        weight=WEIGHTS["mark_to_base_directness"],
        weighted_score=score * WEIGHTS["mark_to_base_directness"],
        summary=(f"{len(direct)}/{total} Mark-to-Base pairs are TMP-extractable"
                 if total > 0 else "no Mark-to-Base records"),
        details={
            "total": total,
            "direct": len(direct),
            "context_only": len(context),
            "context_only_examples": [
                {"mark_glyph": m, "base_glyph": b}
                for (m, b, _) in context[:12]
            ],
        },
    )
    cr.report_lines.append(cr.summary)
    if context:
        cr.report_lines.append(
            f"{len(context)} pair(s) live inside Type-7/8 context wrappers TMP can't recurse through"
        )
        if len(context) <= 12:
            for m, b, _ in context:
                cr.report_lines.append(f"  context-only: {m} → {b}")
        else:
            cr.report_lines.append(f"  (first 12 of {len(context)}:)")
            for m, b, _ in context[:12]:
                cr.report_lines.append(f"    context-only: {m} → {b}")
    if total == 0:
        cr.report_lines.append("font has no Mark-to-Base lookups — no mark positioning at all")
    return cr


def cr_mark_to_mark_directness(font, cmap, ctx) -> CriterionResult:
    triples = ctx["markmark_triples"]
    direct = [t for t in triples if not t[2]]
    context = [t for t in triples if t[2]]
    total = len(triples)
    score = (len(direct) / total) if total > 0 else 0.0
    cr = CriterionResult(
        name="mark_to_mark_directness",
        description="Direct Mark-to-Mark records / total (incl. contextually-wrapped)",
        score=score,
        weight=WEIGHTS["mark_to_mark_directness"],
        weighted_score=score * WEIGHTS["mark_to_mark_directness"],
        summary=(f"{len(direct)}/{total} Mark-to-Mark pairs are TMP-extractable"
                 if total > 0 else "no Mark-to-Mark records"),
        details={
            "total": total,
            "direct": len(direct),
            "context_only": len(context),
            "context_only_examples": [
                {"base_mark": m1, "combining_mark": m2}
                for (m1, m2, _) in context[:12]
            ],
        },
    )
    cr.report_lines.append(cr.summary)
    if context:
        cr.report_lines.append(
            f"{len(context)} pair(s) live inside context wrappers TMP can't recurse through"
        )
    if total == 0:
        cr.report_lines.append("font has no Mark-to-Mark lookups — harakat stacking unsupported")
    return cr


def cr_shadda_vowel_mark_to_mark(font, cmap, ctx) -> CriterionResult:
    """The decisive check: direct Mark-to-Mark for each shadda+vowel pair."""
    shadda_g = cmap.get(0x0651)
    targets = [
        ("fatha", 0x064E),
        ("kasra", 0x0650),
        ("damma", 0x064F),
        ("sukun", 0x0652),
    ]
    if shadda_g is None:
        cr = CriterionResult(
            name="shadda_vowel_mark_to_mark",
            description="Direct Mark-to-Mark for shadda + each common harakat",
            score=0.0,
            weight=WEIGHTS["shadda_vowel_mark_to_mark"],
            weighted_score=0.0,
            summary="shadda (U+0651) not in font — cannot evaluate",
            details={"shadda_in_cmap": False},
        )
        cr.report_lines.append(cr.summary)
        return cr

    direct_pairs = {(m1, m2) for m1, m2, c in ctx["markmark_triples"] if not c}
    expected = []
    hits = []
    misses = []
    for label, cp in targets:
        vowel_g = cmap.get(cp)
        info = {"label": label, "vowel_codepoint": cp_info(cp), "in_font": vowel_g is not None}
        if vowel_g is None:
            info["covered"] = False
            misses.append(info); expected.append(info); continue
        covered = (shadda_g, vowel_g) in direct_pairs
        info["covered"] = covered
        expected.append(info)
        (hits if covered else misses).append(info)

    in_font_total = sum(1 for e in expected if e["in_font"])
    score = (len(hits) / in_font_total) if in_font_total > 0 else 0.0
    cr = CriterionResult(
        name="shadda_vowel_mark_to_mark",
        description="Direct Mark-to-Mark for shadda + each common harakat (TMP-extractable)",
        score=score,
        weight=WEIGHTS["shadda_vowel_mark_to_mark"],
        weighted_score=score * WEIGHTS["shadda_vowel_mark_to_mark"],
        summary=f"{len(hits)}/{in_font_total} shadda+vowel pairs have direct Mark-to-Mark",
        details={"expected": expected, "covered": hits, "missing": misses},
    )
    cr.report_lines.append(cr.summary)
    for m in misses:
        if m["in_font"]:
            cr.report_lines.append(
                f"  missing: shadda + {m['label']}  ({m['vowel_codepoint']['hex']})"
            )
        else:
            cr.report_lines.append(
                f"  unevaluable: {m['label']} ({m['vowel_codepoint']['hex']}) not in font"
            )
    if misses:
        cr.report_lines.append("→ these pairs will overlap unless covered by precomposed ligatures (U+FC5E-FC63)")
    return cr


def cr_harakat_mark_to_base_coverage(font, cmap, ctx) -> CriterionResult:
    """Per-harakat coverage across all Arabic bases. For each harakat,
    count how many distinct base glyphs it has a direct Mark-to-Base
    record with. The base glyphs are pulled from both the base Arabic
    block and the Presentation Forms-B range — what's actually rendered
    after RTLTMPro / TMP shaping.

    Additionally groups coverage per BASE LETTER (e.g., all 4 forms of
    BEH) so the report can show "alef has anchors but alef-with-hamza
    doesn't" rather than just a raw percentage."""
    triples = ctx["markbase_triples"]
    harakat_cp_to_glyph = {cp: cmap[cp] for cp in HARAKAT if cp in cmap}

    # Build the base-glyph universe. Per base glyph, remember the
    # codepoint(s) it maps from in cmap so we can group by letter family.
    glyph_to_cps = {}  # base_glyph_name -> [codepoints in cmap]
    for cp in list(PRESENTATION_FORMS_B) + list(ARABIC_BASE_LETTERS):
        g = cmap.get(cp)
        if g: glyph_to_cps.setdefault(g, []).append(cp)
    base_glyphs = set(glyph_to_cps.keys())

    if not harakat_cp_to_glyph or not base_glyphs:
        cr = CriterionResult(
            name="harakat_mark_to_base_coverage",
            description="Direct Mark-to-Base coverage of each harakat over Arabic bases",
            score=0.0,
            weight=WEIGHTS["harakat_mark_to_base_coverage"],
            weighted_score=0.0,
            summary="no harakat or no base glyphs to evaluate",
            details={},
        )
        cr.report_lines.append(cr.summary)
        return cr

    direct_by_mark = {}
    for m, b, ctx_only in triples:
        if ctx_only: continue
        direct_by_mark.setdefault(m, set()).add(b)

    # For each base glyph, figure out its letter family (one codepoint in
    # the Arabic base block). Glyphs whose family can't be resolved get
    # bucketed under None.
    glyph_to_family = {}
    for g, cps in glyph_to_cps.items():
        families = []
        for cp in cps:
            bl = base_letter_of(cp)
            if bl is not None: families.append(bl)
        glyph_to_family[g] = families[0] if families else None

    per_harakat = []
    sum_coverage = 0.0
    for cp, hg in sorted(harakat_cp_to_glyph.items()):
        covered_bases = direct_by_mark.get(hg, set()) & base_glyphs
        missing_bases = base_glyphs - covered_bases
        cov = len(covered_bases) / max(1, len(base_glyphs))
        sum_coverage += cov

        # Group by family (= base Arabic letter codepoint).
        by_family = {}  # base_letter_cp -> {"covered": [glyphs], "missing": [glyphs]}
        for g in base_glyphs:
            fam = glyph_to_family.get(g)
            by_family.setdefault(fam, {"covered": [], "missing": []})
            bucket = "covered" if g in covered_bases else "missing"
            by_family[fam][bucket].append(g)

        # Serialisable family records.
        family_records = []
        for fam, buckets in by_family.items():
            total = len(buckets["covered"]) + len(buckets["missing"])
            family_records.append({
                "letter_codepoint": cp_info(fam) if fam is not None else None,
                "covered_glyphs": sorted(buckets["covered"]),
                "missing_glyphs": sorted(buckets["missing"]),
                "covered_count": len(buckets["covered"]),
                "total_count": total,
            })
        # Sort by family codepoint for stable output.
        family_records.sort(key=lambda f: (f["letter_codepoint"]["codepoint"]
                                           if f["letter_codepoint"] else 0xFFFF))

        per_harakat.append({
            "harakat": cp_info(cp),
            "harakat_glyph": hg,
            "bases_covered": len(covered_bases),
            "bases_total": len(base_glyphs),
            "ratio": cov,
            "per_letter_family": family_records,
        })
    score = sum_coverage / len(per_harakat)

    cr = CriterionResult(
        name="harakat_mark_to_base_coverage",
        description="Direct Mark-to-Base coverage of each harakat over Arabic bases",
        score=score,
        weight=WEIGHTS["harakat_mark_to_base_coverage"],
        weighted_score=score * WEIGHTS["harakat_mark_to_base_coverage"],
        summary=f"avg {score*100:.0f}% direct base coverage per harakat (across {len(base_glyphs)} base glyphs)",
        details={
            "base_glyph_count": len(base_glyphs),
            "harakat_count": len(per_harakat),
            "per_harakat": per_harakat,
        },
    )
    cr.report_lines.append(cr.summary)
    for h in per_harakat:
        bar = h["ratio"]
        marker = "✓" if bar >= 0.8 else "·" if bar >= 0.4 else "✗"
        cr.report_lines.append(
            f"  {marker} {h['harakat']['hex']} {h['harakat']['name']:<28s}  "
            f"{h['bases_covered']}/{h['bases_total']}  ({bar*100:.0f}%)"
        )
        # Show letter-family rows: only the partially / unsupported ones
        # since fully-covered letters are uninteresting noise.
        problems = [f for f in h["per_letter_family"]
                    if f["covered_count"] < f["total_count"]]
        if problems:
            cr.report_lines.append(f"      letters with partial/no coverage:")
            for f in problems:
                lc = f["letter_codepoint"]
                if lc is None:
                    label = "(non-Unicode glyphs)"
                else:
                    label = f"{lc['hex']} {lc['name']}"
                cr.report_lines.append(
                    f"        {f['covered_count']}/{f['total_count']}  {label}"
                )
    return cr


def cr_arabic_shaping_features(font, cmap, _ctx) -> CriterionResult:
    gsub = font.get("GSUB")
    expected = set(ARABIC_SHAPING_FEATURES)
    present_tags = set()
    if gsub is not None:
        feature_list = gsub.table.FeatureList
        for sr in gsub.table.ScriptList.ScriptRecord:
            if sr.ScriptTag != "arab":
                continue
            lang_systems = []
            if sr.Script.DefaultLangSys is not None:
                lang_systems.append(sr.Script.DefaultLangSys)
            for lr in sr.Script.LangSysRecord:
                lang_systems.append(lr.LangSys)
            for ls in lang_systems:
                for fi in ls.FeatureIndex:
                    fr = feature_list.FeatureRecord[fi]
                    present_tags.add(fr.FeatureTag)
    present_expected = expected & present_tags
    missing = expected - present_tags
    score = len(present_expected) / max(1, len(expected))
    cr = CriterionResult(
        name="arabic_shaping_features",
        description="GSUB features under script 'arab': init/medi/fina/isol/rlig/ccmp",
        score=score,
        weight=WEIGHTS["arabic_shaping_features"],
        weighted_score=score * WEIGHTS["arabic_shaping_features"],
        summary=f"{len(present_expected)}/{len(expected)} required features present for script arab",
        details={
            "expected": sorted(expected),
            "present_expected": sorted(present_expected),
            "missing": sorted(missing),
            "other_features_present": sorted(present_tags - expected),
        },
    )
    cr.report_lines.append(cr.summary)
    if missing:
        cr.report_lines.append(f"missing required features: {sorted(missing)}")
    if gsub is None:
        cr.report_lines.append("font has no GSUB table — Arabic shaping not possible")
    return cr


def cr_kerning_directness(font, cmap, _ctx) -> CriterionResult:
    gpos = font.get("GPOS")
    direct, context = classify_lookup_counts(gpos, direct_type=2, context_types=(7, 8))
    total = direct + context
    score = (direct / total) if total > 0 else 0.0
    cr = CriterionResult(
        name="kerning_directness",
        description="Direct PairPos (kerning) lookups / total",
        score=score,
        weight=WEIGHTS["kerning_directness"],
        weighted_score=score * WEIGHTS["kerning_directness"],
        summary=(f"{direct}/{total} PairPos kerning lookups direct"
                 if total > 0 else "no PairPos kerning lookups"),
        details={"total": total, "direct": direct, "context_only": context},
    )
    cr.report_lines.append(cr.summary)
    if context > 0:
        cr.report_lines.append(f"{context} kerning lookup(s) inside context wrappers")
    return cr


CRITERIA = [
    cr_base_arabic_coverage,
    cr_presentation_forms_b,
    cr_shadda_vowel_ligatures,
    cr_presentation_forms_a_common,
    cr_mark_to_base_directness,
    cr_mark_to_mark_directness,
    cr_shadda_vowel_mark_to_mark,
    cr_harakat_mark_to_base_coverage,
    cr_arabic_shaping_features,
    cr_kerning_directness,
]


# ============================================================ aggregator ===

def verdict_for(score: float) -> str:
    if score >= 80: return "great"
    if score >= 60: return "usable"
    if score >= 40: return "problematic"
    return "poor"


def score_font(font_path: Path) -> FontScore:
    font = TTFont(str(font_path))
    cmap = font.getBestCmap()
    family = font["name"].getBestFamilyName() if "name" in font else font_path.stem
    subfamily = font["name"].getBestSubFamilyName() if "name" in font else ""
    gpos = font.get("GPOS")
    ctx = {
        "markbase_triples": find_markbase_records(gpos),
        "markmark_triples": find_markmark_records(gpos),
    }
    results = [c(font, cmap, ctx) for c in CRITERIA]
    overall = sum(r.weighted_score for r in results)
    post_rehome = compute_post_rehome_overall(results)
    return FontScore(
        font_path=str(font_path),
        font_family=family,
        font_subfamily=subfamily,
        overall_score=round(overall, 2),
        post_rehome_score=round(post_rehome, 2),
        verdict=verdict_for(overall),
        post_rehome_verdict=verdict_for(post_rehome),
        criteria=results,
    )


def compute_post_rehome_overall(criteria) -> float:
    """What would the overall score be if we filled the Presentation
    Forms-B gap by copying base Arabic glyphs into the corresponding
    PF-B codepoints? Conservatively: PF-B's effective score is bounded by
    base_arabic_coverage — we can only rehome what the font actually has.

    Note this only models PF-B rehoming. The shadda+vowel ligatures
    (U+FC5E-FC63) could in principle also be synthesised but the visual
    result wouldn't match what a designed composed glyph produces, so we
    don't claim credit for that here.

    Other criteria are unchanged — GPOS records can't be fabricated by
    rehoming."""
    base_score = next((c.score for c in criteria if c.name == "base_arabic_coverage"), 0.0)
    total = 0.0
    for c in criteria:
        eff = c.score
        if c.name == "presentation_forms_b":
            eff = max(c.score, base_score)
        total += eff * c.weight
    return total


def discover_fonts(paths):
    out = []
    for p in paths:
        path = Path(p)
        if path.is_file():
            out.append(path)
        elif path.is_dir():
            for ext in ("*.ttf", "*.otf"):
                out.extend(sorted(path.rglob(ext)))
        else:
            print(f"skipping (not found): {p}", file=sys.stderr)
    return out


# ============================================================ formatters ===

def render_markdown_table(scores: list) -> str:
    headers = ["Font", "Verdict", "Overall", "PostRehome"]
    short_cols = [
        ("presentation_forms_b",          "PF-B"),
        ("shadda_vowel_ligatures",        "ShdLig"),
        ("mark_to_base_directness",       "MkB-direct"),
        ("mark_to_mark_directness",       "MkMk-direct"),
        ("shadda_vowel_mark_to_mark",     "ShdMkMk"),
        ("harakat_mark_to_base_coverage", "HarakatCov"),
        ("arabic_shaping_features",       "ShapFeat"),
    ]
    for _, n in short_cols: headers.append(n)
    rows = ["| " + " | ".join(headers) + " |",
            "| " + " | ".join("---" for _ in headers) + " |"]
    for s in scores:
        cells = [Path(s.font_path).stem, s.verdict,
                 f"{s.overall_score:.1f}", f"{s.post_rehome_score:.1f}"]
        crit_by = {c.name: c for c in s.criteria}
        for key, _ in short_cols:
            c = crit_by.get(key)
            cells.append("?" if c is None else f"{c.score*100:.0f}%")
        rows.append("| " + " | ".join(cells) + " |")
    return "\n".join(rows)


def render_per_font_report(s: FontScore) -> str:
    out = []
    out.append("")
    out.append("═" * 72)
    out.append(f"{s.font_family}    ({Path(s.font_path).name})")
    out.append(f"  overall      {s.overall_score:.1f}/100   ({s.verdict})")
    out.append(f"  post-rehome  {s.post_rehome_score:.1f}/100   ({s.post_rehome_verdict})  "
               "[hypothetical: PF-B filled by copying base Arabic glyphs]")
    out.append("─" * 72)
    for c in s.criteria:
        pct = c.score * 100
        sym = "✓" if c.score >= 0.9 else "·" if c.score >= 0.5 else "✗"
        out.append(
            f"  {sym}  {c.name:<35s}  "
            f"{c.weighted_score:5.1f} / {c.weight:5.1f}   ({pct:5.1f}%)"
        )
        out.append(f"        {c.description}")
        for line in c.report_lines:
            out.append(f"        {line}")
        out.append("")
    return "\n".join(out)


# =============================================================== main =====

def main(argv):
    p = argparse.ArgumentParser(description="Score Arabic font suitability for TMP.")
    p.add_argument("paths", nargs="+", help="font files or directories")
    p.add_argument("--brief", action="store_true",
                   help="suppress per-font detail; show only the comparison table")
    p.add_argument("--json-only", action="store_true",
                   help="suppress all stderr output (just emit JSON)")
    args = p.parse_args(argv[1:])

    paths = discover_fonts(args.paths)
    if not paths:
        print("no fonts found", file=sys.stderr); return 1

    scores = []
    for fp in paths:
        try:
            scores.append(score_font(fp))
        except Exception as ex:
            print(f"error on {fp}: {ex.__class__.__name__}: {ex}", file=sys.stderr)
    if not scores: return 1

    scores.sort(key=lambda s: s.overall_score, reverse=True)

    # JSON to stdout. Single object if one font, array otherwise.
    payload = asdict(scores[0]) if len(scores) == 1 else [asdict(s) for s in scores]
    sys.stdout.write(json.dumps(payload, indent=2, ensure_ascii=False))
    sys.stdout.write("\n")

    if args.json_only: return 0

    # Detailed per-font reports first (most useful), then the comparison
    # table at the end so it's the last thing on screen.
    if not args.brief:
        for s in scores:
            sys.stderr.write(render_per_font_report(s) + "\n")

    if len(scores) > 1:
        sys.stderr.write("\n" + render_markdown_table(scores) + "\n")
        sys.stderr.write(
            f"\nscored {len(scores)} fonts; "
            f"best: {scores[0].font_family} ({scores[0].overall_score:.1f}); "
            f"worst: {scores[-1].font_family} ({scores[-1].overall_score:.1f})\n"
        )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
