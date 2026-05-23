# Setup — TMP 3.2.0-pre.15 (preview)

Project pinned to Unity **6000.3.14f1** with `com.unity.ugui` **3.2.0-pre.15**. This preview adds OpenType Layout support (Ligatures, Mark-to-Base, Mark-to-Mark) — the features that should make harakat positioning correct for the first time in TMP.

## Automated setup

1. Open this folder in Unity Hub. Package Manager resolves `com.unity.ugui` 3.2.0-pre.15 and RTLTMPro — first resolve can take a few minutes.
2. If a TMP Essentials prompt appears, import it.
3. Once the project is loaded with no compile errors, run **menu → Arabic Study → Run Full Setup**.

[`Assets/Editor/ArabicTestSetup.cs`](Assets/Editor/ArabicTestSetup.cs):

- Creates `Assets/Fonts/Amiri-Regular SDF.asset` (dynamic SDF atlas, 2048², SDFAA, padding 9, multi-atlas enabled).
- **Best-effort enables the OpenType layout features** on the font asset: `liga`, `rlig`, `mark`, `mkmk`, `init`, `medi`, `fina`, `isol`, `ccmp`. If the API has shifted in this preview revision, it logs a clear warning naming the asset and the tags — toggle them in the inspector and re-run.
- Builds `Assets/Scenes/ArabicTest.unity` with two text blocks:
  - `ArabicText_Raw` — plain `TextMeshProUGUI` fed the [test string](Assets/ArabicTestString.txt) **without** RTLTMPro. The hypothesis is that the new OpenType pipeline does shaping itself, so this should now render correctly.
  - `ArabicText_RTL` — `RTLTextMeshPro`, same string. Comparing the two shows whether RTLTMPro is now redundant.

## What to compare against `builtin-tmp/`

Open the same scene in both projects and screenshot. You should see in this project:

- Harakat **anchored to their base letter** (Mark-to-Base working — GPOS `mark`).
- Shadda + vowel rendering as a single composed cluster (Mark-to-Mark working — GPOS `mkmk`).
- Standard ligatures (e.g. `لله`) rendering as the proper ligature glyph (GSUB `liga` / `rlig`).
- Possibly: correct contextual shaping **without RTLTMPro pre-processing**, since the GSUB `init/medi/fina/isol` features now run inside TMP itself.

## If the auto-enable warning fires

Open `Assets/Fonts/Amiri-Regular SDF.asset` in the inspector, find the **OpenType / Font Features** section, and toggle on: `liga`, `rlig`, `mark`, `mkmk`, `init`, `medi`, `fina`, `isol`, `ccmp`. Then re-run the menu item to rebuild the scene — or just save and re-open the scene.
