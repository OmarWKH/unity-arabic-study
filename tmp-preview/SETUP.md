# Setup — TMP 3.2.0-pre.15 (preview)

Project pinned to Unity **6000.0.32f1** with `com.unity.ugui` **3.2.0-pre.15`. This preview adds OpenType Layout support (Ligatures, Mark-to-Base, Mark-to-Mark) — the features that should make harakat positioning correct for the first time in TMP.

## First open

1. Open this folder in Unity Hub. Package Manager will resolve `com.unity.ugui` 3.2.0-pre.15 and RTLTMPro from GitHub. **This may take a few minutes the first time.**
2. The preview TMP package's importer is different from the legacy one — if a TMP Essentials prompt appears, you can still import (it's compatible), but the font asset workflow in step 2 below is what matters.

## Build the TMP font asset (with OpenType features enabled)

1. `Window > TextMeshPro > Font Asset Creator`
2. **Source Font File**: `Assets/Fonts/Amiri-Regular.ttf`
3. **Sampling Point Size**: Auto Sizing
4. **Padding**: 9
5. **Packing Method**: Optimum
6. **Atlas Resolution**: 2048 x 2048
7. **Character Set**: `Unicode Range (Hex)`
8. **Character Sequence**: `0020-007E,00A0-00FF,0600-06FF,0750-077F,FB50-FDFF,FE70-FEFF`
9. **Render Mode**: SDFAA
10. Click **Generate Font Atlas**, then **Save as…** → `Assets/Fonts/Amiri-Regular SDF.asset`.
11. **IMPORTANT — enable OpenType layout features**: select the saved font asset, find the **OpenType / Font Features** section in the inspector, and enable at minimum:
    - `liga` (Standard Ligatures)
    - `rlig` (Required Ligatures)
    - `mark` (Mark Positioning — **Mark-to-Base**)
    - `mkmk` (Mark-to-Mark Positioning)
    - `init`, `medi`, `fina`, `isol` (Arabic positional shaping)
    - `ccmp` (Glyph Composition / Decomposition)

    These are the key features the new pipeline can finally consume from the OTF/TTF.

## Build the test scene

1. `File > New Scene` → Basic (Built-in) → save as `Assets/Scenes/ArabicTest.unity`.
2. `GameObject > UI > Text - TextMeshPro`. Name it `ArabicText_OpenType`.
3. Set its **Font Asset** to `Amiri-Regular SDF`.
4. Paste the contents of `Assets/ArabicTestString.txt`.
5. *(Optional)* Duplicate, name `ArabicText_RTLTMPro`, and replace the TMP component with **RTL Text Mesh Pro UGUI**. The hypothesis is that with OpenType layout enabled, the raw `TextMeshProUGUI` may render Arabic correctly **without** needing the RTLTMPro pre-shaping pass — comparing the two side-by-side is the experiment.

## What to compare against `builtin-tmp/`

Open the same scene in both projects and screenshot. You should see in this project:

- Harakat **anchored to their base letter** (Mark-to-Base working).
- Shadda + vowel rendering as a single composed cluster (Mark-to-Mark working).
- Standard ligatures (e.g. `لله`) rendering as the proper ligature glyph (Ligatures working).
- Possibly: correct contextual shaping **without RTLTMPro pre-processing**, since the GSUB `init/medi/fina/isol` features now run inside TMP itself.
