# unity-arabic

> *Drafted by Claude (Anthropic) in collaboration with the actual debugging work in this repo. Verify any non-obvious claims before relying on them.*

Tooling and notes for shipping Arabic text in Unity 6 with TextMeshPro + RTLTMPro. The repo collects:

- **Python tools** for analysing, scoring, and patching Arabic fonts (`tools/`)
- **Unity Editor tools** for inspecting and modifying TMP font assets (`unity6/Assets/Editor/`)
- **Three empirically-validated fonts** that render Arabic correctly in TMP without further patching (`unity6/Assets/Fonts/*-pfb-rehomed.*`)
- **A findings document** explaining what's broken in TMP's Arabic handling, what the workarounds are, and how to evaluate new fonts ([`FINDINGS.md`](FINDINGS.md))

If you're shipping Arabic in Unity: start with the [Quick-start](#quick-start) below and skim [`FINDINGS.md`](FINDINGS.md) before committing to a font choice.

## What's here

### Python tools (`tools/`)

```
score-arabic-font.py                Score a font on 10 weighted criteria, with per-letter detail
rehome-arabic-presentation-forms.py Add PF-B cmap entries from base Arabic glyphs
shape-with-harfbuzz.py              Reference "what should happen" via HarfBuzz
inspect-gsub-ligature.py            Walk GSUB for a specific substitution
inspect-gpos-markmark.py            Walk GPOS for a specific Mark-to-Mark pair
extract-arabic-mark-variants.py     Feeds the Unity Font Feature Patcher
fetch-fonts.py + candidate-fonts.txt  Polite downloader for a candidate corpus
```

### Editor tools (Unity, `Arabic Study` menu)

```
Font Table Search      Chip-rendered inspector of any TMP font asset's tables
RTLTMPro Debugger      Before/after view of RTLTMPro's string transformation
Font Feature Patcher   Inject missing GPOS records into a font asset (last-resort)
```

### Findings ([`FINDINGS.md`](FINDINGS.md))

Three TMP issues uncovered during the work, each not documented in TMP's own material:

1. TMP's GSUB/GPOS importer doesn't recurse through OpenType chained-context wrappers. Fonts that express substitution / positioning through such wrappers (Amiri included) misrender in TMP even though they render correctly in browsers and UI Toolkit.
2. TMP's runtime dynamic-atlas adder silently drops codepoints unless **Multi Atlas Textures** is enabled. Always enable it.
3. Direct per-letter Mark-to-Base coverage is a much better TMP-suitability predictor than overall feature richness. Noto Naskh tops the as-is scoreboard but misrenders in practice; high-harakat-coverage fonts (Vazirmatn, Tajawal, Thmanyah Sans) render correctly.

The document also covers the Amiri shadda+fatha bug in detail, the 42-font evaluation results, and recommendations for shipping.

## Quick-start

Render Arabic in Unity 6.3:

1. Clone this repo, open `unity6/` in Unity 6.3.
2. Package Manager resolves UGUI + RTLTMPro automatically.
3. Pick one of `unity6/Assets/Fonts/Vazirmatn-Variable-pfb-rehomed.ttf`, `Tajawal-Regular-pfb-rehomed.ttf`, or `ThmanyahSans-Regular-pfb-rehomed.otf`.
4. Build a TMP SDF font asset from it. **Enable Multi Atlas Textures.**
5. Use the `RTLTextMeshPro` component for any Arabic text.

Evaluate a different font:

```bash
python -m venv .venv && source .venv/Scripts/activate
pip install fonttools uharfbuzz
python tools/score-arabic-font.py path/to/font.ttf
```

The scorer prints per-criterion completeness with the specific missing codepoints / glyph pairs by Unicode name, so you can see exactly what (if anything) the font would need to be usable.

## Layout

```
unity-arabic/
├── unity6/         Main Unity 6.3 project — editor tools, validated fonts
├── tmp-2022/       Control project (Unity 2022.3 LTS + standalone TMP preview)
├── tools/          Python font-analysis tooling
├── font-cache/     Gitignored scratch dir for candidate fonts
├── README.md
└── FINDINGS.md     Full write-up — recommended reading
```

## License notes

Committed fonts under SIL OFL 1.1 except:

- **Dubai Font** (in `font-cache/` only, not in `unity6/Assets/`) — Government of Dubai. Free for personal use, commercial requires their license.
- **Thmanyah Sans** — verify with Thmanyah LLC before commercial use; the rehomed copy in `unity6/Assets/Fonts/` was sourced via a community mirror.

**RTLTMPro** — MIT, fetched via Package Manager from the [OmarWKH fork](https://github.com/OmarWKH/RTLTMPro).
