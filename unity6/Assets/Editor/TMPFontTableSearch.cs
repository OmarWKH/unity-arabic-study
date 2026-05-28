using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEditor;
using UnityEngine;

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

        // Visual chip rendering and font-asset views.
        private float _chipSize = 56f;
        private readonly TMPFontAssetView _view = new();

        // GUIStyle that uses a label font with reasonable Unicode coverage.
        private GUIStyle _rowStyle;

        // ---------- menu / lifecycle ----------

        [MenuItem("Arabic Study/Font Table Search")]
        public static void Open()
        {
            var win = GetWindow<TMPFontTableSearch>("TMP Font Table Search");
            win.minSize = new Vector2(640, 480);
        }

        /// Cross-window entry point: opens or focuses the search window with the
        /// given font asset and codepoint pre-loaded as the query, runs the
        /// search, and shows the result. Used by the RTLTMPro Debugger to let
        /// you click through to "what feature entries apply to this codepoint?".
        public static void OpenWith(TMP_FontAsset fontAsset, uint codepoint)
        {
            var win = GetWindow<TMPFontTableSearch>("TMP Font Table Search");
            win.minSize = new Vector2(640, 480);
            if (fontAsset != null) win._fontAsset = fontAsset;
            win._view.FontAsset = win._fontAsset;
            win._query = $"U+{codepoint:X4}";
            win._mode = QueryMode.Codepoint;
            win.RunSearch();
            win.Focus();
            win.Repaint();
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
                if (c.changed) _view.FontAsset = _fontAsset;
            }
            if (_view.FontAsset != _fontAsset) _view.FontAsset = _fontAsset;

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
                if (GUILayout.Button("Rebuild Map", GUILayout.Width(110))) { _view.InvalidateReverseMap(); _view.EnsureReverseMap(); }
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
                    $"filtering by  '{TMPFontAssetView.CodepointAsString(_filterCp)}'  U+{_filterCp:X4}{(string.IsNullOrEmpty(ArabicNames.UnicodeName(_filterCp)) ? "" : "  " + ArabicNames.UnicodeName(_filterCp))}  (glyph {_filterGid})",
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
                    if (_resolvedGid != 0) _view.DrawGlyphChip(_resolvedGid, Mathf.Max(_chipSize, 64f));
                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField(
                            $"codepoint  {(_resolvedCp != 0 ? $"U+{_resolvedCp:X4}  '{TMPFontAssetView.CodepointAsString(_resolvedCp)}'" : "—")}",
                            _rowStyle);
                        EditorGUILayout.LabelField(
                            $"glyph ID   {(_resolvedGid != 0 ? _resolvedGid.ToString() : "—")}",
                            _rowStyle);
                        if (!string.IsNullOrEmpty(_view.MapBuildNote))
                            EditorGUILayout.LabelField(_view.MapBuildNote, EditorStyles.miniLabel);
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        // ---------- chip rendering ----------

        private void DrawGlyphChipPair(uint a, uint b, float size, string separator = " + ")
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _view.DrawGlyphChip(a, size);
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(20)))
                {
                    GUILayout.Space(size * 0.5f - 8f);
                    EditorGUILayout.LabelField(separator, GUILayout.Width(20));
                }
                _view.DrawGlyphChip(b, size);
                GUILayout.FlexibleSpace();
            }
        }

        // ---------- row renderers ----------

        private void DrawCharacterRow(CharacterHit h)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _view.DrawGlyphChip(h.glyphIndex, _chipSize);
                using (new EditorGUILayout.VerticalScope())
                {
                    var name = ArabicNames.UnicodeName(h.unicode);
                    EditorGUILayout.LabelField(
                        $"'{TMPFontAssetView.CodepointAsString(h.unicode)}' U+{h.unicode:X4}{(string.IsNullOrEmpty(name) ? "" : "  " + name)}",
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
                _view.DrawGlyphChip(h.index, _chipSize);
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
                        compLine.Append(_view.GlyphLabel(h.componentGlyphIDs[i]));
                    }
                EditorGUILayout.LabelField($"[{h.role}]   {compLine}   →   {_view.GlyphLabel(h.ligatureGlyphID)}", _rowStyle);
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
                            _view.DrawGlyphChip(h.componentGlyphIDs[i], _chipSize);
                        }
                    }
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(28)))
                    {
                        GUILayout.Space(_chipSize * 0.5f - 8f);
                        EditorGUILayout.LabelField("→", GUILayout.Width(28));
                    }
                    _view.DrawGlyphChip(h.ligatureGlyphID, _chipSize);
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
                    $"[{h.role}]   {_view.GlyphLabel(h.firstGlyph)}   ·   {_view.GlyphLabel(h.secondGlyph)}", _rowStyle);
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
                    $"[{h.role}]   base={_view.GlyphLabel(h.baseGlyph)}   mark={_view.GlyphLabel(h.markGlyph)}", _rowStyle);
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
                    $"[{h.role}]   baseMark={_view.GlyphLabel(h.baseGlyph)}   combining={_view.GlyphLabel(h.combiningGlyph)}", _rowStyle);
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

            _view.EnsureReverseMap();
            _view.PopulateAtlasForCodepoint(_resolvedCp); // make sure the queried glyph itself is in the atlas

            // Bidirectional resolve: codepoint ↔ glyph id (via the view's lookups).
            if (_resolvedGid == 0 && _resolvedCp != 0)
                _resolvedGid = _view.GlyphForCodepoint(_resolvedCp);
            if (_resolvedCp == 0 && _resolvedGid != 0)
            {
                var cps = _view.CodepointsForGlyph(_resolvedGid);
                if (cps.Count > 0) _resolvedCp = cps[0];
            }

            CollectHits();
            PopulateAtlasForHits();
            _hasResult = true;
        }

        /// Walk every collected hit, gather glyph IDs referenced, map them back to a
        /// representative codepoint via the reverse map, and ask the font asset to
        /// materialise them in the atlas so chips actually render.
        private void PopulateAtlasForHits()
        {
            if (_fontAsset == null) return;
            var wanted = new HashSet<uint>();
            void AddGid(uint gid)
            {
                if (gid == 0) return;
                foreach (var cp in _view.CodepointsForGlyph(gid)) wanted.Add(cp);
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

            _view.PopulateAtlasForCodepoints(wanted);
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
                _filterGid = _view.GlyphForCodepoint(_filterCp);
            if (_filterCp == 0 && _filterGid != 0)
            {
                var fcps = _view.CodepointsForGlyph(_filterGid);
                if (fcps.Count > 0) _filterCp = fcps[0];
            }

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

        // (reverse map, glyph lookup, atlas-populate, chip rendering, label
        //  formatting, and Unicode-name lookup all live on TMPFontAssetView /
        //  ArabicNames now — see those files.)
    }
}
