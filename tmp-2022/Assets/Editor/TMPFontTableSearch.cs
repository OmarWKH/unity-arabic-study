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
                DrawSection("Character Table", "char", _charHits.Count, () => { foreach (var h in Cap(_charHits)) DrawCharacterRow(h); });
                DrawSection("Glyph Table", "glyph", _glyphHits.Count, () => { foreach (var h in Cap(_glyphHits)) DrawGlyphRow(h); });
                DrawSection("Ligature Substitution Records", "lig", _ligHits.Count, () => { foreach (var h in Cap(_ligHits)) DrawLigatureRow(h); });
                DrawSection("Glyph Pair Adjustment (Kerning)", "kern", _kernHits.Count, () => { foreach (var h in Cap(_kernHits)) DrawKerningRow(h); });
                DrawSection("Mark-to-Base Adjustment", "m2b", _m2bHits.Count, () => { foreach (var h in Cap(_m2bHits)) DrawMarkBaseRow(h); });
                DrawSection("Mark-to-Mark Adjustment", "m2m", _m2mHits.Count, () => { foreach (var h in Cap(_m2mHits)) DrawMarkMarkRow(h); });
            }
        }

        private IEnumerable<T> Cap<T>(List<T> list)
        {
            int n = Mathf.Min(list.Count, MaxRowsPerSection);
            for (int i = 0; i < n; i++) yield return list[i];
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
                if (c.changed) InvalidateReverseMap();
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
                EditorGUILayout.LabelField(
                    $"codepoint  {(_resolvedCp != 0 ? $"U+{_resolvedCp:X4}  '{Cp(_resolvedCp)}'" : "—")}",
                    _rowStyle);
                EditorGUILayout.LabelField(
                    $"glyph ID   {(_resolvedGid != 0 ? _resolvedGid.ToString() : "—")}",
                    _rowStyle);
                if (!string.IsNullOrEmpty(_mapBuildNote))
                    EditorGUILayout.LabelField(_mapBuildNote, EditorStyles.miniLabel);
            }
        }

        // ---------- foldout section ----------

        private void DrawSection(string title, string id, int count, Action drawBody)
        {
            var key = id;
            if (!_foldout.TryGetValue(key, out var open)) open = count > 0 && count <= 50; // auto-open small sections
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                open = EditorGUILayout.Foldout(open, $"{title}   [{count}]", true, EditorStyles.foldoutHeader);
                _foldout[key] = open;
                if (!open) return;
                if (count == 0)
                {
                    EditorGUILayout.LabelField("  (no match)", EditorStyles.miniLabel);
                    return;
                }
                drawBody();
                if (count > MaxRowsPerSection)
                    EditorGUILayout.HelpBox(
                        $"showing first {MaxRowsPerSection} of {count} — refine your query (or use the by-pair search coming in the next pass)",
                        MessageType.Info);
            }
        }

        // ---------- row renderers ----------

        private void DrawCharacterRow(CharacterHit h)
        {
            EditorGUILayout.LabelField(
                $"U+{h.unicode:X4}  '{Cp(h.unicode)}'   →   glyph {h.glyphIndex}    scale={h.scale}",
                _rowStyle);
        }

        private void DrawGlyphRow(GlyphHit h)
        {
            EditorGUILayout.LabelField(
                $"glyph {h.index}  atlas#{h.atlasIndex}  scale={h.scale}", _rowStyle);
            EditorGUILayout.LabelField(
                $"    metrics  w={h.w} h={h.h} bx={h.bx} by={h.by} adv={h.adv}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                $"    rect     x={h.rx} y={h.ry} w={h.rw} h={h.rh}", EditorStyles.miniLabel);
        }

        private void DrawLigatureRow(LigatureHit h)
        {
            var comp = new System.Text.StringBuilder();
            if (h.componentGlyphIDs != null)
            {
                for (int i = 0; i < h.componentGlyphIDs.Length; i++)
                {
                    if (i > 0) comp.Append(" + ");
                    comp.Append(GlyphLabel(h.componentGlyphIDs[i]));
                }
            }
            EditorGUILayout.LabelField($"[{h.role}]   {comp}   →   {GlyphLabel(h.ligatureGlyphID)}", _rowStyle);
        }

        private void DrawKerningRow(KerningHit h)
        {
            EditorGUILayout.LabelField(
                $"[{h.role}]   {GlyphLabel(h.firstGlyph)} , {GlyphLabel(h.secondGlyph)}", _rowStyle);
            EditorGUILayout.LabelField(
                $"    first   xPlc={h.fxPlc}  yPlc={h.fyPlc}  xAdv={h.fxAdv}  yAdv={h.fyAdv}",
                EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                $"    second  xPlc={h.sxPlc}  yPlc={h.syPlc}  xAdv={h.sxAdv}  yAdv={h.syAdv}",
                EditorStyles.miniLabel);
        }

        private void DrawMarkBaseRow(MarkBaseHit h)
        {
            EditorGUILayout.LabelField(
                $"[{h.role}]   base={GlyphLabel(h.baseGlyph)}   mark={GlyphLabel(h.markGlyph)}", _rowStyle);
            EditorGUILayout.LabelField(
                $"    baseAnchor x={h.baseAx} y={h.baseAy}    markOffset x={h.markOx} y={h.markOy}",
                EditorStyles.miniLabel);
        }

        private void DrawMarkMarkRow(MarkMarkHit h)
        {
            EditorGUILayout.LabelField(
                $"[{h.role}]   baseMark={GlyphLabel(h.baseGlyph)}   combining={GlyphLabel(h.combiningGlyph)}", _rowStyle);
            EditorGUILayout.LabelField(
                $"    baseAnchor x={h.baseAx} y={h.baseAy}    combOffset x={h.combOx} y={h.combOy}",
                EditorStyles.miniLabel);
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
            _hasResult = true;
        }

        private bool TryResolveQuery(out uint codepoint, out uint glyphId, out string note)
        {
            codepoint = 0; glyphId = 0; note = "";
            var q = _query.Trim();
            var mode = _mode;

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
                    var style = _decimal ? NumberStyles.Integer : NumberStyles.HexNumber;
                    if (!uint.TryParse(s, style, CultureInfo.InvariantCulture, out codepoint))
                    { note = $"could not parse '{q}' as { (_decimal ? "decimal" : "hex") } codepoint"; return false; }
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

        /// Rich label for a glyph ID: "ﺑ U+FE91 (glyph 1556)" or "glyph N (no codepoint)".
        private string GlyphLabel(uint gid)
        {
            if (gid == 0) return "glyph 0";
            if (_gidToCps != null && _gidToCps.TryGetValue(gid, out var cps) && cps.Count > 0)
            {
                if (cps.Count == 1)
                    return $"'{Cp(cps[0])}' U+{cps[0]:X4} (glyph {gid})";
                var s = new System.Text.StringBuilder();
                s.Append($"'{Cp(cps[0])}' U+{cps[0]:X4}");
                for (int i = 1; i < cps.Count; i++) s.Append($" / U+{cps[i]:X4}");
                s.Append($" (glyph {gid})");
                return s.ToString();
            }
            return $"glyph {gid}";
        }
    }
}
