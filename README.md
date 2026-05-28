# unity-arabic

Investigation of Arabic text rendering in Unity 6 with TextMeshPro + RTLTMPro. What started as a "compare TMP preview vs bundled TMP" study turned into a deeper look at why a popular Arabic font (Amiri) misrenders vocalised text in TMP, what TMP's GSUB/GPOS importer leaves on the table, which fonts work reliably anyway, and how to evaluate any new Arabic font for TMP-suitability.

**The full write-up is in [`FINDINGS.md`](FINDINGS.md).** Recommended reading if you're shipping Arabic in Unity.

## TL;DR

- TMP's GSUB/GPOS importer doesn't recurse through OpenType chained-context wrappers. Fonts that express shadda+vowel positioning through such wrappers (Amiri, AmiriQuran) misrender in TMP even though they render correctly in browsers / UI Toolkit.
- TMP's runtime dynamic-atlas adder silently drops codepoints unless **Multi Atlas Textures** is enabled on the font asset. Always enable that toggle for Arabic fonts.
- Of 42 evaluated fonts, three render shadda+fatha correctly in TMP without patching: **Vazirmatn**, **Tajawal**, **Thmanyah Sans** (the latter two after a simple PF-B rehoming step).

## Layout

```
unity-arabic/
├── unity6/         ← main Unity 6.3 project. All editor tooling. Working fonts.
├── tmp-2022/       ← control project (Unity 2022.3 + standalone TMP preview)
├── tools/          ← Python utilities: font scorer, rehomer, GPOS/GSUB inspectors
├── font-cache/     ← gitignored scratch directory for candidate fonts
├── FINDINGS.md     ← the write-up
└── README.md
```

## Quick-start

Open `unity6/` in Unity 6000.3.14f1. Package Manager resolves UGUI + RTLTMPro automatically. Use one of the three validated fonts from `unity6/Assets/Fonts/` (suffix `-pfb-rehomed`) for any Arabic UI text. Enable Multi Atlas Textures on the font asset. See [`FINDINGS.md#quick-start`](FINDINGS.md#quick-start) for the longer version.

To evaluate a different font for TMP-compatibility:

```bash
python -m venv .venv && source .venv/Scripts/activate
pip install fonttools uharfbuzz
python tools/score-arabic-font.py path/to/font.ttf
```

The scorer prints a per-criterion breakdown plus a post-rehome projection. See [`FINDINGS.md`](FINDINGS.md) for the criteria and what they mean.

## Editor tools (`Arabic Study` menu in Unity)

- **Run Full Setup** — creates a Tajawal-based TMP font asset + test scene
- **Font Table Search** — chip-rendered inspector of any TMP font asset
- **RTLTMPro Debugger** — before/after view of RTLTMPro string transformation
- **Font Feature Patcher** — last-resort tool for injecting missing GPOS records into a font asset

## License notes

- All committed fonts are SIL OFL 1.1 except:
  - **Dubai Font** (in `font-cache/`, not committed to Unity assets) — Government of Dubai, free for personal use, commercial requires their license.
  - **Thmanyah Sans** — verify with Thmanyah LLC before shipping commercially; the rehomed version in `unity6/Assets/Fonts/` was sourced via a community mirror.
- **RTLTMPro** — MIT — fetched via Package Manager from the OmarWKH fork.
