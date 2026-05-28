# Findings — Arabic Text Rendering in Unity TMP

A trail of debugging notes from "why does shadda+fatha overlap on Amiri in TMP" through to "here are three fonts that render Arabic correctly in TMP without patching." Captured here because the lessons aren't obvious and the existing Unity / TMP documentation doesn't mention any of them.

If you came here to ship Arabic in a Unity project: jump to [Recommendations](#recommendations) and [Quick-start](#quick-start).

If you came here to understand *why*: read in order.

---

## TL;DR

- The "TMP preview vs bundled TMP" comparison the project was originally framed around **doesn't apply on Unity 6**. The OpenType layout features that the `com.unity.textmeshpro@3.2.0-pre.15` preview added (Ligatures, Mark-to-Base, Mark-to-Mark) have been merged into the bundled `com.unity.ugui@2.0.0` that ships with Unity 6.3. The standalone preview package is deprecated on Unity 6 and refuses to install.

- TMP's GSUB/GPOS importer does not recurse through OpenType **chained-context wrappers** (Type 5/6 in GSUB, Type 7/8 in GPOS). When a font expresses a substitution or anchor adjustment via a contextual wrapper around an inner lookup, TMP imports the wrapper but not the inner lookup. The data is in the font, the records never reach the TMP asset, and the rendering is wrong. This is the root cause of Amiri's shadda+fatha overlap.

- TMP's dynamic-atlas auto-add silently fails for some codepoints unless **`Multi Atlas Textures` is enabled** on the font asset. Without it, calls to `TMP_FontAsset.TryAddCharacters` quietly return `false` for any glyph that would trigger a new atlas page — including codepoints like `U+FC60` (the precomposed shadda+fatha ligature that RTLTMPro emits). Symptom: the character renders as `□` "tofu" and the console prints `was not found in the [font asset] or any potential fallbacks`. Fix: enable the toggle.

- RTLTMPro's text-rewriting (a string-level pass that happens before TMP renders anything) is independent of OpenType GSUB. With `Preserve Shadda` off, RTLTMPro emits the precomposed shadda+vowel codepoints `U+FC5E–U+FC63`. The font needs to have those glyphs in its cmap for that path to work.

- Of 42 candidate Arabic fonts evaluated, **three render shadda+fatha correctly in TMP without any patching**: Vazirmatn, Tajawal, and Thmanyah Sans (the latter two after a simple "rehoming" step that adds Presentation Forms-B codepoints by copying the base Arabic glyphs into them).

- The metric that correlates best with empirical correctness in TMP is the **per-harakat, per-base-letter direct Mark-to-Base coverage**. A font scoring high on this metric will render harakat correctly across a wide range of letters. Noto Naskh Arabic, despite topping the as-is leaderboard at 85/100, scored 33% on this metric — and indeed had visible misrenders on letters outside its anchored 48-of-147 set.

---

## The original hypothesis

> TMP 3.2.0-pre.15 added "new OpenType Layout features such as Ligatures, Mark-to-Base and Mark-to-Mark." If we compare it against the older bundled TMP, side by side with the same Arabic font, we should see the rendering difference and document it.

That was the plan that justified the two-project layout. It survived about an hour of contact with Unity 6.

On Unity 6 (the version we're targeting), `com.unity.ugui@2.0.0` is the only viable TMP package: it bundles all of TMP including the OpenType layout features. The standalone `com.unity.textmeshpro` package is **deprecated** on Unity 6 — Package Manager prints `com.unity.textmeshpro is deprecated: TextMeshPro functionalities are now included in the com.unity.ugui package` and refuses to install. The "preview features" we wanted to compare aren't preview anymore; they're stable.

We kept `tmp-2022/` as a control project (Unity 2022.3 LTS + the standalone preview) for verification, but the main story moved to `unity6/`: one project, bundled TMP, focus on what *actually* breaks Arabic rendering in this stack.

---

## The Amiri shadda+fatha bug

The first concrete failure was rendering of vocalised Arabic with [Amiri](https://www.amirifont.org/) — a high-quality classical Naskh font widely used for typesetting Arabic. With the test string containing `بَّ` (a base letter with both shadda and fatha), the two marks visually overlap when rendered through TMP. The same text in UI Toolkit (which uses HarfBuzz under the hood) renders correctly.

Same font binary, same input string, different rendering. The bug is in the path between font and pixel.

### The chase

1. **First hypothesis: missing Mark-to-Mark anchor between shadda and fatha.** Ruled out — Amiri has Mark-to-Mark lookups, but the `(shadda, fatha)` pair isn't covered by any of them. The static GPOS analysis (`tools/inspect-gpos-markmark.py`) was definitive on this.

2. **Second hypothesis: a precomposed ligature substitution (`<shadda, fatha> → U+FC60`) inside a GSUB chained-context wrapper that TMP doesn't recurse into.** Ruled out for the simple "ligature" form — Amiri's GSUB has Type 4 Ligature lookups, but none rewrites the `<shadda, fatha>` sequence into a single glyph. (`tools/inspect-gsub-ligature.py`)

3. **What HarfBuzz actually does.** Shaping the sequence `<U+0628 base, U+0651 shadda, U+064E fatha>` through `uharfbuzz` (the same engine UI Toolkit uses) and reading out the resulting glyph stream made the actual mechanism visible (`tools/shape-with-harfbuzz.py`):
   - HarfBuzz applies a contextual **Single Substitution** (Type 6 ChainedContext wrapping a Type 1 Single): when fatha follows shadda, fatha is replaced by a separately-designed variant glyph (`glyph01439`) drawn slightly higher on its canvas.
   - GPOS Mark-to-Base then positions both the shadda and the variant fatha with their respective anchors. The variant fatha's higher visual position keeps it clear of the shadda.

4. **The root cause.** TMP's GSUB importer enumerates feature → lookup references but **doesn't recurse through Type 5/6 ChainedContext wrappers** to register inner lookups. The variant-fatha substitution lives in such a wrapper; the inner Single substitution isn't referenced by any feature directly (only reachable through the chain). So TMP never sees the substitution, never adds the variant glyph mapping, and at render time uses the regular fatha glyph. Both marks then attach to the base letter at the same Mark-to-Base anchor and overlap.

The HarfBuzz path works because HarfBuzz executes the full OpenType spec including contextual substitution. TMP's homegrown OpenType-subset shaper doesn't.

This is a structural gap, not a one-off bug — any font that uses chained-context Single substitution to produce stacking-aware mark variants (which is the conventional approach for Arabic harakat) will misrender in TMP.

---

## Attempted fixes, in increasing order of practicality

### Patch the font asset

We built a small Editor pipeline (under `unity6/Assets/Editor/FontFeaturePatch/`) that runs a Python extractor (`tools/extract-arabic-mark-variants.py`) over the source font, identifies the contextual Single substitutions TMP missed, computes the visual offset between original and variant glyphs from their Mark-to-Base anchors, and synthesises matching Mark-to-Mark records that are then injected into the font asset's `MarkToMarkAdjustmentRecords` list. The records become real TMP-extractable data; the original mark glyphs get positioned via Mark-to-Mark instead of the missing contextual variant swap.

This works in principle but requires **per-font empirical tuning** of the y-offset value, because the anchor delta computed from the font tables (~151 for shadda+fatha on Amiri) overshoots what HarfBuzz actually applies (~51 of the same units). The variant glyph isn't just the original glyph translated; it's drawn differently. Without a full HarfBuzz-grade positioning calculation, the offset we'd need to apply is empirical.

We didn't pursue this further once it became clear that *picking a different font* avoided the problem entirely. The patcher infrastructure is still in the repo (`Arabic Study → Font Feature Patcher` menu) for anyone who needs to bypass the bug for a specific font.

### Switch fonts

The patcher made the cost-benefit clear: spend ongoing effort tuning one font per project, or spend ~30 minutes finding a font that doesn't trigger the bug. We built a scoring tool to do the latter at scale.

---

## The scoring tool

`tools/score-arabic-font.py` evaluates fonts against ten weighted criteria and emits both a per-font breakdown and a comparison table across multiple fonts:

| Criterion | What it measures | Weight |
| --- | --- | --- |
| `base_arabic_coverage` | 28 base letters + harakat present in cmap | 5 |
| `presentation_forms_b` | U+FE70–FEFC coverage (RTLTMPro's output) | 20 |
| `shadda_vowel_ligatures` | U+FC5E–FC63 precomposed forms | 10 |
| `presentation_forms_a_common` | A handful of common PF-A ligatures | 5 |
| `mark_to_base_directness` | % of Type-4 records reachable without context | 10 |
| `mark_to_mark_directness` | % of Type-6 records reachable without context | 10 |
| `shadda_vowel_mark_to_mark` | Direct MkMk for shadda + each common harakat | 15 |
| `harakat_mark_to_base_coverage` | Per-(harakat × base) direct anchor coverage | 15 |
| `arabic_shaping_features` | `init/medi/fina/isol/rlig/ccmp` under script `arab` | 5 |
| `kerning_directness` | Direct PairPos / total | 5 |

Weights bias toward "works without patching" — a font with rich contextual lookups scores lower than one with simpler, directly-referenced data, because TMP can read the latter but not the former.

The tool also computes a **post-rehome projection**: what the overall score would become if the Presentation Forms-B coverage were filled by copying base Arabic glyphs into PF codepoints. This is the operation `tools/rehome-arabic-presentation-forms.py` actually performs — adds cmap entries for missing PF-B codepoints, pointing them at whatever glyph the font's base codepoint already uses. The rendered shape is the isolated form rather than the contextually-correct shape, but that's an acceptable degradation when the alternative is rendering nothing.

For each criterion, the tool emits both summary counts ("139/141 codepoints present") and the specific missing items with their Unicode names. For the harakat coverage criterion, it groups by Arabic letter family — so the report directly shows "alef has anchors, alef-with-hamza-above doesn't" rather than a single opaque percentage.

---

## Empirical results

Evaluated 42 candidate Arabic fonts pulled from Google Fonts, aliftype (the Amiri authors), rastikerdar (Persian fonts), and a curated set of corporate/government typefaces (Dubai Font, Thmanyah Sans).

### Top 10 after the post-rehome pass

| Font | As-is | After rehome | Why interesting |
| --- | --- | --- | --- |
| Vazirmatn (variable) | 83.4 | **93.4** | 96% direct harakat coverage — highest in the set |
| Thmanyah Sans | 75.3 | **91.6** | Only font with 100% shadda+vowel Mark-to-Mark (including the kasra pair other fonts omit) |
| Mirza | 75.3 | 91.6 | Persian Naskh with comprehensive direct GPOS |
| El Messiri | 74.5 | 90.8 | Modern Arabic display, clean profile |
| Dubai (4 weights) | 74.4 | 90.7 | Government-commissioned, polished |
| Tajawal | 81.5 | **87.7** | Already has shadda ligatures, just needs PF-B rehoming |
| Noto Naskh Arabic | 85.2 | 85.3 | Tops the as-is leaderboard but only 33% harakat coverage |
| Noto Sans Arabic | 85.2 | 85.3 | Identical profile to Noto Naskh |
| Estedad | 84.9 | 85.3 | Only font in the set with 100% Arabic shaping features |
| Noto Kufi Arabic | 81.3 | 81.5 | Same Noto profile, Kufic style |

### What rendered correctly in practice

Three fonts were empirically validated as rendering shadda+fatha correctly on basic-baa in TMP, with no font-asset patching required:

- **Vazirmatn (rehomed)** — top scorer, 96% harakat coverage
- **Tajawal (rehomed)** — has shadda+vowel ligatures plus 87% harakat coverage
- **Thmanyah Sans (rehomed)** — uniquely has direct Mark-to-Mark for *all four* shadda+vowel pairs

These are now in `unity6/Assets/Fonts/` with their `-pfb-rehomed` suffix.

Notably absent from the working set: **Noto Naskh Arabic**. Despite topping the as-is leaderboard, its low direct harakat coverage (33% per the per-letter breakdown — anchors for only 48 of 147 base presentation forms) translated to visible misrenders on common letters in actual TMP scenes. This is a real empirical signal that the scoring tool's overall metric over-weighted feature presence and under-weighted per-letter coverage; the per-harakat breakdown was the more reliable indicator.

### A typography note

The "shadda + kasra" direct Mark-to-Mark pair is missing from almost every font in the set, including the top performers — only Thmanyah Sans has it. The pair isn't a coincidence: kasra sits *below* the letter while shadda sits *above*, so the two never visually conflict and don't need a Mark-to-Mark record to position correctly relative to each other. A font scoring 75% (3 of 4) on the shadda+vowel MkMk criterion is effectively at 100% for the cases that matter.

---

## TMP's runtime atlas bug

A separate gotcha discovered along the way: `TMP_FontAsset.TryAddCharacters` silently fails for certain codepoints when **Multi Atlas Textures** isn't enabled on the font asset. The failure mode is:

1. Source font has the codepoint (verified via the font's cmap).
2. Font Asset Creator's "Update Atlas Texture" inspector workflow can add the codepoint.
3. Runtime `TryAddCharacters` call (whether ours via the patcher's atlas-populate helper or TMP's own shaper at render time) returns `false` and no glyph gets added.
4. Console shows `\u<XXXX> was not found in the [font asset] or any potential fallbacks. It was replaced by Unicode character □`.

Affected codepoints in our investigation: `U+FC60` (the precomposed shadda+fatha ligature). Other Presentation Forms codepoints are presumably also affected when the font's first atlas page can't accommodate them.

**Fix: enable Multi Atlas Textures.** The toggle is on the font asset in the inspector. Once enabled, the runtime adder happily allocates new atlas pages and the missing glyphs render.

This isn't documented in any TMP material we found. It's the kind of bug that's most easily diagnosed by comparing what the Font Asset Creator's offline workflow can do (it bypasses some of the runtime adder's guards) against what runtime APIs can do.

---

## Recommendations

For shipping Arabic text in Unity 6 with TMP:

1. **Use the bundled TMP** — `com.unity.ugui@2.0.0`. Don't add `com.unity.textmeshpro` as a separate dependency on Unity 6; it's deprecated and will produce confusing warnings.

2. **Use RTLTMPro** for the text-shaping pass. The OmarWKH fork (`https://github.com/OmarWKH/RTLTMPro.git?path=/UPMPackage`) is the merge of the original maintainer's v4.0.0 with a "Preserve Shadda" option. Whether to enable Preserve Shadda depends on the font: leave it off (which causes RTLTMPro to emit `U+FC5E–FC63` precomposed forms) if your font has those codepoints; turn it on (which leaves shadda + vowel as two glyphs) if your font has direct Mark-to-Mark for those pairs.

3. **Enable Multi Atlas Textures on every Arabic font asset.** Without it, the runtime dynamic-atlas adder will silently drop characters from the Presentation Forms ranges. This single toggle prevents most runtime-tofu issues with Arabic.

4. **Pick a font with high direct harakat coverage.** Top empirically-validated picks (in alphabetical order): Tajawal, Thmanyah Sans, Vazirmatn. All work after a simple PF-B rehoming step that this repo automates. Vazirmatn has the broadest direct coverage, Thmanyah is uniquely complete on shadda+vowel pairs, Tajawal is the safest license-wise (clean SIL OFL).

5. **Score new fonts before committing to them.** `tools/score-arabic-font.py path/to/font.ttf` prints a per-criterion breakdown with the specific missing items and a recommendation for what (if anything) the font would need to be usable. Run it before adding a font to your project.

6. **If you must use a font that doesn't work directly**, the Font Feature Patcher (`Arabic Study → Font Feature Patcher` menu) can inject Mark-to-Mark records that bypass TMP's contextual-lookup blind spot. Be prepared to empirically tune the y-offset values — the anchor-delta default from the extractor is a starting point, not the right answer.

---

## Repository orientation

```
unity-arabic/
├── unity6/                  ← main Unity 6.3 project, all editor tools + winning fonts
├── tmp-2022/                ← control: Unity 2022.3 LTS + standalone TMP preview
├── tools/                   ← Python tooling (see tools/ section below)
├── font-cache/              ← gitignored, scratch dir for font candidates being evaluated
├── README.md                ← orientation, points here
└── FINDINGS.md              ← this document
```

### Tools

All scripts are in `tools/` and run from a Python venv at the repo root (`.venv/`, gitignored).

```
inspect-gpos-markmark.py        diagnostic — does the font have Mark-to-Mark for a given pair?
inspect-gsub-ligature.py        diagnostic — does the font have a ligature substitution for a sequence?
shape-with-harfbuzz.py          ground-truth shaper output (uses uharfbuzz)
extract-arabic-mark-variants.py extractor used by the Unity Font Feature Patcher
score-arabic-font.py            10-criterion scorer with per-criterion details and post-rehome projection
rehome-arabic-presentation-forms.py    adds PF-B cmap entries pointing at base Arabic glyphs
fetch-fonts.py + candidate-fonts.txt   polite downloader for the candidate font corpus
```

### Editor tools (under `Arabic Study` menu in Unity)

```
Run Full Setup               creates a font asset + test scene from Amiri-Regular.ttf
Font Table Search            chip-rendered inspector of any TMP font asset's tables, with secondary filter
RTLTMPro Debugger            before/after view of what RTLTMPro does to a string, with cross-links into Font Table Search
Font Feature Patcher         injects missing-from-import GPOS records into a font asset (per-font, persisted)
```

---

## Quick-start

If you just want to render Arabic correctly in a Unity 6.3 project:

1. `git clone` this repo, open `unity6/` in Unity 6.3.
2. Package Manager will resolve UGUI (bundled) and RTLTMPro automatically.
3. Pick one of `unity6/Assets/Fonts/Vazirmatn-Variable-pfb-rehomed.ttf`, `Tajawal-Regular-pfb-rehomed.ttf`, or `ThmanyahSans-Regular-pfb-rehomed.otf`.
4. Generate a TMP SDF font asset from it via the Font Asset Creator. **Enable Multi Atlas Textures.**
5. Use it in a `TextMeshProUGUI` with `RTLTextMeshPro` (the RTLTMPro variant) for any Arabic text. With Preserve Shadda off for fonts that have FC5E–FC63 (Vazirmatn, Thmanyah, Tajawal-rehomed all do).

If you want to evaluate a new font:

1. `pip install fonttools uharfbuzz` (one-time).
2. `python tools/score-arabic-font.py path/to/font.ttf` — read the per-criterion breakdown.
3. If `presentation_forms_b` is low but `base_arabic_coverage` is high: `python tools/rehome-arabic-presentation-forms.py path/to/font.ttf` produces a rehomed variant.
4. Re-score. If the rehomed score is in the 85+ range, the font is likely a viable TMP font.
