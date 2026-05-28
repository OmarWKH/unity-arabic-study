# unity6 — setup notes

> *Drafted by Claude (Anthropic). Verify project-specific claims before relying on them.*

The main Unity project of this repo. Unity **6000.3.14f1** with the bundled TMP that ships inside `com.unity.ugui` 2.0.0. RTLTMPro pulled from the [OmarWKH fork](https://github.com/OmarWKH/RTLTMPro). See [`../FINDINGS.md`](../FINDINGS.md) for the bigger picture; this document covers only the project-local setup.

## First open

1. Open this folder in Unity Hub with editor **6000.3.14f1**. Package Manager will resolve UGUI (bundled) and RTLTMPro automatically.
2. Accept the TMP Essentials import when prompted.

## To render Arabic text

1. Pick one of the validated fonts from `Assets/Fonts/` — `Vazirmatn-Variable-pfb-rehomed.ttf`, `Tajawal-Regular-pfb-rehomed.ttf`, or `ThmanyahSans-Regular-pfb-rehomed.otf`. These are PF-B-rehomed versions of the three fonts empirically confirmed to render shadda+harakat correctly in TMP without further patching.
2. Build a TMP SDF font asset from your chosen font (Font Asset Creator, or there are pre-built `*SDF.asset` files alongside each TTF).
3. **Enable `Multi Atlas Textures` on the font asset.** This is critical — without it TMP's runtime dynamic-atlas adder silently drops codepoints in the Presentation Forms ranges, producing `□` tofu. The finding is documented in [`../FINDINGS.md`](../FINDINGS.md).
4. Use the font in a `TextMeshProUGUI` via the `RTLTextMeshPro` component. Leave Preserve Shadda off for these three fonts (they have the precomposed shadda+vowel codepoints, so the RTLTMPro-emitted U+FC5E–FC63 forms render correctly).

The repo includes per-font test scenes (`Assets/Scenes/ArabicTest-*.unity`) used during the empirical evaluation — useful for visual comparison.

## Editor tools (`Arabic Study` menu)

- **Font Table Search** — Chip-rendered inspector of any TMP font asset's tables (Character, Glyph, Ligature, Pair Adjustment, Mark-to-Base, Mark-to-Mark). Searchable by character / codepoint / glyph ID, with a secondary filter that narrows to records referencing both glyphs.
- **RTLTMPro Debugger** — Before/after view of the codepoint stream when RTLTMPro's `FixRTL` is applied to a string. Each row has an Inspect button that deep-links into the Font Table Search window.
- **Font Feature Patcher** — *Abandoned.* Left in place for archaeology only. See the abandoned-tools section in `../FINDINGS.md` for why; the practical fix is to switch to a font that doesn't trigger the underlying TMP bug.

(`Arabic Study → Run Full Setup` is also present in the menu — it's a scratch tool from early in the investigation that builds a font asset and test scene from Amiri. Not useful as a workflow now that we know Amiri itself is one of the fonts that misrenders.)
