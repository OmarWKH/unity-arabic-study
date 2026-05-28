using System;
using System.Reflection;
using RTLTMPro;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace ArabicStudy.Editor
{
    /// EditorWindow that runs RTLTMPro's FixRTL pass on a string and shows the
    /// codepoint-by-codepoint before/after diff. Two scrollable columns of glyph
    /// chips: Original on the left, After-FixRTL on the right. Each row has an
    /// "Inspect" button that opens the Font Table Search window with that
    /// codepoint pre-filled, so you can quickly see which feature-table entries
    /// apply to the original vs. its presentation-form replacement.
    ///
    /// Input modes:
    ///   - Drag in an RTLTextMeshPro / RTLTextMeshPro3D component → reads its
    ///     current flags and (best-effort) its pre-shaping original text via
    ///     reflection. Lets you debug what RTLTMPro is actually doing to a real
    ///     scene component.
    ///   - Or paste raw text + toggle the four flags by hand.
    public sealed class RTLTMProDebugger : EditorWindow
    {
        // ---------- state ----------

        private TMP_FontAsset _fontAsset;
        private MonoBehaviour _rtlComponent; // RTLTextMeshPro or RTLTextMeshPro3D
        private string _input = "بِسْمِ اللَّهِ الرَّحْمَٰنِ الرَّحِيمِ";
        private bool _farsi = true;
        private bool _fixTags = true;
        private bool _preserveNumbers;
        private bool _preserveShadda;

        private string _output = "";
        private string _runNote = "";
        private bool _hasOutput;

        private float _chipSize = 56f;
        private Vector2 _scrollLeft, _scrollRight;
        private readonly TMPFontAssetView _view = new();
        private GUIStyle _rowStyle;

        // ---------- menu / lifecycle ----------

        [MenuItem("Arabic Study/RTLTMPro Debugger")]
        public static void Open()
        {
            var win = GetWindow<RTLTMProDebugger>("RTLTMPro Debugger");
            win.minSize = new Vector2(820, 520);
        }

        private void OnGUI()
        {
            if (_rowStyle == null) _rowStyle = new GUIStyle(EditorStyles.label) { richText = true };

            DrawHeader();
            EditorGUILayout.Space(4);
            if (_hasOutput) DrawColumns();
        }

        // ---------- header ----------

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("RTLTMPro Debugger", EditorStyles.boldLabel);

            using (var c = new EditorGUI.ChangeCheckScope())
            {
                _fontAsset = (TMP_FontAsset)EditorGUILayout.ObjectField(
                    "Font Asset (for chips)", _fontAsset, typeof(TMP_FontAsset), false);
                if (c.changed) _view.FontAsset = _fontAsset;
            }
            if (_view.FontAsset != _fontAsset) _view.FontAsset = _fontAsset;

            using (var c = new EditorGUI.ChangeCheckScope())
            {
                _rtlComponent = (MonoBehaviour)EditorGUILayout.ObjectField(
                    new GUIContent("RTL Component", "Drag an RTLTextMeshPro or RTLTextMeshPro3D from the scene to pull its flags + pre-fix text."),
                    _rtlComponent, typeof(MonoBehaviour), true);
                if (c.changed && _rtlComponent != null) ImportFromComponent(_rtlComponent);
            }

            _input = EditorGUILayout.TextField("Input Text", _input);

            using (new EditorGUILayout.HorizontalScope())
            {
                _farsi = EditorGUILayout.ToggleLeft("farsi", _farsi, GUILayout.Width(80));
                _fixTags = EditorGUILayout.ToggleLeft("fixTags", _fixTags, GUILayout.Width(90));
                _preserveNumbers = EditorGUILayout.ToggleLeft("preserveNumbers", _preserveNumbers, GUILayout.Width(150));
                _preserveShadda = EditorGUILayout.ToggleLeft("preserveShadda", _preserveShadda, GUILayout.Width(140));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Chip size", GUILayout.Width(70));
                _chipSize = GUILayout.HorizontalSlider(_chipSize, 24f, 128f);
                EditorGUILayout.LabelField($"{(int)_chipSize}px", GUILayout.Width(40));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Run FixRTL", GUILayout.Width(120))) RunFixRtl();
                if (GUILayout.Button("Clear", GUILayout.Width(60)))
                { _input = ""; _output = ""; _hasOutput = false; _runNote = ""; }
                GUILayout.FlexibleSpace();
            }

            if (!string.IsNullOrEmpty(_runNote))
                EditorGUILayout.HelpBox(_runNote, MessageType.Info);
        }

        /// Read flags + pre-fix text from a real RTLTMPro component. Field names
        /// vary slightly between component variants and fork revisions, so this
        /// uses reflection to gather what's there and tolerates absent fields.
        private void ImportFromComponent(MonoBehaviour mb)
        {
            var t = mb.GetType();
            _farsi = ReadBool(mb, t, "farsi", "Farsi") ?? _farsi;
            _fixTags = ReadBool(mb, t, "fixTags", "FixTags") ?? _fixTags;
            _preserveNumbers = ReadBool(mb, t, "preserveNumbers", "PreserveNumbers") ?? _preserveNumbers;
            _preserveShadda = ReadBool(mb, t, "preserveShadda", "PreserveShadda") ?? _preserveShadda;

            // RTLTMPro stores the user-supplied (pre-fix) text in a private
            // field on the component; the TMP `text` property holds the
            // post-fix shaped result. Try common field names.
            var pre = ReadString(mb, t, "OriginalText", "originalText", "m_OriginalText");
            if (!string.IsNullOrEmpty(pre))
            {
                _input = pre;
                _runNote = $"imported {pre.Length} chars from {t.Name}";
            }
            else
            {
                _runNote = $"imported flags from {t.Name} — couldn't find a pre-shaping text field; using current Input Text";
            }
        }

        private static bool? ReadBool(object o, Type t, params string[] names)
        {
            foreach (var n in names)
            {
                var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.PropertyType == typeof(bool)) return (bool)p.GetValue(o);
                var f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(bool)) return (bool)f.GetValue(o);
            }
            return null;
        }

        private static string ReadString(object o, Type t, params string[] names)
        {
            foreach (var n in names)
            {
                var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.PropertyType == typeof(string)) return (string)p.GetValue(o);
                var f = t.GetField(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(string)) return (string)f.GetValue(o);
            }
            return null;
        }

        // ---------- run FixRTL ----------

        private void RunFixRtl()
        {
            _output = "";
            _hasOutput = false;
            if (string.IsNullOrEmpty(_input))
            { _runNote = "input is empty"; _hasOutput = false; return; }

            try
            {
                // RTLSupport.FixRTL fills a FastStringBuilder; convert to string after.
                var sb = new FastStringBuilder(_input.Length * 2 + 32);
                RTLSupport.FixRTL(_input, sb, _farsi, _fixTags, _preserveNumbers, _preserveShadda);
                _output = sb.ToString();
                _hasOutput = true;
                _runNote = $"input {_input.Length} chars  →  output {_output.Length} chars";

                // Pre-populate the atlas for both strings so chips render.
                if (_view.FontAsset != null)
                {
                    var cps = new System.Collections.Generic.HashSet<uint>();
                    AddCodepoints(_input, cps);
                    AddCodepoints(_output, cps);
                    _view.PopulateAtlasForCodepoints(cps);
                    _view.EnsureReverseMap();
                }
            }
            catch (Exception ex)
            {
                _runNote = $"FixRTL threw: {ex.GetType().Name}: {ex.Message}";
                _hasOutput = false;
            }
        }

        private static void AddCodepoints(string s, System.Collections.Generic.HashSet<uint> into)
        {
            if (string.IsNullOrEmpty(s)) return;
            int i = 0;
            while (i < s.Length)
            {
                int cp = char.ConvertToUtf32(s, i);
                into.Add((uint)cp);
                i += char.IsSurrogatePair(s, i) ? 2 : 1;
            }
        }

        // ---------- columns ----------

        private void DrawColumns()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawColumn("Original", _input, ref _scrollLeft);
                DrawColumn("After FixRTL", _output, ref _scrollRight);
            }
        }

        private void DrawColumn(string title, string text, ref Vector2 scroll)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandWidth(true)))
            {
                EditorGUILayout.LabelField($"{title}  ({CountCodepoints(text)} codepoints)", EditorStyles.miniBoldLabel);
                using (var s = new EditorGUILayout.ScrollViewScope(scroll, GUILayout.ExpandHeight(true)))
                {
                    scroll = s.scrollPosition;
                    if (string.IsNullOrEmpty(text))
                    {
                        EditorGUILayout.LabelField("(empty)", EditorStyles.miniLabel);
                        return;
                    }
                    int i = 0, index = 0;
                    while (i < text.Length)
                    {
                        int cp = char.ConvertToUtf32(text, i);
                        DrawCodepointRow(index, (uint)cp);
                        i += char.IsSurrogatePair(text, i) ? 2 : 1;
                        index++;
                    }
                }
            }
        }

        private void DrawCodepointRow(int index, uint cp)
        {
            var gid = _view.GlyphForCodepoint(cp);
            using (new EditorGUILayout.HorizontalScope())
            {
                _view.DrawGlyphChip(gid, _chipSize);
                using (new EditorGUILayout.VerticalScope())
                {
                    var name = ArabicNames.UnicodeName(cp);
                    EditorGUILayout.LabelField(
                        $"#{index}  '{TMPFontAssetView.CodepointAsString(cp)}'  U+{cp:X4}{(string.IsNullOrEmpty(name) ? "" : "  " + name)}",
                        _rowStyle);
                    EditorGUILayout.LabelField(
                        gid != 0 ? $"glyph {gid}" : "(no glyph mapping)",
                        EditorStyles.miniLabel);
                    if (GUILayout.Button("Inspect in Font Table Search", GUILayout.Width(220)))
                        TMPFontTableSearch.OpenWith(_fontAsset, cp);
                }
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.Space(2);
        }

        private static int CountCodepoints(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int i = 0, n = 0;
            while (i < s.Length)
            {
                n++;
                i += char.IsSurrogatePair(s, i) ? 2 : 1;
            }
            return n;
        }
    }
}
