# tmp-2022 — Control project (Unity 2022.3 LTS + standalone preview TMP)

The purpose of this project is to **confirm** that the OpenType layout features (Ligatures, Mark-to-Base, Mark-to-Mark) which now ship inside Unity 6's bundled TMP are equivalent to what the standalone `com.unity.textmeshpro@3.2.0-pre.15` preview offered. By rendering the same Amiri Arabic test string on the same RTLTMPro variant in both projects, any rendering difference between `tmp-2022/` and `tmp-preview/` isolates the impact of the Editor / bundled-TMP merge.

## Stack

| | tmp-2022 (this project) | tmp-preview (companion) |
| --- | --- | --- |
| Unity | **2022.3.21f1** LTS | 6000.3.14f1 |
| TMP source | `com.unity.textmeshpro@3.2.0-pre.15` (standalone preview) | `com.unity.ugui@2.0.0` (bundled TMP 5.0.0) |
| RTLTMPro | **MeemAinEdu/RTLTMPro** (pre-merge fork w/ Preserve Shadda, v3.4.5-edit.1) | OmarWKH/RTLTMPro (post-merge, v4.0.0 + Preserve Shadda) |
| Font | Amiri Regular (SIL OFL) | Amiri Regular (same file) |
| Test string | `Assets/ArabicTestString.txt` (same as tmp-preview) | same |

The control deliberately uses the MeemAinEdu fork rather than the post-merge OmarWKH fork because Unity 2022.3 predates the upstream v4.0.0 "Fix issues with Unity 6" changes — using a fork dated before that release matches the era of the standalone TMP preview.

## First-open workflow

1. Open this folder with **Unity 2022.3.21f1**. Package Manager resolves `com.unity.textmeshpro` 3.2.0-pre.15 (no deprecation warning on this Unity version) and RTLTMPro from MeemAinEdu.
2. When the TMP Importer dialog appears, click **Import TMP Essentials**.
3. Run **menu → Arabic Study → Run Full Setup**. This generates `Assets/Fonts/Amiri-Regular SDF.asset` (dynamic SDF, 2048², SDFAA, padding 9, multi-atlas), best-effort enables `liga / rlig / mark / mkmk / init / medi / fina / isol / ccmp` on it, and saves `Assets/Scenes/ArabicTest.unity` with a raw `TextMeshProUGUI` and an `RTLTextMeshPro` side by side rendering the test string.
4. Use **menu → Arabic Study → Font Table Search** to inspect what made it into the Character / Glyph / Ligature / Pair Adjustment / Mark-to-Base / Mark-to-Mark tables on the font asset.

## What to compare against tmp-preview

Open the scene in both Editors, screenshot. If the rendering is essentially identical:

- The OpenType layout features in Unity 6's bundled TMP behave the same as the standalone preview that originally introduced them.
- The decision to drop `builtin-tmp/` was correct — there is no untapped feature gap on Unity 6.
- Any harakat / ligature rendering issues seen in `tmp-preview/` are inherent to the feature implementation, not to a missing port.

If the rendering differs:

- Note exactly which marks / ligatures differ. The difference indicates a real divergence between the standalone preview and the merged-into-ugui version — useful evidence for filing a bug against the Unity 6 TMP.

## Caveats

- **Dynamic atlas mode**: glyphs are added on demand. Enter Play mode or scrub the scene once before using Font Table Search on exotic glyphs.
- **OpenType auto-enable**: this script does a best-effort SerializedObject set of the feature list on the font asset. If it logs a warning, open the SDF asset in the inspector and toggle the features manually.
- This project is intentionally throwaway / verification-only. Once the equivalence is confirmed (or refuted), it can be deleted without losing anything.
