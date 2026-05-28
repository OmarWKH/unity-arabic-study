# Unity Arabic Font Support — TMP OpenType Study

A study of Arabic text rendering in Unity 6 with the modern TextMesh Pro pipeline (OpenType Layout: Ligatures, Mark-to-Base, Mark-to-Mark).

## Background

The original hypothesis was that the TMP `3.2.0-pre.15` preview package would fix long-standing Arabic harakat positioning problems, and that comparing it against the older bundled TMP would isolate the impact of OpenType Layout.

In practice, on **Unity 6 (`6000.3.14f1`)** that comparison no longer maps to two installable packages:

- `com.unity.ugui` 2.0.0 — the package that bundles TMP in Unity 6 — already contains the OpenType Layout features (Ligatures, Mark-to-Base, Mark-to-Mark, Glyph Adjustment tables). See the [uGUI 2.0.0 Font Assets docs](https://docs.unity3d.com/Packages/com.unity.ugui@2.0/manual/TextMeshPro/FontAssets.html).
- The standalone `com.unity.textmeshpro` package is deprecated on this Unity version and refuses to install on top — Unity prints `com.unity.textmeshpro is deprecated: TextMeshPro functionalities are now included in the com.unity.ugui package`.
- So the preview "won" by being merged in. There is no longer a meaningful package-level baseline to compare against.

The study now uses **one Unity 6.3 project** with the bundled TMP, and investigates Arabic rendering through font-asset feature configuration and the RTLTMPro pre-shaping pass instead of through package version diffing.

## Project

`unity6/` (name kept for now; will be renamed later) — Unity `6000.3.14f1`, bundled TMP via `com.unity.ugui` 2.0.0, with:

- **Amiri Regular** (SIL OFL) as the test font, committed at `Assets/Fonts/Amiri-Regular.ttf`.
- **RTLTMPro** from the OmarWKH fork (`https://github.com/OmarWKH/RTLTMPro.git?path=/UPMPackage`), which adds a `Preserve Shadda` option on top of the upstream v4.0.0 — relevant because the shadda is the diacritic most likely to expose Mark-to-Mark behaviour.
- An Editor automation menu `Arabic Study → Run Full Setup` (`Assets/Editor/ArabicTestSetup.cs`) that creates a dynamic SDF font asset from Amiri, best-effort enables the OpenType feature tags on it, and builds a side-by-side test scene with one raw `TextMeshProUGUI` and one `RTLTextMeshPro` rendering the same string.
- A font-table inspector menu `Arabic Study → Font Table Search` (`Assets/Editor/TMPFontTableSearch.cs`) for querying a TMP font asset by character / Unicode codepoint / glyph ID and reading out every Character / Glyph / Ligature / Kerning Pair / Mark-to-Base / Mark-to-Mark record involving that glyph.

## Test string

`unity6/Assets/ArabicTestString.txt` includes:

- Plain Arabic words
- Words with full harakat (fatha, kasra, damma, sukun, shadda, tanwin)
- Mark-to-mark cases (shadda + vowel)
- Bidirectional mixed Arabic / Latin / digits

## First-open workflow

1. Open `unity6/` in Unity Hub. Package Manager resolves UGUI (bundled), RTLTMPro (from the OmarWKH fork).
2. Accept the TMP Essentials import when prompted.
3. Run **menu → Arabic Study → Run Full Setup**. This generates the SDF font asset and `Assets/Scenes/ArabicTest.unity`, ready for Play mode.
4. To inspect what made it into the font asset's tables, use **menu → Arabic Study → Font Table Search**.

See `unity6/SETUP.md` for details and the OpenType-feature notes.

## License notes

- **Amiri** font — SIL Open Font License 1.1 — redistributed in this repo.
- **RTLTMPro** — MIT License — fetched via Package Manager from the OmarWKH fork.
