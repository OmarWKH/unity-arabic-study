using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace ArabicStudy.Editor
{
    /// EditorWindow for inspecting a TMP font asset by glyph.
    ///
    /// Accepts input as:
    ///   - A literal character pasted into the field (first codepoint is taken)
    ///   - A Unicode codepoint: "U+0628", "0x628", "0628" (hex), or "1576" (dec, tick "Decimal")
    ///   - A glyph ID (internal index into glyphTable)
    ///
    /// Searches and reports hits across:
    ///   - Character Table (Unicode → Glyph mapping)
    ///   - Glyph Table (glyph metrics & atlas rect)
    ///   - Ligature Substitution Records
    ///   - Glyph Pair Adjustment Records (kerning)
    ///   - Mark-to-Base Adjustment Records
    ///   - Mark-to-Mark Adjustment Records
    ///
    /// Reverse glyph→codepoint resolution uses FontEngine across the relevant Unicode
    /// ranges (ASCII, Arabic block, Arabic Supplement, Arabic Extended-A, Arabic
    /// Presentation Forms-A and -B). The font asset's character table alone is
    /// insufficient because GPOS / GSUB records mostly address SHAPED glyphs whose
    /// codepoints live in the Presentation Forms ranges, not in the base Arabic block
    /// that TMP populates the character table with.
    public sealed class TMPFontTableSearch : EditorWindow
    {
        // ---------- types ----------

        private enum QueryMode { Auto, Codepoint, GlyphId, Char }

        private struct CharacterHit { public uint unicode; public uint glyphIndex; public float scale; }

        private struct GlyphHit
        {
            public uint index;
            public int atlasIndex;
            public float scale;
            public float w, h, bx, by, adv;
            public float rx, ry, rw, rh;
        }

        private struct LigatureHit
        {
            public string role; // PRODUCT | COMPONENT | PRODUCT+COMPONENT
            public uint[] componentGlyphIDs;
            public uint ligatureGlyphID;
        }

        private struct KerningHit
        {
            public string role; // FIRST | SECOND | FIRST+SECOND
            public uint firstGlyph, secondGlyph;
            public float fxPlc, fyPlc, fxAdv, fyAdv;
            public float sxPlc, syPlc, sxAdv, syAdv;
        }

        private struct MarkBaseHit
        {
            public string role; // BASE | MARK | BASE+MARK
            public uint baseGlyph, markGlyph;
            public float baseAx, baseAy;
            public float markOx, markOy;
        }

        private struct MarkMarkHit
        {
            public string role; // BASE | COMBINING | BASE+COMBINING
            public uint baseGlyph, combiningGlyph;
            public float baseAx, baseAy;
            public float combOx, combOy;
        }

        // ---------- ranges ----------

        private static readonly (uint start, uint end, string label)[] ReverseMapRanges =
        {
            (0x0020, 0x007E, "ASCII"),
            (0x00A0, 0x00FF, "Latin-1 Supp"),
            (0x0600, 0x06FF, "Arabic"),
            (0x0750, 0x077F, "Arabic Supplement"),
            (0x08A0, 0x08FF, "Arabic Extended-A"),
            (0xFB50, 0xFDFF, "Arabic Presentation Forms-A"),
            (0xFE70, 0xFEFF, "Arabic Presentation Forms-B"),
        };

        // ---------- state ----------

        private TMP_FontAsset _fontAsset;
        private string _query = "";
        private QueryMode _mode = QueryMode.Auto;
        private bool _decimal;

        private bool _hasResult;
        private uint _resolvedCp;
        private uint _resolvedGid;
        private string _resolveNote;
        private List<CharacterHit> _charHits = new();
        private List<GlyphHit> _glyphHits = new();
        private List<LigatureHit> _ligHits = new();
        private List<KerningHit> _kernHits = new();
        private List<MarkBaseHit> _m2bHits = new();
        private List<MarkMarkHit> _m2mHits = new();

        private Vector2 _scroll;
        private readonly Dictionary<string, bool> _foldout = new();
        private const int MaxRowsPerSection = 500;

        // Secondary filter: narrows hits to records that also reference this glyph.
        private string _filter = "";
        private QueryMode _filterMode = QueryMode.Auto;
        private bool _filterDecimal;
        private uint _filterCp;
        private uint _filterGid;
        private bool _filterActive;
        private string _filterNote = "";

        // Visual chip rendering.
        private float _chipSize = 56f;
        private Dictionary<uint, UnityEngine.TextCore.Glyph> _glyphLookup;
        private TMP_FontAsset _cachedGlyphLookupFont;

        // Reverse map cache.
        private TMP_FontAsset _cachedMapFont;
        private Dictionary<uint, List<uint>> _gidToCps;
        private string _mapBuildNote = "";

        // GUIStyle that uses a label font with reasonable Unicode coverage.
        private GUIStyle _rowStyle;

        // ---------- menu / lifecycle ----------

        [MenuItem("Arabic Study/Font Table Search")]
        public static void Open()
        {
            var win = GetWindow<TMPFontTableSearch>("TMP Font Table Search");
            win.minSize = new Vector2(640, 480);
        }

        private void OnGUI()
        {
            EnsureStyle();
            DrawHeader();
            EditorGUILayout.Space(4);
            if (!_hasResult) return;
            DrawResolvedSummary();
            using (var s = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = s.scrollPosition;
                DrawFilteredSection("Character Table", "char", _charHits, Keep, DrawCharacterRow);
                DrawFilteredSection("Glyph Table", "glyph", _glyphHits, Keep, DrawGlyphRow);
                DrawFilteredSection("Ligature Substitution Records", "lig", _ligHits, Keep, DrawLigatureRow);
                DrawFilteredSection("Glyph Pair Adjustment (Kerning)", "kern", _kernHits, Keep, DrawKerningRow);
                DrawFilteredSection("Mark-to-Base Adjustment", "m2b", _m2bHits, Keep, DrawMarkBaseRow);
                DrawFilteredSection("Mark-to-Mark Adjustment", "m2m", _m2mHits, Keep, DrawMarkMarkRow);
            }
        }

        /// Draw a foldout section over a typed hit list, applying the secondary
        /// filter predicate. The header shows "filtered / total" when active so
        /// you can see how much the filter narrowed things down.
        private void DrawFilteredSection<T>(string title, string id, List<T> hits,
            Func<T, bool> keep, Action<T> drawRow)
        {
            // Count first (cheap struct iterations).
            int kept = 0;
            for (int i = 0; i < hits.Count; i++) if (keep(hits[i])) kept++;

            int total = hits.Count;
            string countLabel = _filterActive ? $"[{kept} / {total}]" : $"[{total}]";

            if (!_foldout.TryGetValue(id, out var open))
                open = kept > 0 && kept <= 50;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                open = EditorGUILayout.Foldout(open, $"{title}   {countLabel}", true, EditorStyles.foldoutHeader);
                _foldout[id] = open;
                if (!open) return;
                if (kept == 0)
                {
                    EditorGUILayout.LabelField(
                        _filterActive ? "  (no match after filter)" : "  (no match)",
                        EditorStyles.miniLabel);
                    return;
                }

                int drawn = 0;
                for (int i = 0; i < hits.Count && drawn < MaxRowsPerSection; i++)
                {
                    if (!keep(hits[i])) continue;
                    drawRow(hits[i]);
                    drawn++;
                }
                if (kept > MaxRowsPerSection)
                    EditorGUILayout.HelpBox(
                        $"showing first {MaxRowsPerSection} of {kept} — narrow the filter further to see the rest",
                        MessageType.Info);
            }
        }

        private void EnsureStyle()
        {
            if (_rowStyle != null) return;
            _rowStyle = new GUIStyle(EditorStyles.label) { richText = true };
        }

        // ---------- header / input ----------

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("TMP Font Table Search", EditorStyles.boldLabel);
            using (var c = new EditorGUI.ChangeCheckScope())
            {
                _fontAsset = (TMP_FontAsset)EditorGUILayout.ObjectField(
                    "Font Asset", _fontAsset, typeof(TMP_FontAsset), false);
                if (c.changed) { InvalidateReverseMap(); _glyphLookup = null; _cachedGlyphLookupFont = null; }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _query = EditorGUILayout.TextField("Query", _query);
                _mode = (QueryMode)EditorGUILayout.EnumPopup(_mode, GUILayout.Width(90));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _decimal = EditorGUILayout.ToggleLeft(
                    "Interpret bare digits as decimal (default: hex codepoint)",
                    _decimal);
                if (GUILayout.Button("Search", GUILayout.Width(80))) RunSearch();
                if (GUILayout.Button("Rebuild Map", GUILayout.Width(110))) { InvalidateReverseMap(); EnsureReverseMap(); }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Chip size", GUILayout.Width(70));
                _chipSize = GUILayout.HorizontalSlider(_chipSize, 24f, 128f);
                EditorGUILayout.LabelField($"{(int)_chipSize}px", GUILayout.Width(40));
            }

            // Secondary filter — narrows results to records that also reference this glyph.
            using (new EditorGUILayout.HorizontalScope())
            {
                _filter = EditorGUILayout.TextField("Filter by", _filter);
                _filterMode = (QueryMode)EditorGUILayout.EnumPopup(_filterMode, GUILayout.Width(90));
                if (GUILayout.Button("Clear", GUILayout.Width(60))) { _filter = ""; _filterCp = 0; _filterGid = 0; _filterActive = false; _filterNote = ""; }
            }
            ResolveFilter();
            if (_filterActive)
                EditorGUILayout.HelpBox(
                    $"filtering by  '{Cp(_filterCp)}'  U+{_filterCp:X4}{(string.IsNullOrEmpty(UnicodeName(_filterCp)) ? "" : "  " + UnicodeName(_filterCp))}  (glyph {_filterGid})",
                    MessageType.Info);
            else if (!string.IsNullOrEmpty(_filter) && !string.IsNullOrEmpty(_filterNote))
                EditorGUILayout.HelpBox($"filter not applied: {_filterNote}", MessageType.Warning);

            EditorGUILayout.HelpBox(
                "Examples:  ب   |   U+0628   |   0628   |   0x628   |   12345 (glyph id)\n" +
                "Auto mode picks Char for length-1, Codepoint for U+/0x/3+hex, GlyphId otherwise.\n" +
                "Reverse glyph→codepoint resolution covers ASCII + full Arabic incl. Presentation Forms.",
                MessageType.None);
        }

        private void DrawResolvedSummary()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Resolved", EditorStyles.miniBoldLabel);
                if (!string.IsNullOrEmpty(_resolveNote))
                    EditorGUILayout.LabelField(_resolveNote, EditorStyles.miniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (_resolvedGid != 0) DrawGlyphChip(_resolvedGid, Mathf.Max(_chipSize, 64f));
                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField(
                            $"codepoint  {(_resolvedCp != 0 ? $"U+{_resolvedCp:X4}  '{Cp(_resolvedCp)}'" : "—")}",
                            _rowStyle);
                        EditorGUILayout.LabelField(
                            $"glyph ID   {(_resolvedGid != 0 ? _resolvedGid.ToString() : "—")}",
                            _rowStyle);
                        if (!string.IsNullOrEmpty(_mapBuildNote))
                            EditorGUILayout.LabelField(_mapBuildNote, EditorStyles.miniLabel);
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        // ---------- chip rendering ----------

        private void EnsureGlyphLookup()
        {
            if (_fontAsset == null) { _glyphLookup = null; _cachedGlyphLookupFont = null; return; }
            if (_glyphLookup != null && _cachedGlyphLookupFont == _fontAsset) return;
            _glyphLookup = new Dictionary<uint, UnityEngine.TextCore.Glyph>(_fontAsset.glyphTable.Count);
            foreach (var g in _fontAsset.glyphTable) _glyphLookup[g.index] = g;
            _cachedGlyphLookupFont = _fontAsset;
        }

        /// Render a single glyph as a visual chip: a `size × size` square containing
        /// the glyph blitted from the font asset's atlas, plus a tiny caption line
        /// below it with the codepoint(s) and glyph ID.
        private void DrawGlyphChip(uint gid, float size)
        {
            EnsureGlyphLookup();
            var captionH = 30f;
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(size)))
            {
                var rect = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
                EditorGUI.DrawRect(rect, new Color(0.13f, 0.13f, 0.13f));

                bool drawn = false;
                if (_glyphLookup != null && _glyphLookup.TryGetValue(gid, out var glyph)
                    && _fontAsset.atlasTextures != null
                    && glyph.atlasIndex >= 0 && glyph.atlasIndex < _fontAsset.atlasTextures.Length)
                {
                    var atlas = _fontAsset.atlasTextures[glyph.atlasIndex];
                    if (atlas != null)
                    {
                        var gr = glyph.glyphRect;
                        float aw = atlas.width, ah = atlas.height;
                        if (aw > 0 && ah > 0 && gr.width > 0 && gr.height > 0)
                        {
                            // Fit glyph into chip preserving aspect ratio.
                            float pad = 4f;
                            float box = size - pad * 2f;
                            float gw = gr.width, gh = gr.height;
                            float scale = Mathf.Min(box / gw, box / gh);
                            float dw = gw * scale, dh = gh * scale;
                            var inner = new Rect(rect.x + (size - dw) * 0.5f, rect.y + (size - dh) * 0.5f, dw, dh);
                            var uv = new Rect(gr.x / aw, gr.y / ah, gr.width / aw, gr.height / ah);
                            GUI.DrawTextureWithTexCoords(inner, atlas, uv, true);
                            drawn = true;
                        }
                    }
                }
                if (!drawn)
                {
                    // Fallback: show the glyph ID as a number, since the Editor IMGUI
                    // font often lacks Arabic coverage and would render as tofu.
                    var fallbackStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = Mathf.Max(10, (int)(size * 0.28f)),
                        normal = { textColor = new Color(0.85f, 0.55f, 0.55f) },
                    };
                    GUI.Label(rect, gid == 0 ? ".notdef" : $"g{gid}\n(not in atlas)", fallbackStyle);
                }

                // Caption below: codepoint(s) + glyph id.
                string cap;
                if (_gidToCps != null && _gidToCps.TryGetValue(gid, out var caps) && caps.Count > 0)
                {
                    cap = caps.Count == 1 ? $"U+{caps[0]:X4}" : $"U+{caps[0]:X4} +{caps.Count - 1}";
                }
                else cap = "(no cp)";
                var capRect = GUILayoutUtility.GetRect(size, captionH, GUILayout.Width(size), GUILayout.Height(captionH));
                var capStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.UpperCenter, wordWrap = false };
                GUI.Label(capRect, $"{cap}\ng{gid}", capStyle);
            }
        }

        private void DrawGlyphChipPair(uint a, uint b, float size, string separator = " + ")
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawGlyphChip(a, size);
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(20)))
                {
                    GUILayout.Space(size * 0.5f - 8f);
                    EditorGUILayout.LabelField(separator, GUILayout.Width(20));
                }
                DrawGlyphChip(b, size);
                GUILayout.FlexibleSpace();
            }
        }

        // ---------- row renderers ----------

        private void DrawCharacterRow(CharacterHit h)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawGlyphChip(h.glyphIndex, _chipSize);
                using (new EditorGUILayout.VerticalScope())
                {
                    var name = UnicodeName(h.unicode);
                    EditorGUILayout.LabelField(
                        $"'{Cp(h.unicode)}' U+{h.unicode:X4}{(string.IsNullOrEmpty(name) ? "" : "  " + name)}",
                        _rowStyle);
                    EditorGUILayout.LabelField($"→ glyph {h.glyphIndex}    scale = {h.scale}", EditorStyles.miniLabel);
                }
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.Space(2);
        }

        private void DrawGlyphRow(GlyphHit h)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawGlyphChip(h.index, _chipSize);
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField($"glyph {h.index}  atlas#{h.atlasIndex}  scale={h.scale}", _rowStyle);
                    EditorGUILayout.LabelField(
                        $"metrics  w={h.w} h={h.h} bx={h.bx} by={h.by} adv={h.adv}", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField(
                        $"rect     x={h.rx} y={h.ry} w={h.rw} h={h.rh}", EditorStyles.miniLabel);
                }
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.Space(2);
        }

        private void DrawLigatureRow(LigatureHit h)
        {
            using (new EditorGUILayout.VerticalScope())
            {
                var compLine = new System.Text.StringBuilder();
                if (h.componentGlyphIDs != null)
                    for (int i = 0; i < h.componentGlyphIDs.Length; i++)
                    {
                        if (i > 0) compLine.Append("  +  ");
                        compLine.Append(GlyphLabel(h.componentGlyphIDs[i]));
                    }
                EditorGUILayout.LabelField($"[{h.role}]   {compLine}   →   {GlyphLabel(h.ligatureGlyphID)}", _rowStyle);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (h.componentGlyphIDs != null)
                    {
                        for (int i = 0; i < h.componentGlyphIDs.Length; i++)
                        {
                            if (i > 0)
                            {
                                using (new EditorGUILayout.VerticalScope(GUILayout.Width(14)))
                                {
                                    GUILayout.Space(_chipSize * 0.5f - 8f);
                                    EditorGUILayout.LabelField("+", GUILayout.Width(14));
                                }
                            }
                            DrawGlyphChip(h.componentGlyphIDs[i], _chipSize);
                        }
                    }
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(28)))
                    {
                        GUILayout.Space(_chipSize * 0.5f - 8f);
                        EditorGUILayout.LabelField("→", GUILayout.Width(28));
                    }
                    DrawGlyphChip(h.ligatureGlyphID, _chipSize);
                    GUILayout.FlexibleSpace();
                }
            }
            EditorGUILayout.Space(4);
        }

        private void DrawKerningRow(KerningHit h)
        {
            using (new EditorGUILayout.VerticalScope())
            {
                EditorGUILayout.LabelField(
                    $"[{h.role}]   {GlyphLabel(h.firstGlyph)}   ·   {GlyphLabel(h.secondGlyph)}", _rowStyle);
                DrawGlyphChipPair(h.firstGlyph, h.secondGlyph, _chipSize, " · ");
                EditorGUILayout.LabelField(
                    $"first   xPlc={h.fxPlc}  yPlc={h.fyPlc}  xAdv={h.fxAdv}  yAdv={h.fyAdv}",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    $"second  xPlc={h.sxPlc}  yPlc={h.syPlc}  xAdv={h.sxAdv}  yAdv={h.syAdv}",
                    EditorStyles.miniLabel);
            }
            EditorGUILayout.Space(4);
        }

        private void DrawMarkBaseRow(MarkBaseHit h)
        {
            using (new EditorGUILayout.VerticalScope())
            {
                EditorGUILayout.LabelField(
                    $"[{h.role}]   base={GlyphLabel(h.baseGlyph)}   mark={GlyphLabel(h.markGlyph)}", _rowStyle);
                DrawGlyphChipPair(h.baseGlyph, h.markGlyph, _chipSize);
                EditorGUILayout.LabelField(
                    $"baseAnchor x={h.baseAx} y={h.baseAy}    markOffset x={h.markOx} y={h.markOy}",
                    EditorStyles.miniLabel);
            }
            EditorGUILayout.Space(4);
        }

        private void DrawMarkMarkRow(MarkMarkHit h)
        {
            using (new EditorGUILayout.VerticalScope())
            {
                EditorGUILayout.LabelField(
                    $"[{h.role}]   baseMark={GlyphLabel(h.baseGlyph)}   combining={GlyphLabel(h.combiningGlyph)}", _rowStyle);
                DrawGlyphChipPair(h.baseGlyph, h.combiningGlyph, _chipSize);
                EditorGUILayout.LabelField(
                    $"baseAnchor x={h.baseAx} y={h.baseAy}    combOffset x={h.combOx} y={h.combOy}",
                    EditorStyles.miniLabel);
            }
            EditorGUILayout.Space(4);
        }

        // ---------- search pipeline ----------

        private void RunSearch()
        {
            _hasResult = false;
            _charHits.Clear(); _glyphHits.Clear(); _ligHits.Clear();
            _kernHits.Clear(); _m2bHits.Clear(); _m2mHits.Clear();
            _resolveNote = "";
            _resolvedCp = 0; _resolvedGid = 0;

            if (_fontAsset == null) { _resolveNote = "(no font asset assigned)"; _hasResult = true; return; }
            if (string.IsNullOrEmpty(_query)) { _resolveNote = "(empty query)"; _hasResult = true; return; }

            if (!TryResolveQuery(out _resolvedCp, out _resolvedGid, out _resolveNote))
            { _hasResult = true; return; }

            EnsureReverseMap();
            PopulateAtlasForCodepoint(_resolvedCp); // make sure the queried glyph itself is in the atlas


            // Bidirectional resolve: codepoint ↔ glyph id
            if (_resolvedGid == 0 && _resolvedCp != 0)
            {
                foreach (var c in _fontAsset.characterTable)
                    if (c.unicode == _resolvedCp) { _resolvedGid = c.glyphIndex; break; }
                if (_resolvedGid == 0 && _gidToCps != null)
                    foreach (var kv in _gidToCps)
                        if (kv.Value.Contains(_resolvedCp)) { _resolvedGid = kv.Key; break; }
            }
            if (_resolvedCp == 0 && _resolvedGid != 0 && _gidToCps != null
                && _gidToCps.TryGetValue(_resolvedGid, out var cps) && cps.Count > 0)
                _resolvedCp = cps[0];

            CollectHits();
            PopulateAtlasForHits();
            _hasResult = true;
        }

        /// Ensure the resolved codepoint is rendered into the dynamic atlas so its chip
        /// doesn't come up blank when the glyph hasn't been seen by any text component yet.
        private void PopulateAtlasForCodepoint(uint cp)
        {
            if (cp == 0 || _fontAsset == null) return;
            try
            {
                _fontAsset.TryAddCharacters(new[] { cp }, out _);
                _glyphLookup = null; // force rebuild on next chip draw
                EditorUtility.SetDirty(_fontAsset);
            }
            catch { /* best-effort */ }
        }

        /// Walk every collected hit, gather glyph IDs referenced, map them back to a
        /// representative codepoint via the reverse map, and ask the font asset to
        /// materialise them in the atlas. Without this step, GPOS records pointing at
        /// shaped (presentation form) glyphs render as black squares because the
        /// dynamic atlas has never been asked to render those glyphs.
        private void PopulateAtlasForHits()
        {
            if (_fontAsset == null) return;
            var wanted = new HashSet<uint>();
            void AddGid(uint gid)
            {
                if (gid == 0) return;
                if (_gidToCps != null && _gidToCps.TryGetValue(gid, out var cps))
                    foreach (var cp in cps) wanted.Add(cp);
            }

            foreach (var h in _charHits) wanted.Add(h.unicode);
            foreach (var h in _ligHits)
            {
                AddGid(h.ligatureGlyphID);
                if (h.componentGlyphIDs != null)
                    foreach (var c in h.componentGlyphIDs) AddGid(c);
            }
            foreach (var h in _kernHits) { AddGid(h.firstGlyph); AddGid(h.secondGlyph); }
            foreach (var h in _m2bHits) { AddGid(h.baseGlyph); AddGid(h.markGlyph); }
            foreach (var h in _m2mHits) { AddGid(h.baseGlyph); AddGid(h.combiningGlyph); }

            if (wanted.Count == 0) return;

            var arr = new uint[wanted.Count];
            int i = 0; foreach (var cp in wanted) arr[i++] = cp;
            try
            {
                _fontAsset.TryAddCharacters(arr, out _);
                _glyphLookup = null;
                EditorUtility.SetDirty(_fontAsset);
            }
            catch { /* best-effort */ }
        }

        private bool TryResolveQuery(out uint codepoint, out uint glyphId, out string note)
            => TryResolveQueryGeneric(_query, _mode, _decimal, out codepoint, out glyphId, out note);

        /// Resolve the secondary filter input to (codepoint, glyphID), going through
        /// the same parsing as the main query plus the same reverse-map bidirectional
        /// lookup. Called every OnGUI so filter changes apply live without re-search.
        private void ResolveFilter()
        {
            _filterActive = false;
            _filterCp = 0; _filterGid = 0; _filterNote = "";
            if (string.IsNullOrEmpty(_filter) || _fontAsset == null) return;

            if (!TryResolveQueryGeneric(_filter, _filterMode, _filterDecimal,
                    out _filterCp, out _filterGid, out _filterNote))
                return;

            if (_filterGid == 0 && _filterCp != 0)
            {
                foreach (var c in _fontAsset.characterTable)
                    if (c.unicode == _filterCp) { _filterGid = c.glyphIndex; break; }
                if (_filterGid == 0 && _gidToCps != null)
                    foreach (var kv in _gidToCps)
                        if (kv.Value.Contains(_filterCp)) { _filterGid = kv.Key; break; }
            }
            if (_filterCp == 0 && _filterGid != 0 && _gidToCps != null
                && _gidToCps.TryGetValue(_filterGid, out var fcps) && fcps.Count > 0)
                _filterCp = fcps[0];

            if (_filterGid != 0 || _filterCp != 0) _filterActive = true;
            else _filterNote = "filter did not resolve to a known glyph";
        }

        // Per-hit-type predicates. Returning true means "keep this row."
        private bool Keep(CharacterHit h) => !_filterActive
            || (_filterCp != 0 && h.unicode == _filterCp)
            || (_filterGid != 0 && h.glyphIndex == _filterGid);

        private bool Keep(GlyphHit h) => !_filterActive
            || (_filterGid != 0 && h.index == _filterGid);

        private bool Keep(LigatureHit h)
        {
            if (!_filterActive) return true;
            if (_filterGid == 0) return false;
            if (h.ligatureGlyphID == _filterGid) return true;
            if (h.componentGlyphIDs != null)
                foreach (var c in h.componentGlyphIDs) if (c == _filterGid) return true;
            return false;
        }

        private bool Keep(KerningHit h) => !_filterActive
            || (_filterGid != 0 && (h.firstGlyph == _filterGid || h.secondGlyph == _filterGid));

        private bool Keep(MarkBaseHit h) => !_filterActive
            || (_filterGid != 0 && (h.baseGlyph == _filterGid || h.markGlyph == _filterGid));

        private bool Keep(MarkMarkHit h) => !_filterActive
            || (_filterGid != 0 && (h.baseGlyph == _filterGid || h.combiningGlyph == _filterGid));

        private static bool TryResolveQueryGeneric(string query, QueryMode origMode, bool asDecimal,
            out uint codepoint, out uint glyphId, out string note)
        {
            codepoint = 0; glyphId = 0; note = "";
            var q = (query ?? "").Trim();
            var mode = origMode;

            if (mode == QueryMode.Auto)
            {
                if (q.Length == 1) mode = QueryMode.Char;
                else if (q.StartsWith("U+", StringComparison.OrdinalIgnoreCase) ||
                         q.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    mode = QueryMode.Codepoint;
                else if (IsHexLike(q)) mode = QueryMode.Codepoint;
                else mode = QueryMode.GlyphId;
            }

            switch (mode)
            {
                case QueryMode.Char:
                    if (q.Length == 0) { note = "empty char"; return false; }
                    codepoint = (uint)char.ConvertToUtf32(q, 0);
                    return true;

                case QueryMode.Codepoint:
                {
                    var s = q;
                    if (s.StartsWith("U+", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
                    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
                    var style = asDecimal ? NumberStyles.Integer : NumberStyles.HexNumber;
                    if (!uint.TryParse(s, style, CultureInfo.InvariantCulture, out codepoint))
                    { note = $"could not parse '{q}' as { (asDecimal ? "decimal" : "hex") } codepoint"; return false; }
                    return true;
                }

                case QueryMode.GlyphId:
                {
                    var style = q.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? NumberStyles.HexNumber
                        : NumberStyles.Integer;
                    var s = style == NumberStyles.HexNumber ? q.Substring(2) : q;
                    if (!uint.TryParse(s, style, CultureInfo.InvariantCulture, out glyphId))
                    { note = $"could not parse '{q}' as glyph id"; return false; }
                    return true;
                }
            }
            note = "unhandled mode";
            return false;
        }

        private static bool IsHexLike(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var c in s) if (!Uri.IsHexDigit(c)) return false;
            return s.Length >= 3;
        }

        private void CollectHits()
        {
            // Character Table
            foreach (var c in _fontAsset.characterTable)
            {
                if ((_resolvedCp != 0 && c.unicode == _resolvedCp) ||
                    (_resolvedGid != 0 && c.glyphIndex == _resolvedGid))
                {
                    _charHits.Add(new CharacterHit { unicode = c.unicode, glyphIndex = c.glyphIndex, scale = c.scale });
                }
            }

            // Glyph Table
            if (_resolvedGid != 0)
            {
                foreach (var g in _fontAsset.glyphTable)
                {
                    if (g.index == _resolvedGid)
                    {
                        var m = g.metrics; var r = g.glyphRect;
                        _glyphHits.Add(new GlyphHit
                        {
                            index = g.index, atlasIndex = g.atlasIndex, scale = g.scale,
                            w = m.width, h = m.height, bx = m.horizontalBearingX, by = m.horizontalBearingY, adv = m.horizontalAdvance,
                            rx = r.x, ry = r.y, rw = r.width, rh = r.height,
                        });
                    }
                }
            }

            if (_resolvedGid == 0) return;

            // Ligature Substitution
            var ligs = _fontAsset.fontFeatureTable?.ligatureRecords;
            if (ligs != null)
            {
                foreach (var lig in ligs)
                {
                    bool isProduct = lig.ligatureGlyphID == _resolvedGid;
                    bool isComponent = false;
                    if (lig.componentGlyphIDs != null)
                        foreach (var c in lig.componentGlyphIDs)
                            if (c == _resolvedGid) { isComponent = true; break; }
                    if (!isProduct && !isComponent) continue;
                    _ligHits.Add(new LigatureHit
                    {
                        role = isProduct && isComponent ? "PRODUCT+COMPONENT" : isProduct ? "PRODUCT" : "COMPONENT",
                        componentGlyphIDs = lig.componentGlyphIDs,
                        ligatureGlyphID = lig.ligatureGlyphID,
                    });
                }
            }

            // Pair Adjustment
            var pairs = _fontAsset.fontFeatureTable?.glyphPairAdjustmentRecords;
            if (pairs != null)
            {
                foreach (var p in pairs)
                {
                    var a = p.firstAdjustmentRecord.glyphIndex;
                    var b = p.secondAdjustmentRecord.glyphIndex;
                    if (a != _resolvedGid && b != _resolvedGid) continue;
                    var fr = p.firstAdjustmentRecord.glyphValueRecord;
                    var sr = p.secondAdjustmentRecord.glyphValueRecord;
                    _kernHits.Add(new KerningHit
                    {
                        role = a == _resolvedGid && b == _resolvedGid ? "FIRST+SECOND" : a == _resolvedGid ? "FIRST" : "SECOND",
                        firstGlyph = a, secondGlyph = b,
                        fxPlc = fr.xPlacement, fyPlc = fr.yPlacement, fxAdv = fr.xAdvance, fyAdv = fr.yAdvance,
                        sxPlc = sr.xPlacement, syPlc = sr.yPlacement, sxAdv = sr.xAdvance, syAdv = sr.yAdvance,
                    });
                }
            }

            // Mark-to-Base
            var m2b = _fontAsset.fontFeatureTable?.MarkToBaseAdjustmentRecords;
            if (m2b != null)
            {
                foreach (var rec in m2b)
                {
                    if (rec.baseGlyphID != _resolvedGid && rec.markGlyphID != _resolvedGid) continue;
                    var bp = rec.baseGlyphAnchorPoint;
                    var mp = rec.markPositionAdjustment;
                    _m2bHits.Add(new MarkBaseHit
                    {
                        role = rec.baseGlyphID == _resolvedGid && rec.markGlyphID == _resolvedGid ? "BASE+MARK"
                             : rec.baseGlyphID == _resolvedGid ? "BASE" : "MARK",
                        baseGlyph = rec.baseGlyphID, markGlyph = rec.markGlyphID,
                        baseAx = bp.xCoordinate, baseAy = bp.yCoordinate,
                        markOx = mp.xPositionAdjustment, markOy = mp.yPositionAdjustment,
                    });
                }
            }

            // Mark-to-Mark
            var m2m = _fontAsset.fontFeatureTable?.MarkToMarkAdjustmentRecords;
            if (m2m != null)
            {
                foreach (var rec in m2m)
                {
                    if (rec.baseMarkGlyphID != _resolvedGid && rec.combiningMarkGlyphID != _resolvedGid) continue;
                    var bp = rec.baseMarkGlyphAnchorPoint;
                    var mp = rec.combiningMarkPositionAdjustment;
                    _m2mHits.Add(new MarkMarkHit
                    {
                        role = rec.baseMarkGlyphID == _resolvedGid && rec.combiningMarkGlyphID == _resolvedGid ? "BASE+COMBINING"
                             : rec.baseMarkGlyphID == _resolvedGid ? "BASE" : "COMBINING",
                        baseGlyph = rec.baseMarkGlyphID, combiningGlyph = rec.combiningMarkGlyphID,
                        baseAx = bp.xCoordinate, baseAy = bp.yCoordinate,
                        combOx = mp.xPositionAdjustment, combOy = mp.yPositionAdjustment,
                    });
                }
            }
        }

        // ---------- reverse map (FontEngine) ----------

        private void InvalidateReverseMap()
        {
            _cachedMapFont = null;
            _gidToCps = null;
            _mapBuildNote = "";
        }

        private void EnsureReverseMap()
        {
            if (_fontAsset == null) return;
            if (_cachedMapFont == _fontAsset && _gidToCps != null) return;

            _gidToCps = new Dictionary<uint, List<uint>>(2048);

            // Seed from the character table first (cheap, always available).
            foreach (var c in _fontAsset.characterTable)
                Add(_gidToCps, c.glyphIndex, c.unicode);

            // Augment with FontEngine cmap queries across Arabic ranges. This is what
            // lets us reverse-resolve shaped (presentation form) glyph IDs that the
            // character table doesn't contain.
            int extra = 0;
            string fontEngineNote = "";
            var srcFont = _fontAsset.sourceFontFile;
            if (srcFont == null)
            {
                fontEngineNote = "source font file not assigned — reverse map limited to character table";
            }
            else
            {
                FontEngineError err;
                try { err = FontEngine.LoadFontFace(srcFont); }
                catch (Exception ex) { err = FontEngineError.Invalid_Face; fontEngineNote = ex.Message; }

                if (err != FontEngineError.Success)
                {
                    if (string.IsNullOrEmpty(fontEngineNote))
                        fontEngineNote = $"FontEngine.LoadFontFace failed: {err}";
                }
                else
                {
                    foreach (var (start, end, _) in ReverseMapRanges)
                    {
                        for (uint cp = start; cp <= end; cp++)
                        {
                            uint gid = 0;
                            try { FontEngine.TryGetGlyphIndex(cp, out gid); }
                            catch { gid = 0; }
                            if (gid == 0) continue;
                            if (Add(_gidToCps, gid, cp)) extra++;
                        }
                    }
                }
            }

            _cachedMapFont = _fontAsset;
            _mapBuildNote = string.IsNullOrEmpty(fontEngineNote)
                ? $"reverse map: {_gidToCps.Count} glyph IDs   (+{extra} added via FontEngine)"
                : $"reverse map: {_gidToCps.Count} glyph IDs   (FontEngine: {fontEngineNote})";
        }

        private static bool Add(Dictionary<uint, List<uint>> map, uint gid, uint cp)
        {
            if (!map.TryGetValue(gid, out var list))
            { list = new List<uint>(1); map[gid] = list; }
            if (list.Contains(cp)) return false;
            list.Add(cp);
            return true;
        }

        // ---------- formatting helpers ----------

        private static string Cp(uint cp)
        {
            try { return char.ConvertFromUtf32((int)cp); }
            catch { return "?"; }
        }

        /// Rich label for a glyph ID, used in labels alongside chips:
        ///   "'ﺑ' U+FE91 ARABIC LETTER BEH INITIAL FORM (glyph 1556)"
        /// Falls back to "glyph N" when there's no reverse codepoint mapping.
        private string GlyphLabel(uint gid)
        {
            if (gid == 0) return ".notdef (glyph 0)";
            if (_gidToCps != null && _gidToCps.TryGetValue(gid, out var cps) && cps.Count > 0)
            {
                var primary = cps[0];
                var s = new System.Text.StringBuilder();
                s.Append($"'{Cp(primary)}' U+{primary:X4}");
                var name = UnicodeName(primary);
                if (!string.IsNullOrEmpty(name)) s.Append(' ').Append(name);
                for (int i = 1; i < cps.Count; i++) s.Append($" / U+{cps[i]:X4}");
                s.Append($" (glyph {gid})");
                return s.ToString();
            }
            return $"glyph {gid}";
        }

        /// Curated Unicode name lookup for the codepoints most relevant to Arabic text
        /// rendering: ASCII, the Arabic block (base letters + harakat), and a generic
        /// label for the Presentation Forms ranges. Unicode doesn't expose names via
        /// the standard library so we hand-roll just what we need.
        private static string UnicodeName(uint cp)
        {
            if (cp < 0x80)
            {
                if (cp >= 'A' && cp <= 'Z') return $"LATIN CAPITAL LETTER {(char)cp}";
                if (cp >= 'a' && cp <= 'z') return $"LATIN SMALL LETTER {(char)('A' + cp - 'a')}";
                if (cp >= '0' && cp <= '9') return $"DIGIT {(char)cp}";
                if (cp == ' ') return "SPACE";
                return "";
            }
            if (_arabicNames.TryGetValue(cp, out var n)) return n;
            if (cp >= 0xFB50 && cp <= 0xFDFF) return $"ARABIC PRES.FORM-A";
            if (cp >= 0xFE70 && cp <= 0xFEFF) return $"ARABIC PRES.FORM-B";
            if (cp >= 0x0600 && cp <= 0x06FF) return "ARABIC";
            return "";
        }

        // Curated Arabic codepoint names. Covers the base 28 letters, the eight common
        // harakat, hamza variants, alef variants, tatweel, ta marbuta, alef maksura,
        // and the ZWJ/ZWNJ shaping controls that come up when debugging RTLTMPro.
        private static readonly Dictionary<uint, string> _arabicNames = new()
        {
            // hamza + alef family
            { 0x0621, "ARABIC LETTER HAMZA" },
            { 0x0622, "ARABIC LETTER ALEF WITH MADDA ABOVE" },
            { 0x0623, "ARABIC LETTER ALEF WITH HAMZA ABOVE" },
            { 0x0624, "ARABIC LETTER WAW WITH HAMZA ABOVE" },
            { 0x0625, "ARABIC LETTER ALEF WITH HAMZA BELOW" },
            { 0x0626, "ARABIC LETTER YEH WITH HAMZA ABOVE" },
            // 28 base letters
            { 0x0627, "ARABIC LETTER ALEF" },
            { 0x0628, "ARABIC LETTER BEH" },
            { 0x0629, "ARABIC LETTER TEH MARBUTA" },
            { 0x062A, "ARABIC LETTER TEH" },
            { 0x062B, "ARABIC LETTER THEH" },
            { 0x062C, "ARABIC LETTER JEEM" },
            { 0x062D, "ARABIC LETTER HAH" },
            { 0x062E, "ARABIC LETTER KHAH" },
            { 0x062F, "ARABIC LETTER DAL" },
            { 0x0630, "ARABIC LETTER THAL" },
            { 0x0631, "ARABIC LETTER REH" },
            { 0x0632, "ARABIC LETTER ZAIN" },
            { 0x0633, "ARABIC LETTER SEEN" },
            { 0x0634, "ARABIC LETTER SHEEN" },
            { 0x0635, "ARABIC LETTER SAD" },
            { 0x0636, "ARABIC LETTER DAD" },
            { 0x0637, "ARABIC LETTER TAH" },
            { 0x0638, "ARABIC LETTER ZAH" },
            { 0x0639, "ARABIC LETTER AIN" },
            { 0x063A, "ARABIC LETTER GHAIN" },
            { 0x0640, "ARABIC TATWEEL" },
            { 0x0641, "ARABIC LETTER FEH" },
            { 0x0642, "ARABIC LETTER QAF" },
            { 0x0643, "ARABIC LETTER KAF" },
            { 0x0644, "ARABIC LETTER LAM" },
            { 0x0645, "ARABIC LETTER MEEM" },
            { 0x0646, "ARABIC LETTER NOON" },
            { 0x0647, "ARABIC LETTER HEH" },
            { 0x0648, "ARABIC LETTER WAW" },
            { 0x0649, "ARABIC LETTER ALEF MAKSURA" },
            { 0x064A, "ARABIC LETTER YEH" },
            // harakat
            { 0x064B, "ARABIC FATHATAN" },
            { 0x064C, "ARABIC DAMMATAN" },
            { 0x064D, "ARABIC KASRATAN" },
            { 0x064E, "ARABIC FATHA" },
            { 0x064F, "ARABIC DAMMA" },
            { 0x0650, "ARABIC KASRA" },
            { 0x0651, "ARABIC SHADDA" },
            { 0x0652, "ARABIC SUKUN" },
            { 0x0653, "ARABIC MADDAH ABOVE" },
            { 0x0654, "ARABIC HAMZA ABOVE" },
            { 0x0655, "ARABIC HAMZA BELOW" },
            { 0x0656, "ARABIC SUBSCRIPT ALEF" },
            { 0x0670, "ARABIC LETTER SUPERSCRIPT ALEF" },
            // RTL / shaping controls
            { 0x200C, "ZERO WIDTH NON-JOINER" },
            { 0x200D, "ZERO WIDTH JOINER" },
            { 0x200E, "LEFT-TO-RIGHT MARK" },
            { 0x200F, "RIGHT-TO-LEFT MARK" },
            { 0x202B, "RIGHT-TO-LEFT EMBEDDING" },
            { 0x202C, "POP DIRECTIONAL FORMATTING" },
        };
    }
}
