# Unity Arabic Font Support — TMP Version Comparison

A side-by-side study of Arabic text rendering in Unity 6 with two TextMesh Pro versions.

## Hypothesis

TMP **3.2.0-pre.15** adds OpenType Layout features (**Ligatures, Mark-to-Base, Mark-to-Mark**), which should fix long-standing Arabic problems in Unity — specifically the rendering of harakat (diacritical marks) where vowel marks fail to position correctly over their base letters.

## Projects

| Project | TMP version | Notes |
| --- | --- | --- |
| `builtin-tmp/` | The TMP bundled with `com.unity.ugui` in Unity 6 (stable) | Baseline — reproduces existing problems |
| `tmp-preview/` | `com.unity.ugui` **3.2.0-pre.15** (preview) | Tests the new OpenType layout pipeline |

Both projects:

- Are Unity 6 (`6000.3.14f1`)
- Use the same Arabic font (**Amiri Regular**, SIL OFL) so the only variable is the TMP version
- Include **RTLTMPro** via Package Manager git URL
- Ship an Editor automation script (`Assets/Editor/ArabicTestSetup.cs`) that generates the font asset and test scene from a single menu item: **Arabic Study → Run Full Setup**

## Test string

The shared test string lives at `Assets/ArabicTestString.txt` in each project and includes a mix of:

- Plain Arabic words
- Words with full harakat (fatha, kasra, damma, sukun, shadda, tanwin)
- Mark-to-mark cases (shadda + vowel)
- Bidirectional mixed Arabic / Latin / digits

## First-open workflow

1. Open one of the projects in Unity Hub. Let Package Manager resolve (the preview project will pull TMP 3.2.0-pre.15 and may take a few minutes the first time).
2. Import TMP Essentials when prompted.
3. Run **menu → Arabic Study → Run Full Setup**. This generates the SDF font asset and the `Assets/Scenes/ArabicTest.unity` scene, ready to enter Play mode.
4. Repeat for the other project. See each project's `SETUP.md` for details and the OpenType-feature notes for the preview project.

## License notes

- **Amiri** font — SIL Open Font License 1.1 — redistributed in this repo.
- **RTLTMPro** — MIT License — fetched via Package Manager.
