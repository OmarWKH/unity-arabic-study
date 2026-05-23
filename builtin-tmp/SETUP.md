# Setup — built-in TMP (baseline)

Project pinned to Unity **6000.0.32f1** with `com.unity.ugui` **2.0.0** (the TMP bundled with Unity 6 LTS). This is the baseline that should reproduce the typical Arabic harakat positioning problems.

## First open

1. Open this folder in Unity Hub. Let Package Manager resolve — it will fetch RTLTMPro from GitHub.
2. When the **TMP Importer** dialog appears, click **Import TMP Essentials**. (Do NOT import the examples & extras — they aren't needed.)
3. Wait for the import to finish, then close the dialog.

## Build the TMP font asset

1. `Window > TextMeshPro > Font Asset Creator`
2. **Source Font File**: `Assets/Fonts/Amiri-Regular.ttf`
3. **Sampling Point Size**: Auto Sizing
4. **Padding**: 9
5. **Packing Method**: Optimum
6. **Atlas Resolution**: 2048 x 2048
7. **Character Set**: `Unicode Range (Hex)`
8. **Character Sequence**: `0020-007E,00A0-00FF,0600-06FF,0750-077F,FB50-FDFF,FE70-FEFF`
   *(Latin + Arabic + Arabic Supplement + Presentation Forms-A + Presentation Forms-B — covers harakat and shaping forms)*
9. **Render Mode**: SDFAA
10. Click **Generate Font Atlas**, then **Save as…** → `Assets/Fonts/Amiri-Regular SDF.asset`

## Build the test scene

1. `File > New Scene` → Basic (Built-in) → save as `Assets/Scenes/ArabicTest.unity`.
2. `GameObject > UI > Text - TextMeshPro` (creates Canvas + EventSystem + Text). Name the text `ArabicText_Raw`.
3. Set its **Font Asset** to `Amiri-Regular SDF`.
4. Paste the contents of `Assets/ArabicTestString.txt` into the Text field.
5. Duplicate the text object, name it `ArabicText_RTL`, and replace its `TextMeshProUGUI` component with **RTL Text Mesh Pro UGUI** (`Component > UI > RTL TMP > RTL Text Mesh Pro UGUI`). Paste the same string.
6. Position the two text blocks side by side so you can compare raw vs. RTLTMPro-shaped output.

## What to look for

In this baseline you should see:

- Harakat that **drift** above/below their base letter rather than sitting on the cap.
- Shadda + vowel combinations rendering as two stacked marks rather than the proper composed glyph.
- Some marks dropping entirely on certain letters.

These are the exact problems the preview TMP project should resolve.
