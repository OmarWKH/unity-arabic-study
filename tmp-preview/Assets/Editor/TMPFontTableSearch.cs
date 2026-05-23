using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;

namespace ArabicStudy.Editor
{
    /// EditorWindow for inspecting a TMP font asset by glyph.
    ///
    /// Accepts input as:
    ///   - A literal character pasted into the field (first codepoint is taken)
    ///   - A Unicode codepoint: "U+0628", "0x628", "0628" (hex), or "1576" (dec, must tick "Decimal")
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
    /// Codepoints printed elsewhere are looked up via a reverse glyph-to-codepoint map
    /// built from the character table, so you can read a ligature's components in plain
    /// Arabic instead of raw glyph IDs.
    public sealed class TMPFontTableSearch : EditorWindow
    {
        private enum QueryMode { Auto, Codepoint, GlyphId, Char }

        private TMP_FontAsset _fontAsset;
        private string _query = "";
        private QueryMode _mode = QueryMode.Auto;
        private bool _decimal;
        private Vector2 _scroll;
        private string _output = "";

        [MenuItem("Arabic Study/Font Table Search")]
        public static void Open()
        {
            var win = GetWindow<TMPFontTableSearch>("TMP Font Table Search");
            win.minSize = new Vector2(520, 400);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("TMP Font Table Search", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            _fontAsset = (TMP_FontAsset)EditorGUILayout.ObjectField(
                "Font Asset", _fontAsset, typeof(TMP_FontAsset), false);

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
                if (GUILayout.Button("Search", GUILayout.Width(80)))
                {
                    _output = Search();
                }
            }

            EditorGUILayout.HelpBox(
                "Examples:  ب   |   U+0628   |   0628   |   0x628   |   12345 (glyph id)\n" +
                "Mode = Auto picks Char for length-1 input, Codepoint for U+/0x/hex, GlyphId otherwise.",
                MessageType.None);

            EditorGUILayout.Space(4);
            using (var s = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = s.scrollPosition;
                EditorGUILayout.TextArea(_output, GUILayout.ExpandHeight(true));
            }
        }

        private string Search()
        {
            if (_fontAsset == null) return "(no font asset assigned)";
            if (string.IsNullOrEmpty(_query)) return "(empty query)";

            if (!TryResolveQuery(out var codepoint, out var glyphId, out var resolveNote))
                return resolveNote;

            // If we have a codepoint but not a glyph id, look one up from the character table.
            // If we have a glyph id but not a codepoint, attempt to find a codepoint that maps to it.
            var revMap = BuildGlyphToCodepointMap();

            if (glyphId == 0 && codepoint != 0)
            {
                foreach (var c in _fontAsset.characterTable)
                    if (c.unicode == codepoint) { glyphId = c.glyphIndex; break; }
            }
            if (codepoint == 0 && glyphId != 0 && revMap.TryGetValue(glyphId, out var cp))
                codepoint = cp;

            var sb = new StringBuilder();
            sb.Append("Resolved: ");
            if (codepoint != 0) sb.Append($"codepoint U+{codepoint:X4} ('{Cp(codepoint)}')  ");
            sb.Append($"glyphID {glyphId}");
            sb.AppendLine();
            sb.AppendLine($"Font: {_fontAsset.name}");
            sb.AppendLine(new string('-', 60));

            ReportCharacterTable(sb, codepoint, glyphId);
            ReportGlyphTable(sb, glyphId);
            ReportLigatures(sb, glyphId, revMap);
            ReportGlyphPairAdjustments(sb, glyphId, revMap);
            ReportMarkToBase(sb, glyphId, revMap);
            ReportMarkToMark(sb, glyphId, revMap);

            return sb.ToString();
        }

        // ---------- query resolution ----------

        private bool TryResolveQuery(out uint codepoint, out uint glyphId, out string note)
        {
            codepoint = 0;
            glyphId = 0;
            note = "";
            var q = _query.Trim();
            var mode = _mode;

            if (mode == QueryMode.Auto)
            {
                if (q.Length == 1) mode = QueryMode.Char;
                else if (q.StartsWith("U+", System.StringComparison.OrdinalIgnoreCase) ||
                         q.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
                    mode = QueryMode.Codepoint;
                else if (IsHexLike(q)) mode = QueryMode.Codepoint;
                else mode = QueryMode.GlyphId;
            }

            switch (mode)
            {
                case QueryMode.Char:
                    if (q.Length == 0) { note = "empty char"; return false; }
                    codepoint = char.ConvertToUtf32(q, 0) > 0 ? (uint)char.ConvertToUtf32(q, 0) : q[0];
                    return true;

                case QueryMode.Codepoint:
                    {
                        var s = q;
                        if (s.StartsWith("U+", System.StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
                        if (s.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
                        var style = _decimal ? NumberStyles.Integer : NumberStyles.HexNumber;
                        if (!uint.TryParse(s, style, CultureInfo.InvariantCulture, out codepoint))
                        { note = $"could not parse '{q}' as { (_decimal?"decimal":"hex") } codepoint"; return false; }
                        return true;
                    }

                case QueryMode.GlyphId:
                    {
                        var style = q.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)
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
            foreach (var c in s)
                if (!Uri.IsHexDigit(c)) return false;
            return s.Length >= 3; // 1-2 digits could be glyph id; 3+ hex more likely codepoint
        }

        private Dictionary<uint, uint> BuildGlyphToCodepointMap()
        {
            var m = new Dictionary<uint, uint>(_fontAsset.characterTable.Count);
            foreach (var c in _fontAsset.characterTable)
                if (!m.ContainsKey(c.glyphIndex)) m[c.glyphIndex] = c.unicode;
            return m;
        }

        // ---------- reports ----------

        private void ReportCharacterTable(StringBuilder sb, uint codepoint, uint glyphId)
        {
            sb.AppendLine("[Character Table]");
            int hits = 0;
            foreach (var c in _fontAsset.characterTable)
            {
                if ((codepoint != 0 && c.unicode == codepoint) ||
                    (glyphId != 0 && c.glyphIndex == glyphId))
                {
                    sb.AppendLine($"  U+{c.unicode:X4} ('{Cp(c.unicode)}')  →  glyph {c.glyphIndex}  scale={c.scale}");
                    hits++;
                }
            }
            if (hits == 0) sb.AppendLine("  (no match)");
            sb.AppendLine();
        }

        private void ReportGlyphTable(StringBuilder sb, uint glyphId)
        {
            sb.AppendLine("[Glyph Table]");
            if (glyphId == 0) { sb.AppendLine("  (no glyph id resolved)"); sb.AppendLine(); return; }
            int hits = 0;
            foreach (var g in _fontAsset.glyphTable)
            {
                if (g.index == glyphId)
                {
                    var m = g.metrics;
                    var r = g.glyphRect;
                    sb.AppendLine($"  glyph {g.index}  atlas#{g.atlasIndex}  scale={g.scale}");
                    sb.AppendLine($"    metrics  w={m.width} h={m.height} bx={m.horizontalBearingX} by={m.horizontalBearingY} adv={m.horizontalAdvance}");
                    sb.AppendLine($"    rect     x={r.x} y={r.y} w={r.width} h={r.height}");
                    hits++;
                }
            }
            if (hits == 0) sb.AppendLine("  (glyph id not in glyphTable — dynamic atlas may not yet have it)");
            sb.AppendLine();
        }

        private void ReportLigatures(StringBuilder sb, uint glyphId, Dictionary<uint, uint> revMap)
        {
            sb.AppendLine("[Ligature Substitution Records]");
            if (glyphId == 0) { sb.AppendLine("  (no glyph id resolved)"); sb.AppendLine(); return; }

            var records = _fontAsset.fontFeatureTable?.ligatureSubstitutionRecords;
            if (records == null || records.Count == 0)
            { sb.AppendLine("  (table empty or not present)"); sb.AppendLine(); return; }

            int hits = 0;
            foreach (var lig in records)
            {
                bool isProduct = lig.ligatureGlyphID == glyphId;
                bool isComponent = false;
                if (lig.componentGlyphIDs != null)
                    foreach (var c in lig.componentGlyphIDs)
                        if (c == glyphId) { isComponent = true; break; }
                if (!isProduct && !isComponent) continue;

                var role = isProduct && isComponent ? "PRODUCT+COMPONENT"
                         : isProduct ? "PRODUCT"
                         : "COMPONENT";
                sb.Append($"  [{role}] ");
                sb.Append("components=[");
                if (lig.componentGlyphIDs != null)
                {
                    for (int i = 0; i < lig.componentGlyphIDs.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        var gid = lig.componentGlyphIDs[i];
                        sb.Append(GlyphLabel(gid, revMap));
                    }
                }
                sb.Append("]  →  ");
                sb.Append(GlyphLabel(lig.ligatureGlyphID, revMap));
                sb.AppendLine();
                hits++;
            }
            if (hits == 0) sb.AppendLine("  (no match)");
            sb.AppendLine();
        }

        private void ReportGlyphPairAdjustments(StringBuilder sb, uint glyphId, Dictionary<uint, uint> revMap)
        {
            sb.AppendLine("[Glyph Pair Adjustment Records (kerning)]");
            if (glyphId == 0) { sb.AppendLine("  (no glyph id resolved)"); sb.AppendLine(); return; }

            var records = _fontAsset.fontFeatureTable?.glyphPairAdjustmentRecords;
            if (records == null || records.Count == 0)
            { sb.AppendLine("  (table empty or not present)"); sb.AppendLine(); return; }

            int hits = 0;
            foreach (var pair in records)
            {
                var a = pair.firstAdjustmentRecord.glyphIndex;
                var b = pair.secondAdjustmentRecord.glyphIndex;
                if (a != glyphId && b != glyphId) continue;

                var role = (a == glyphId && b == glyphId) ? "FIRST+SECOND"
                         : (a == glyphId) ? "FIRST" : "SECOND";
                var fr = pair.firstAdjustmentRecord.glyphValueRecord;
                var sr = pair.secondAdjustmentRecord.glyphValueRecord;
                sb.AppendLine(
                    $"  [{role}] {GlyphLabel(a, revMap)} , {GlyphLabel(b, revMap)}\n" +
                    $"        firstAdj  xPlc={fr.xPlacement} yPlc={fr.yPlacement} xAdv={fr.xAdvance} yAdv={fr.yAdvance}\n" +
                    $"        secondAdj xPlc={sr.xPlacement} yPlc={sr.yPlacement} xAdv={sr.xAdvance} yAdv={sr.yAdvance}");
                hits++;
            }
            if (hits == 0) sb.AppendLine("  (no match)");
            sb.AppendLine();
        }

        private void ReportMarkToBase(StringBuilder sb, uint glyphId, Dictionary<uint, uint> revMap)
        {
            sb.AppendLine("[Mark-to-Base Adjustment Records]");
            if (glyphId == 0) { sb.AppendLine("  (no glyph id resolved)"); sb.AppendLine(); return; }

            var records = _fontAsset.fontFeatureTable?.markToBaseAdjustmentRecords;
            if (records == null || records.Count == 0)
            { sb.AppendLine("  (table empty or not present)"); sb.AppendLine(); return; }

            int hits = 0;
            foreach (var rec in records)
            {
                if (rec.baseGlyphID != glyphId && rec.markGlyphID != glyphId) continue;
                var role = rec.baseGlyphID == glyphId && rec.markGlyphID == glyphId ? "BASE+MARK"
                         : rec.baseGlyphID == glyphId ? "BASE" : "MARK";
                var bp = rec.baseGlyphAnchorPoint;
                var mp = rec.markPositionAdjustment;
                sb.AppendLine(
                    $"  [{role}] base={GlyphLabel(rec.baseGlyphID, revMap)}  mark={GlyphLabel(rec.markGlyphID, revMap)}\n" +
                    $"        baseAnchor  x={bp.xCoordinate} y={bp.yCoordinate}\n" +
                    $"        markOffset  x={mp.xPositionAdjustment} y={mp.yPositionAdjustment}");
                hits++;
            }
            if (hits == 0) sb.AppendLine("  (no match)");
            sb.AppendLine();
        }

        private void ReportMarkToMark(StringBuilder sb, uint glyphId, Dictionary<uint, uint> revMap)
        {
            sb.AppendLine("[Mark-to-Mark Adjustment Records]");
            if (glyphId == 0) { sb.AppendLine("  (no glyph id resolved)"); sb.AppendLine(); return; }

            var records = _fontAsset.fontFeatureTable?.markToMarkAdjustmentRecords;
            if (records == null || records.Count == 0)
            { sb.AppendLine("  (table empty or not present)"); sb.AppendLine(); return; }

            int hits = 0;
            foreach (var rec in records)
            {
                if (rec.baseMarkGlyphID != glyphId && rec.combiningMarkGlyphID != glyphId) continue;
                var role = rec.baseMarkGlyphID == glyphId && rec.combiningMarkGlyphID == glyphId ? "BASE+COMBINING"
                         : rec.baseMarkGlyphID == glyphId ? "BASE" : "COMBINING";
                var bp = rec.baseMarkGlyphAnchorPoint;
                var mp = rec.combiningMarkPositionAdjustment;
                sb.AppendLine(
                    $"  [{role}] baseMark={GlyphLabel(rec.baseMarkGlyphID, revMap)}  combining={GlyphLabel(rec.combiningMarkGlyphID, revMap)}\n" +
                    $"        baseAnchor  x={bp.xCoordinate} y={bp.yCoordinate}\n" +
                    $"        combOffset  x={mp.xPositionAdjustment} y={mp.yPositionAdjustment}");
                hits++;
            }
            if (hits == 0) sb.AppendLine("  (no match)");
            sb.AppendLine();
        }

        // ---------- formatting helpers ----------

        private static string Cp(uint cp)
        {
            try { return char.ConvertFromUtf32((int)cp); }
            catch { return "?"; }
        }

        private static string GlyphLabel(uint gid, Dictionary<uint, uint> revMap)
        {
            if (revMap != null && revMap.TryGetValue(gid, out var cp))
                return $"glyph {gid} (U+{cp:X4} '{Cp(cp)}')";
            return $"glyph {gid}";
        }
    }
}
