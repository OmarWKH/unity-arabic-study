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

- Are Unity 6 (`6000.0.32f1`)
- Use the same Arabic font (**Amiri Regular**, SIL OFL) so the only variable is the TMP version
- Include **RTLTMPro** via Package Manager git URL
- Ship with a placeholder scene `Assets/Scenes/ArabicTest.unity` — you populate the TMP text objects after first open (see each project's `SETUP.md`)

## Test string

The shared test string lives at `Assets/ArabicTestString.txt` in each project and includes a mix of:

- Plain Arabic words
- Words with full harakat (fatha, kasra, damma, sukun, shadda, tanwin)
- Mark-to-mark cases (shadda + vowel)
- Bidirectional mixed Arabic / Latin / digits

## First-open workflow

1. Open the project in Unity Hub (it will resolve packages — the preview project will pull TMP 3.2.0-pre.15).
2. When TMP prompts to import the TMP Essentials, **decline** for the preview project (the new pipeline doesn't need the legacy essentials package the same way) — accept for the built-in project.
3. Follow `SETUP.md` inside each project to finish wiring the test scene.

## License notes

- **Amiri** font — SIL Open Font License 1.1 — redistributed in this repo.
- **RTLTMPro** — MIT License — fetched via Package Manager.
