using System.IO;
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
    /// One-shot automation of the manual SETUP.md steps for the baseline (built-in TMP) project.
    /// Run via menu: Arabic Study > Run Full Setup.
    public static class ArabicTestSetup
    {
        private const string FontTtfPath = "Assets/Fonts/Amiri-Regular.ttf";
        private const string FontAssetPath = "Assets/Fonts/Amiri-Regular SDF.asset";
        private const string TestStringPath = "Assets/ArabicTestString.txt";
        private const string ScenePath = "Assets/Scenes/ArabicTest.unity";
        private const string ProjectLabel = "[ArabicTestSetup/builtin-tmp]";

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
                "RAW TextMeshProUGUI (no RTLTMPro)",
                new Vector2(0, 240), new Vector2(1800, 60), fontAsset);
            CreateRawText(canvasGo.transform, "ArabicText_Raw", testString,
                new Vector2(0, 100), new Vector2(1800, 220), fontAsset);

            CreateLabel(canvasGo.transform, "Label_RTL",
                "RTL Text Mesh Pro UGUI (pre-shaped by RTLTMPro)",
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
            tmp.enableWordWrapping = true;
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
            tmp.enableWordWrapping = true;
        }
    }
}
