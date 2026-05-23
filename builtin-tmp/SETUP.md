# Setup — built-in TMP (baseline)

Project pinned to Unity **6000.3.14f1** with `com.unity.ugui` 2.0.0 (TMP bundled with Unity 6). This is the baseline that should reproduce the typical Arabic harakat positioning problems.

## Automated setup

1. Open this folder in Unity Hub. Let Package Manager resolve — it pulls RTLTMPro from GitHub.
2. When the **TMP Importer** dialog appears, click **Import TMP Essentials**.
3. Once the project is loaded with no compile errors, run **menu → Arabic Study → Run Full Setup**.

That single menu item runs [`Assets/Editor/ArabicTestSetup.cs`](Assets/Editor/ArabicTestSetup.cs), which:

- Creates `Assets/Fonts/Amiri-Regular SDF.asset` (dynamic SDF atlas, 2048², SDFAA, padding 9, multi-atlas enabled).
- Builds `Assets/Scenes/ArabicTest.unity` containing a Screen Space - Overlay Canvas with two text blocks:
  - `ArabicText_Raw` — plain `TextMeshProUGUI`, `isRightToLeftText = true`, fed the contents of [`Assets/ArabicTestString.txt`](Assets/ArabicTestString.txt) verbatim.
  - `ArabicText_RTL` — `RTLTextMeshPro` (RTLTMPro), same input string, pre-shaped by RTLTMPro before reaching TMP.
- Saves the scene and refreshes the asset database.

Re-running the menu item reuses an existing font asset; delete `Amiri-Regular SDF.asset` to regenerate.

## What to look for

In this baseline you should see, in the **Raw** text:

- Harakat that **drift** above/below their base letter rather than sitting on the cap.
- Shadda + vowel combinations rendering as two stacked marks rather than the proper composed glyph.
- Some marks dropping entirely on certain letters.

The **RTL** text exists for comparison — RTLTMPro pre-shapes the string into presentation forms, which fixes contextual joining but does not solve mark positioning, because mark positioning is a GPOS feature that TMP 2.x cannot execute.
