using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace ArabicStudy.Editor.FontFeaturePatch
{
    /// EditorWindow that drives feature-table patches for a TMP font asset:
    /// extract data the TMP importer misses, persist it to a sibling
    /// patch SO, and Apply / Revert the records into the font asset's
    /// feature tables.
    ///
    /// Extractors are discovered by reflection — any class implementing
    /// IFontFeatureExtractor with a public parameterless constructor is
    /// picked up automatically.
    public sealed class FontFeaturePatcherWindow : EditorWindow
    {
        // ---------- state ----------

        private TMP_FontAsset _fontAsset;
        private FontFeaturePatch _patch;            // sibling, loaded on demand
        private string _patchAssetPath = "";
        private string _sourceFontAbsPath = "";

        private List<IFontFeatureExtractor> _extractors;
        private Dictionary<string, bool> _extractorEnabled = new();
        private Vector2 _scroll;
        private bool _showRecords = true;

        // ---------- menu / lifecycle ----------

        [MenuItem("Arabic Study/Font Feature Patcher")]
        public static void Open()
        {
            var win = GetWindow<FontFeaturePatcherWindow>("Font Feature Patcher");
            win.minSize = new Vector2(540, 480);
        }

        private void OnEnable() => RebuildExtractorList();

        private void OnGUI()
        {
            DrawAssetPicker();
            EditorGUILayout.Space(4);
            if (_fontAsset == null)
            {
                EditorGUILayout.HelpBox(
                    "Pick a TMP font asset to begin.\n\n" +
                    "The window walks the asset's source TTF for feature data " +
                    "that TMP's own importer misses, persists the missing " +
                    "records to a sibling .featurepatch.asset, and gives you " +
                    "Apply / Revert buttons to merge them into the font asset's " +
                    "feature tables.",
                    MessageType.Info);
                return;
            }

            DrawExtractorList();
            EditorGUILayout.Space(4);
            DrawPatchSummary();
            EditorGUILayout.Space(4);
            DrawApplyControls();
            EditorGUILayout.Space(4);
            DrawRecordEditor();
        }

        // ---------- top: asset + source font ----------

        private void DrawAssetPicker()
        {
            using (var c = new EditorGUI.ChangeCheckScope())
            {
                _fontAsset = (TMP_FontAsset)EditorGUILayout.ObjectField(
                    "Font Asset", _fontAsset, typeof(TMP_FontAsset), false);
                if (c.changed) OnFontAssetChanged();
            }
            if (_fontAsset == null) return;

            // Source TTF auto-detect with override.
            EditorGUILayout.LabelField("Source font (TTF/OTF)", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _sourceFontAbsPath = EditorGUILayout.TextField(
                    GUIContent.none, _sourceFontAbsPath);
                if (GUILayout.Button("Auto", GUILayout.Width(50)))
                    _sourceFontAbsPath = DetectSourceFontPath(_fontAsset);
                if (GUILayout.Button("Browse...", GUILayout.Width(80)))
                {
                    var picked = EditorUtility.OpenFilePanel("Source TTF/OTF", "", "ttf,otf");
                    if (!string.IsNullOrEmpty(picked)) _sourceFontAbsPath = picked;
                }
            }
            if (!string.IsNullOrEmpty(_sourceFontAbsPath) && !File.Exists(_sourceFontAbsPath))
                EditorGUILayout.HelpBox("source file not found at this path", MessageType.Warning);
        }

        private void OnFontAssetChanged()
        {
            _sourceFontAbsPath = DetectSourceFontPath(_fontAsset);
            _patchAssetPath = ComputePatchAssetPath(_fontAsset);
            _patch = !string.IsNullOrEmpty(_patchAssetPath)
                ? AssetDatabase.LoadAssetAtPath<FontFeaturePatch>(_patchAssetPath)
                : null;
            // Re-resolve which extractors are applicable now.
            _extractorEnabled.Clear();
        }

        private static string DetectSourceFontPath(TMP_FontAsset asset)
        {
            if (asset == null || asset.sourceFontFile == null) return "";
            var rel = AssetDatabase.GetAssetPath(asset.sourceFontFile);
            if (string.IsNullOrEmpty(rel)) return "";
            return Path.GetFullPath(rel);
        }

        private static string ComputePatchAssetPath(TMP_FontAsset asset)
        {
            var assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath)) return "";
            var dir = Path.GetDirectoryName(assetPath);
            var stem = Path.GetFileNameWithoutExtension(assetPath);
            return $"{dir}/{stem}.featurepatch.asset".Replace('\\', '/');
        }

        // ---------- extractors ----------

        private void RebuildExtractorList()
        {
            var ifaceType = typeof(IFontFeatureExtractor);
            _extractors = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => SafeGetTypes(a))
                .Where(t => !t.IsAbstract && !t.IsInterface && ifaceType.IsAssignableFrom(t))
                .Where(t => t.GetConstructor(Type.EmptyTypes) != null)
                .Select(t => (IFontFeatureExtractor)Activator.CreateInstance(t))
                .OrderBy(e => e.DisplayName)
                .ToList();
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
            catch { return Array.Empty<Type>(); }
        }

        private void DrawExtractorList()
        {
            EditorGUILayout.LabelField("Extractors", EditorStyles.boldLabel);
            if (_extractors == null || _extractors.Count == 0)
            {
                EditorGUILayout.HelpBox("no IFontFeatureExtractor implementations found in any loaded assembly", MessageType.Warning);
                return;
            }

            foreach (var ext in _extractors)
            {
                var applicable = ext.IsApplicableTo(_fontAsset, _sourceFontAbsPath);
                using (new EditorGUI.DisabledScope(!applicable))
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (!_extractorEnabled.TryGetValue(ext.GetType().FullName, out var on))
                            on = applicable;
                        on = EditorGUILayout.ToggleLeft(
                            ext.DisplayName + (applicable ? "" : "  — not applicable to this font"),
                            on, EditorStyles.boldLabel);
                        _extractorEnabled[ext.GetType().FullName] = on;
                    }
                    EditorGUILayout.LabelField(ext.Description, EditorStyles.wordWrappedMiniLabel);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Extract → Patch", GUILayout.Width(160)))
                    RunSelectedExtractors();
                GUILayout.FlexibleSpace();
            }
        }

        private void RunSelectedExtractors()
        {
            if (_fontAsset == null) return;
            if (string.IsNullOrEmpty(_sourceFontAbsPath) || !File.Exists(_sourceFontAbsPath))
            {
                EditorUtility.DisplayDialog("Font Feature Patcher",
                    "Couldn't resolve the source TTF/OTF path. Use Auto or Browse to set it.", "OK");
                return;
            }

            // Ensure the patch SO exists.
            if (_patch == null)
            {
                _patchAssetPath = ComputePatchAssetPath(_fontAsset);
                _patch = ScriptableObject.CreateInstance<FontFeaturePatch>();
                _patch.targetFontAsset = _fontAsset;
                AssetDatabase.CreateAsset(_patch, _patchAssetPath);
            }
            else
            {
                _patch.targetFontAsset = _fontAsset;
            }

            int totalExtractors = 0;
            foreach (var ext in _extractors)
            {
                if (!_extractorEnabled.TryGetValue(ext.GetType().FullName, out var on) || !on)
                    continue;
                if (!ext.IsApplicableTo(_fontAsset, _sourceFontAbsPath)) continue;
                var ok = ext.Extract(_fontAsset, _sourceFontAbsPath, _patch);
                if (ok) totalExtractors++;
            }

            EditorUtility.SetDirty(_patch);
            AssetDatabase.SaveAssetIfDirty(_patch);
            ShowNotification(new GUIContent(
                $"Extracted via {totalExtractors} extractor(s) → {_patch.markToMarkRecords.Count} records"));
        }

        // ---------- patch summary + apply/revert ----------

        private void DrawPatchSummary()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (_patch == null)
                {
                    EditorGUILayout.LabelField(
                        "No patch file yet — run an extractor to create one.",
                        EditorStyles.miniLabel);
                    return;
                }
                EditorGUILayout.LabelField("Patch", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"  file:        {_patchAssetPath}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"  extractor:   {_patch.extractorName}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"  extracted:   {_patch.extractedAtIso}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"  records:     {_patch.markToMarkRecords.Count} mark-to-mark", EditorStyles.miniLabel);
            }
        }

        private void DrawApplyControls()
        {
            if (_patch == null) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_patch.markToMarkRecords.Count == 0))
                {
                    if (GUILayout.Button("Apply patch → font asset", GUILayout.Width(220)))
                    {
                        var r = FontFeaturePatchApplicator.Apply(_patch);
                        ShowNotification(new GUIContent(
                            $"added {r.added}, updated {r.updated}, skipped {r.skipped}"));
                    }
                    if (GUILayout.Button("Re-apply", GUILayout.Width(90)))
                    {
                        var r = FontFeaturePatchApplicator.Apply(_patch);
                        ShowNotification(new GUIContent(
                            $"added {r.added}, updated {r.updated}, skipped {r.skipped}"));
                    }
                    if (GUILayout.Button("Revert", GUILayout.Width(90)))
                    {
                        var r = FontFeaturePatchApplicator.Revert(_patch);
                        ShowNotification(new GUIContent(
                            $"removed {r.removed}, not found {r.notFound}"));
                    }
                }
            }
        }

        // ---------- record editor ----------

        private void DrawRecordEditor()
        {
            if (_patch == null) return;
            _showRecords = EditorGUILayout.Foldout(_showRecords,
                $"Records ({_patch.markToMarkRecords.Count})", true, EditorStyles.foldoutHeader);
            if (!_showRecords) return;

            if (_patch.markToMarkRecords.Count == 0)
            {
                EditorGUILayout.LabelField("  (no records — run an extractor)", EditorStyles.miniLabel);
                return;
            }

            using (var s = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = s.scrollPosition;

                var so = new SerializedObject(_patch);
                var prop = so.FindProperty(nameof(FontFeaturePatch.markToMarkRecords));
                so.Update();
                for (int i = 0; i < prop.arraySize; i++)
                {
                    var rec = _patch.markToMarkRecords[i];
                    var header = $"#{i}   '{Cp(rec.baseMarkCodepoint)}'+'{Cp(rec.combiningMarkCodepoint)}'   " +
                                 $"({rec.baseMarkName} → {rec.combiningMarkName})";
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        EditorGUILayout.LabelField(header, EditorStyles.miniBoldLabel);
                        EditorGUILayout.PropertyField(prop.GetArrayElementAtIndex(i), GUIContent.none, true);
                    }
                }
                so.ApplyModifiedProperties();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Save patch (no apply)", GUILayout.Width(180)))
                {
                    EditorUtility.SetDirty(_patch);
                    AssetDatabase.SaveAssetIfDirty(_patch);
                    ShowNotification(new GUIContent("saved"));
                }
            }
        }

        private static string Cp(uint cp)
        {
            try { return char.ConvertFromUtf32((int)cp); }
            catch { return "?"; }
        }
    }
}
