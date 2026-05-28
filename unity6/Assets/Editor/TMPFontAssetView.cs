using System;
using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace ArabicStudy.Editor
{
    /// Shared helper that wraps a TMP_FontAsset with the lookups and rendering
    /// the Arabic-study editor windows need:
    ///
    ///   - A glyph-id → Glyph cache rebuilt from the font asset's glyphTable.
    ///   - A glyph-id → Unicode codepoints reverse map built from the character
    ///     table seed + FontEngine cmap queries across the Arabic Unicode
    ///     ranges (so shaped presentation-form glyphs are addressable).
    ///   - A chip drawer that blits the actual glyph region from the atlas.
    ///   - A label formatter that decorates glyph IDs with codepoint + Unicode
    ///     name (curated for Arabic + harakat).
    ///   - Atlas populate helpers that ask TMP to materialise codepoints in the
    ///     dynamic atlas so chips don't come up empty.
    ///
    /// Each window owns its own instance; they're independent and safe to use
    /// in parallel against the same font asset.
    public sealed class TMPFontAssetView
    {
        private TMP_FontAsset _fontAsset;
        private Dictionary<uint, UnityEngine.TextCore.Glyph> _glyphLookup;
        private Dictionary<uint, List<uint>> _gidToCps;

        public TMP_FontAsset FontAsset
        {
            get => _fontAsset;
            set
            {
                if (_fontAsset == value) return;
                _fontAsset = value;
                _glyphLookup = null;
                _gidToCps = null;
                MapBuildNote = "";
            }
        }

        public IReadOnlyDictionary<uint, List<uint>> ReverseMap => _gidToCps;
        public string MapBuildNote { get; private set; } = "";

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

        // ---------- caches ----------

        public void EnsureGlyphLookup()
        {
            if (_fontAsset == null) { _glyphLookup = null; return; }
            if (_glyphLookup != null) return;
            _glyphLookup = new Dictionary<uint, UnityEngine.TextCore.Glyph>(_fontAsset.glyphTable.Count);
            foreach (var g in _fontAsset.glyphTable) _glyphLookup[g.index] = g;
        }

        public void EnsureReverseMap()
        {
            if (_fontAsset == null) { _gidToCps = null; return; }
            if (_gidToCps != null) return;

            _gidToCps = new Dictionary<uint, List<uint>>(2048);

            foreach (var c in _fontAsset.characterTable)
                Add(_gidToCps, c.glyphIndex, c.unicode);

            int extra = 0;
            string note = "";
            var srcFont = _fontAsset.sourceFontFile;
            if (srcFont == null)
            {
                note = "source font file not assigned — reverse map limited to character table";
            }
            else
            {
                FontEngineError err;
                try { err = FontEngine.LoadFontFace(srcFont); }
                catch (Exception ex) { err = FontEngineError.Invalid_Face; note = ex.Message; }

                if (err != FontEngineError.Success)
                {
                    if (string.IsNullOrEmpty(note)) note = $"FontEngine.LoadFontFace failed: {err}";
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

            MapBuildNote = string.IsNullOrEmpty(note)
                ? $"reverse map: {_gidToCps.Count} glyph IDs   (+{extra} added via FontEngine)"
                : $"reverse map: {_gidToCps.Count} glyph IDs   (FontEngine: {note})";
        }

        public void InvalidateGlyphLookup() => _glyphLookup = null;
        public void InvalidateReverseMap() { _gidToCps = null; MapBuildNote = ""; }

        private static bool Add(Dictionary<uint, List<uint>> map, uint gid, uint cp)
        {
            if (!map.TryGetValue(gid, out var list))
            { list = new List<uint>(1); map[gid] = list; }
            if (list.Contains(cp)) return false;
            list.Add(cp);
            return true;
        }

        // ---------- bidirectional resolve ----------

        /// Look up the glyph ID for a given codepoint, going through both the
        /// font asset's character table and the FontEngine-augmented reverse map.
        /// Returns 0 when unknown.
        public uint GlyphForCodepoint(uint cp)
        {
            if (_fontAsset == null || cp == 0) return 0;
            foreach (var c in _fontAsset.characterTable)
                if (c.unicode == cp) return c.glyphIndex;
            EnsureReverseMap();
            if (_gidToCps != null)
                foreach (var kv in _gidToCps)
                    if (kv.Value.Contains(cp)) return kv.Key;
            return 0;
        }

        public IReadOnlyList<uint> CodepointsForGlyph(uint gid)
        {
            EnsureReverseMap();
            if (_gidToCps != null && _gidToCps.TryGetValue(gid, out var list)) return list;
            return Array.Empty<uint>();
        }

        // ---------- atlas populate ----------

        /// Ask TMP to materialise a single codepoint in the dynamic atlas. Used
        /// before rendering a chip so the glyph actually shows up.
        public void PopulateAtlasForCodepoint(uint cp)
        {
            if (cp == 0 || _fontAsset == null) return;
            try
            {
                _fontAsset.TryAddCharacters(new[] { cp }, out _);
                InvalidateGlyphLookup();
                EditorUtility.SetDirty(_fontAsset);
            }
            catch { /* best-effort */ }
        }

        public void PopulateAtlasForCodepoints(IEnumerable<uint> cps)
        {
            if (_fontAsset == null || cps == null) return;
            var set = new HashSet<uint>(cps);
            set.Remove(0);
            if (set.Count == 0) return;
            var arr = new uint[set.Count];
            int i = 0; foreach (var c in set) arr[i++] = c;
            try
            {
                _fontAsset.TryAddCharacters(arr, out _);
                InvalidateGlyphLookup();
                EditorUtility.SetDirty(_fontAsset);
            }
            catch { /* best-effort */ }
        }

        // ---------- formatting ----------

        public static string CodepointAsString(uint cp)
        {
            try { return char.ConvertFromUtf32((int)cp); }
            catch { return "?"; }
        }

        /// Rich label for a glyph ID. Falls back to "glyph N" when no codepoint
        /// is known.
        public string GlyphLabel(uint gid)
        {
            if (gid == 0) return ".notdef (glyph 0)";
            EnsureReverseMap();
            if (_gidToCps != null && _gidToCps.TryGetValue(gid, out var cps) && cps.Count > 0)
            {
                var primary = cps[0];
                var s = new System.Text.StringBuilder();
                s.Append($"'{CodepointAsString(primary)}' U+{primary:X4}");
                var name = ArabicNames.UnicodeName(primary);
                if (!string.IsNullOrEmpty(name)) s.Append(' ').Append(name);
                for (int i = 1; i < cps.Count; i++) s.Append($" / U+{cps[i]:X4}");
                s.Append($" (glyph {gid})");
                return s.ToString();
            }
            return $"glyph {gid}";
        }

        // ---------- chip rendering ----------

        /// Render a single glyph as a visual chip: a `size × size` square
        /// containing the glyph blitted from the font asset's atlas, plus a
        /// short caption below it with the codepoint and glyph ID.
        public void DrawGlyphChip(uint gid, float size)
        {
            EnsureGlyphLookup();
            EnsureReverseMap();

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
                    var fallbackStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = Mathf.Max(10, (int)(size * 0.28f)),
                        normal = { textColor = new Color(0.85f, 0.55f, 0.55f) },
                    };
                    GUI.Label(rect, gid == 0 ? ".notdef" : $"g{gid}\n(not in atlas)", fallbackStyle);
                }

                string cap;
                if (_gidToCps != null && _gidToCps.TryGetValue(gid, out var caps) && caps.Count > 0)
                    cap = caps.Count == 1 ? $"U+{caps[0]:X4}" : $"U+{caps[0]:X4} +{caps.Count - 1}";
                else cap = "(no cp)";
                var capRect = GUILayoutUtility.GetRect(size, captionH, GUILayout.Width(size), GUILayout.Height(captionH));
                var capStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.UpperCenter, wordWrap = false };
                GUI.Label(capRect, $"{cap}\ng{gid}", capStyle);
            }
        }
    }
}
