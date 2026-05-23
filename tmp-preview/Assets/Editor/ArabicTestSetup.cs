using System;
using System.IO;
using System.Linq;
using System.Reflection;
using RTLTMPro;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

namespace ArabicStudy.Editor
{
    /// One-shot automation for the TMP 3.2.0-pre.15 (preview) project.
    /// Same as the baseline setup, plus a best-effort enable of OpenType layout
    /// features (liga, rlig, mark, mkmk, init, medi, fina, isol, ccmp) — the
    /// features that are the whole point of this comparison.
    public static class ArabicTestSetup
    {
        private const string FontTtfPath = "Assets/Fonts/Amiri-Regular.ttf";
        private const string FontAssetPath = "Assets/Fonts/Amiri-Regular SDF.asset";
        private const string TestStringPath = "Assets/ArabicTestString.txt";
        private const string ScenePath = "Assets/Scenes/ArabicTest.unity";
        private const string ProjectLabel = "[ArabicTestSetup/tmp-preview]";

        private static readonly string[] OpenTypeFeatureTags =
            { "liga", "rlig", "mark", "mkmk", "init", "medi", "fina", "isol", "ccmp" };

        [MenuItem("Arabic Study/Run Full Setup")]
        public static void Run()
        {
            Debug.Log($"{ProjectLabel} starting setup");

            var fontAsset = EnsureFontAsset();
            if (fontAsset == null)
            {
                Debug.LogError($"{ProjectLabel} could not produce font asset, aborting");
                return;
            }

            TryEnableOpenTypeFeatures(fontAsset);
            BuildScene(fontAsset);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"{ProjectLabel} done — open {ScenePath}");
        }

        private static TMP_FontAsset EnsureFontAsset()
        {
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontAssetPath);
            if (existing != null)
            {
                Debug.Log($"{ProjectLabel} reusing existing font asset at {FontAssetPath}");
                return existing;
            }

            var sourceFont = AssetDatabase.LoadAssetAtPath<Font>(FontTtfPath);
            if (sourceFont == null)
            {
                Debug.LogError($"{ProjectLabel} {FontTtfPath} missing — did the .ttf import?");
                return null;
            }

            var fontAsset = TMP_FontAsset.CreateFontAsset(
                sourceFont,
                samplingPointSize: 90,
                atlasPadding: 9,
                renderMode: GlyphRenderMode.SDFAA,
                atlasWidth: 2048,
                atlasHeight: 2048,
                atlasPopulationMode: AtlasPopulationMode.Dynamic,
                enableMultiAtlasSupport: true);

            fontAsset.name = "Amiri-Regular SDF";

            Directory.CreateDirectory("Assets/Fonts");
            AssetDatabase.CreateAsset(fontAsset, FontAssetPath);
            Debug.Log($"{ProjectLabel} created dynamic SDF font asset at {FontAssetPath}");
            return fontAsset;
        }

        /// In TMP 3.2.0-pre.15 the active OpenType feature list is stored on the
        /// font asset. The exact field name/shape varies across preview revisions,
        /// so this uses SerializedObject + reflection defensively. If the API
        /// shifts, this logs a clear warning and the user enables features by
        /// hand in the inspector.
        private static void TryEnableOpenTypeFeatures(TMP_FontAsset fontAsset)
        {
            try
            {
                var so = new SerializedObject(fontAsset);

                // Most likely serialized field name in TMP 3.2 preview.
                var prop = so.FindProperty("m_ActiveFontFeatures");
                if (prop == null || !prop.isArray)
                {
                    // Fall back: scan all serialized properties for anything feature-shaped.
                    prop = FindArrayProperty(so, "FontFeature") ?? FindArrayProperty(so, "OpenType");
                }

                if (prop == null || !prop.isArray)
                {
                    LogManualEnableHint(fontAsset, "no serialized OpenType feature list found on font asset");
                    return;
                }

                prop.arraySize = OpenTypeFeatureTags.Length;
                for (int i = 0; i < OpenTypeFeatureTags.Length; i++)
                {
                    var element = prop.GetArrayElementAtIndex(i);
                    var tag = OpenTypeFeatureTags[i];
                    var tagAsInt = TagToInt(tag);

                    if (element.propertyType == SerializedPropertyType.Integer ||
                        element.propertyType == SerializedPropertyType.Enum)
                    {
                        element.intValue = tagAsInt;
                    }
                    else
                    {
                        LogManualEnableHint(fontAsset,
                            $"feature list element type is {element.propertyType}, not int/enum — cannot set automatically");
                        return;
                    }
                }

                so.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log($"{ProjectLabel} enabled OpenType features: {string.Join(", ", OpenTypeFeatureTags)}");
            }
            catch (Exception ex)
            {
                LogManualEnableHint(fontAsset, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static SerializedProperty FindArrayProperty(SerializedObject so, string nameContains)
        {
            var iter = so.GetIterator();
            if (!iter.NextVisible(true)) return null;
            do
            {
                if (iter.isArray && iter.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    return iter.Copy();
            } while (iter.NextVisible(false));
            return null;
        }

        private static int TagToInt(string tag)
        {
            // OpenType feature tags are 4-char ASCII packed big-endian into a uint.
            return (tag[0] << 24) | (tag[1] << 16) | (tag[2] << 8) | tag[3];
        }

        private static void LogManualEnableHint(TMP_FontAsset fontAsset, string reason)
        {
            Debug.LogWarning(
                $"{ProjectLabel} could not auto-enable OpenType features ({reason}). " +
                $"Open {AssetDatabase.GetAssetPath(fontAsset)} in the inspector and enable: " +
                string.Join(", ", OpenTypeFeatureTags));
        }

        private static void BuildScene(TMP_FontAsset fontAsset)
        {
            Directory.CreateDirectory("Assets/Scenes");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var canvasGo = new GameObject("Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            var testString = File.Exists(TestStringPath)
                ? File.ReadAllText(TestStringPath)
                : "بِسْمِ اللَّهِ الرَّحْمَٰنِ الرَّحِيمِ";

            CreateLabel(canvasGo.transform, "Label_Raw",
                "RAW TextMeshProUGUI — OpenType shaping in TMP itself",
                new Vector2(0, 240), new Vector2(1800, 60), fontAsset);
            CreateRawText(canvasGo.transform, "ArabicText_Raw", testString,
                new Vector2(0, 100), new Vector2(1800, 220), fontAsset);

            CreateLabel(canvasGo.transform, "Label_RTL",
                "RTL Text Mesh Pro UGUI — RTLTMPro pre-shaping (may be redundant now)",
                new Vector2(0, -100), new Vector2(1800, 60), fontAsset);
            CreateRtlText(canvasGo.transform, "ArabicText_RTL", testString,
                new Vector2(0, -240), new Vector2(1800, 220), fontAsset);

            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"{ProjectLabel} saved scene at {ScenePath}");
        }

        private static RectTransform AttachRect(GameObject go, Vector2 anchoredPos, Vector2 size)
        {
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            return rt;
        }

        private static void CreateLabel(Transform parent, string name, string text,
            Vector2 pos, Vector2 size, TMP_FontAsset fontAsset)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            AttachRect(go, pos, size);

            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.font = fontAsset;
            tmp.fontSize = 28;
            tmp.text = text;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.75f, 0.75f, 0.75f);
        }

        private static void CreateRawText(Transform parent, string name, string text,
            Vector2 pos, Vector2 size, TMP_FontAsset fontAsset)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            AttachRect(go, pos, size);

            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.font = fontAsset;
            tmp.fontSize = 40;
            tmp.text = text;
            tmp.alignment = TextAlignmentOptions.MidlineRight;
            tmp.isRightToLeftText = true;
            tmp.textWrappingMode = TextWrappingModes.Normal;
        }

        private static void CreateRtlText(Transform parent, string name, string text,
            Vector2 pos, Vector2 size, TMP_FontAsset fontAsset)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent, false);
            AttachRect(go, pos, size);

            var tmp = go.AddComponent<RTLTextMeshPro>();
            tmp.font = fontAsset;
            tmp.fontSize = 40;
            tmp.text = text;
            tmp.alignment = TextAlignmentOptions.MidlineRight;
            tmp.textWrappingMode = TextWrappingModes.Normal;
        }
    }
}
