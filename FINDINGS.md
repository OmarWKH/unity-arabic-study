# Findings — Arabic Text Rendering in Unity TMP

> *Drafted by Claude (Anthropic) in the course of the debugging work captured in this repo. Claims describe what was actually observed; the conclusions about TMP internals are inferred from black-box behaviour and the GSUB/GPOS data of the fonts used. Verify before relying on them in production.*

What this document is: the lessons learned while debugging "why does shadda+fatha overlap on Amiri in TMP", together with the tooling we built to investigate and remediate the problem. None of the bugs documented here appear in Unity's or TMP's official material.

If you came to ship Arabic in a Unity project, jump to [Recommendations](#recommendations) and [Quick-start](#quick-start). If you came to understand *why*, read in order.

---

## Three core findings

### 1. TMP's GSUB / GPOS importer does not recurse through contextual lookup wrappers

OpenType encodes many position- and substitution-context rules through **chained-context wrappers** — Type 5/6 in GSUB, Type 7/8 in GPOS — that point at inner lookups via `SubstLookupRecord` / `PosLookupRecord`. The wrapper is referenced by a feature; the inner lookup is not. A correct OpenType implementation enumerates the wrapper's inner references and applies them.

**TMP imports the wrapper and ignores the inner reference.** Records that exist in the font but are reachable only through a context wrapper never make it into the TMP font asset. This is a structural gap, not a one-off bug: any font that expresses positioning or substitution through contextual lookups (which is most well-built Arabic fonts) will misrender in TMP.

Concrete example: Amiri's shadda+fatha rendering. The font handles the case by GSUB-substituting fatha → a lifted variant glyph via a Type 6 chained-context wrapping a Type 1 single substitution. TMP sees the Type 6 wrapper, fails to recurse, never registers the substitution, and renders the regular fatha at the same anchor as shadda — they overlap. HarfBuzz (which UI Toolkit uses) renders correctly because it executes the full OpenType spec.

The repo's diagnostic scripts (`tools/inspect-gsub-ligature.py`, `tools/inspect-gpos-markmark.py`, `tools/shape-with-harfbuzz.py`) walk through this evidence for any font on demand.

### 2. TMP's runtime dynamic-atlas adder silently drops codepoints without Multi Atlas Textures

`TMP_FontAsset.TryAddCharacters` and TMP's own render-time auto-add both return `false` (silently) for codepoints that would trigger allocation of a new atlas page, *unless* the font asset has **Multi Atlas Textures** enabled. The console shows:

> `\u<XXXX> was not found in the [font asset] or any potential fallbacks. It was replaced by Unicode character □`

Found while debugging `U+FC60` (the precomposed shadda+fatha ligature RTLTMPro emits). The font's cmap has the glyph, the Font Asset Creator's offline "Update Atlas Texture" workflow can add it, but every runtime path fails.

**Fix: enable Multi Atlas Textures on every Arabic font asset.** It's a single inspector toggle. Without it, the runtime adder silently drops characters from the Presentation Forms ranges; with it, the adder happily allocates new atlas pages.

This isn't in TMP's documentation. We hit it by comparing what the offline tool can do (it bypasses some of the runtime adder's guards) against what runtime APIs can do.

### 3. Per-letter direct Mark-to-Base coverage is a better TMP-suitability predictor than overall feature richness

When we scored 42 candidate Arabic fonts and then visually verified the top ones, Noto Naskh Arabic — which topped the as-is scoreboard at 85/100 — turned out to misrender on common Arabic letters. Reason: Noto Naskh's direct Mark-to-Base coverage is 33% across (harakat × base) pairs. The harakat are anchored on 48 of 147 base presentation forms. On letters outside the anchored 48, the marks fall back to default positioning, which TMP places incorrectly.

Fonts with high `harakat_mark_to_base_coverage` rendered correctly. Vazirmatn (96%), Tajawal (93%), Thmanyah Sans (88%) — three different design families — all worked. Noto Naskh, with 33%, did not.

The scoring tool (`tools/score-arabic-font.py`) reports this metric with per-letter-family detail, so you can see precisely *which* letters lack anchors for which harakat. A font scoring low here is a font that will visibly misrender on whatever letters are missing, no matter how good its other scores are.

---

## Tools

All Python tools live in `tools/` and assume a venv at the repo root (`.venv/`, gitignored).

```
pip install fonttools uharfbuzz
```

### Font analysis (Python)

- **`score-arabic-font.py`** — Evaluates a font (or a directory of fonts) on 10 weighted criteria covering glyph coverage and direct vs context-wrapped GPOS/GSUB. Emits per-criterion completeness (X/Y present, list of missing items by Unicode name) and a post-rehome projection. For the harakat coverage criterion, the report groups by Arabic letter family so partial coverage is visible per letter (e.g. "ALEF has anchors, ALEF WITH HAMZA ABOVE doesn't").
- **`rehome-arabic-presentation-forms.py`** — Adds cmap entries for missing Presentation Forms-B and shadda+harakat ligature codepoints, pointing them at whatever glyph the font already uses for the corresponding base Arabic codepoint. Fonts that score poorly because they ship base Arabic but no PF-B (a common SIL pattern) become usable with no glyph drawing needed.
- **`shape-with-harfbuzz.py`** — Shapes an arbitrary codepoint sequence through HarfBuzz (the engine UI Toolkit uses) and prints the resulting glyph stream with x/y offsets and advances. This is the "what *should* happen" reference any TMP rendering can be compared against.
- **`inspect-gsub-ligature.py`** — Walks a font's GSUB looking for ligature substitutions of a given glyph sequence. Reports direct Type-4 hits and context-wrapped (Type-5/6 → Type-4) hits separately, with the feature tags that reach each.
- **`inspect-gpos-markmark.py`** — Same shape, for Mark-to-Mark records of a given (base mark, combining mark) pair.
- **`fetch-fonts.py`** + `candidate-fonts.txt` — Polite rate-limited downloader for a curated candidate font corpus. Downloads land in `font-cache/` (gitignored).

Together these are enough to take an unfamiliar font, predict its TMP behaviour, identify the specific gaps it has, and (sometimes) patch them.

### Editor tools (Unity, `Arabic Study` menu)

- **Font Table Search** (`unity6/Assets/Editor/TMPFontTableSearch.cs`) — Inspector window for a TMP font asset's tables. Searchable by character / Unicode codepoint / glyph ID. Renders each glyph as a visual chip blitted from the atlas with a U+xxxx caption, so harakat and presentation forms are readable rather than abstract IDs. Foldout sections show the Character Table, Glyph Table, Ligature Records, Pair Adjustment (kerning), Mark-to-Base, and Mark-to-Mark records with counts. A secondary filter narrows hits to records referencing both the resolved glyph and the filter glyph — e.g. you query shadda, filter by fatha, and see only the Mark-to-Mark / kerning / ligature records that involve both.
- **RTLTMPro Debugger** (`unity6/Assets/Editor/RTLTMProDebugger.cs`) — Reads an `RTLTextMeshPro` component (or a raw text input + flag toggles) and runs RTLTMPro's `FixRTL` pass, showing the input and output codepoint streams in two scrolling columns. Each row has an "Inspect in Font Table Search" button that deep-links into the search window with the codepoint pre-filled — useful for tracing how RTLTMPro transforms text and which feature-table entries apply to the original vs. the post-fix codepoints.
- **Font Feature Patcher** (`unity6/Assets/Editor/FontFeaturePatch/`) — Plug-in editor window for injecting feature-table records that TMP's importer misses. Bound to a TMP font asset. Discovers extractor classes via reflection (current: `ArabicMarkVariantExtractor`, which invokes `tools/extract-arabic-mark-variants.py` to walk a font's GSUB for contextual single substitutions of harakat and synthesise corresponding Mark-to-Mark records). The window persists patches as ScriptableObjects sibling to their font asset, supports Apply / Re-apply / Revert, and exposes each record's offset values for empirical tuning.

The patcher exists as a last-resort tool for fonts that *must* be used despite the GSUB-importer gap. In practice, the empirical work concluded that **picking a font that doesn't trigger the bug is much cheaper than patching one that does**, so the patcher is rarely the right answer. The infrastructure is here if you need it.

(There's also a `Run Full Setup` menu item that's a scratch tool used during early investigation — not interesting on its own.)

---

## The Amiri case, briefly

The debugging that surfaced finding 1 took place against the Amiri font, rendering the string `بَّ` (basic baa + shadda + fatha). The two marks visually overlap in TMP; the same text renders correctly in UI Toolkit.

We confirmed Amiri does not have a Mark-to-Mark anchor record for the `(shadda, fatha)` pair, and does not have a Type-4 ligature substitution for the sequence. What it does have is a contextual *single* substitution that — when fatha follows shadda — swaps fatha for a different glyph (`glyph01439`, with no cmap entry, designed slightly higher) so that after Mark-to-Base anchoring the two marks sit clear of each other. The substitution is reachable only via a Type-6 ChainedContext wrapper around a Type-1 Single, in the `rlig` feature for the Arabic script. TMP imports the wrapper, doesn't recurse, and the substitution never fires.

`tools/shape-with-harfbuzz.py` showed the substitution's output directly. `tools/inspect-gsub-ligature.py` walked Amiri's GSUB tree and found the substitution buried under the chain. After that the cause and mechanism were clear.

---

## Empirical font evaluation

Forty-two candidate Arabic fonts pulled from Google Fonts, aliftype (the Amiri authors), rastikerdar (Persian fonts), and a small set of government/corporate typefaces (Dubai Font, Thmanyah Sans). Each scored by `tools/score-arabic-font.py`, then the top fonts visually verified in TMP.

Top 10 after post-rehome adjustment:

| Font | As-is | After PF-B rehome | Notable |
| --- | --- | --- | --- |
| Vazirmatn (variable) | 83.4 | **93.4** | 96% direct harakat coverage — highest |
| Thmanyah Sans | 75.3 | 91.6 | Only font with 100% shadda+vowel MkMk including the kasra pair |
| Mirza | 75.3 | 91.6 | Persian Naskh with comprehensive direct GPOS |
| El Messiri | 74.5 | 90.8 | Modern Arabic display |
| Dubai (Regular) | 74.4 | 90.7 | Polished, restricted commercial license |
| Tajawal | 81.5 | **87.7** | Has shadda ligatures + 87% harakat coverage |
| Noto Naskh Arabic | 85.2 | 85.3 | Tops as-is, but only 33% harakat coverage; misrenders |
| Noto Sans Arabic | 85.2 | 85.3 | Same profile as Noto Naskh |
| Estedad (variable) | 84.9 | 85.3 | Only font with 100% Arabic shaping features |
| Noto Kufi Arabic | 81.3 | 81.5 | Same Noto profile, Kufic style |

**Visually confirmed as rendering shadda+fatha correctly in TMP without font patching:** Vazirmatn-rehomed, Tajawal-rehomed, Thmanyah Sans-rehomed. All three sit in `unity6/Assets/Fonts/` with the `-pfb-rehomed` suffix.

**Notably not in the working set despite topping the as-is leaderboard:** Noto Naskh Arabic — empirical signal that the overall score over-weighted feature presence and under-weighted per-letter coverage. The per-harakat per-letter breakdown was the more reliable indicator.

A typography note worth recording: the **shadda+kasra** direct Mark-to-Mark pair is absent in almost every font in the set (only Thmanyah Sans has it). The pair isn't a coincidence — kasra sits *below* the letter while shadda sits *above*, so the two never visually conflict and don't need a Mark-to-Mark adjustment to coexist. A font scoring 75% on the shadda+vowel MkMk criterion (3 of 4) is effectively at 100% for the cases that actually matter.

---

## Recommendations

For shipping Arabic text in Unity 6 with TMP:

1. **Use the bundled TMP** — `com.unity.ugui@2.0.0`. Don't add `com.unity.textmeshpro` as a separate dependency on Unity 6.
2. **Use RTLTMPro** for shaping. The OmarWKH fork (`https://github.com/OmarWKH/RTLTMPro.git?path=/UPMPackage`) is the maintained merge of upstream v4.0.0 plus a Preserve Shadda option. Leave Preserve Shadda off if your font has the precomposed `U+FC5E–FC63` codepoints (Vazirmatn, Thmanyah, Tajawal-rehomed all do); turn it on if your font has direct Mark-to-Mark for shadda+vowel pairs.
3. **Enable Multi Atlas Textures on every Arabic font asset.** This single inspector toggle prevents most runtime tofu issues.
4. **Pick a font with high direct harakat coverage.** Top empirically-validated picks: Vazirmatn, Tajawal, Thmanyah Sans. All work after the PF-B rehoming step this repo automates.
5. **Score new fonts before committing to them.** `python tools/score-arabic-font.py path/to/font.ttf` prints a per-criterion breakdown with the specific missing items. Add it to your font-evaluation workflow.
6. **If you must use a font that doesn't render directly**, the Font Feature Patcher can inject Mark-to-Mark records to bypass TMP's contextual-lookup blind spot. Expect to empirically tune the y-offset values — the anchor-delta default from the extractor is a starting point.

---

## Quick-start

To render Arabic correctly in a Unity 6.3 project:

1. Clone this repo, open `unity6/` in Unity 6.3.
2. Package Manager resolves UGUI (bundled) + RTLTMPro automatically.
3. Pick one of `Vazirmatn-Variable-pfb-rehomed.ttf`, `Tajawal-Regular-pfb-rehomed.ttf`, or `ThmanyahSans-Regular-pfb-rehomed.otf` from `unity6/Assets/Fonts/`.
4. Generate a TMP SDF font asset from it via the Font Asset Creator. **Enable Multi Atlas Textures.**
5. Use it in `TextMeshProUGUI` via the `RTLTextMeshPro` component for any Arabic text.

To evaluate a new font:

```bash
python -m venv .venv && source .venv/Scripts/activate
pip install fonttools uharfbuzz
python tools/score-arabic-font.py path/to/font.ttf
# Optional, if PF-B coverage is low but base Arabic coverage is high:
python tools/rehome-arabic-presentation-forms.py path/to/font.ttf
python tools/score-arabic-font.py path/to/font-pfb-rehomed.ttf
```

---

## Repository layout

```
unity-arabic/
├── unity6/         ← main Unity 6.3 project, editor tools, validated fonts
├── tmp-2022/       ← control project (Unity 2022.3 LTS + standalone TMP preview)
├── tools/          ← Python font-analysis tooling
├── font-cache/     ← gitignored scratch dir for candidates
├── README.md       ← orientation
└── FINDINGS.md     ← this document
```

The `tmp-2022/` control project verified that the OpenType features in Unity 6's bundled TMP match what the standalone `com.unity.textmeshpro@3.2.0-pre.15` preview provided pre-merge. It's parked; the active work happens in `unity6/`.
