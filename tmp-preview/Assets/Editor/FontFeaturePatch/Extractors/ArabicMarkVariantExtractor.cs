using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace ArabicStudy.Editor.FontFeaturePatch.Extractors
{
    /// Extracts contextual mark-variant substitutions from an Arabic font
    /// and synthesises Mark-to-Mark records that reproduce the visual
    /// stacking HarfBuzz produces but that TMP can't extract from the font
    /// directly (because TMP doesn't recurse through GSUB Type-6 chained
    /// contexts).
    ///
    /// Pipes the work out to tools/extract-arabic-mark-variants.py via the
    /// project's local venv. The Python script streams JSON on stdout.
    public sealed class ArabicMarkVariantExtractor : IFontFeatureExtractor
    {
        public string DisplayName => "Arabic mark variants (Shadda + Harakat)";

        public string Description =>
            "Walks GSUB Type-6 ChainedContext lookups under rlig/ccmp/liga for the Arabic script, " +
            "finds the inner Type-1 substitutions that swap a harakat for a 'lifted' variant glyph, " +
            "and emits Mark-to-Mark records pairing the original harakat with shadda. " +
            "Offsets default to the variant's anchor-delta but are editable in this window — " +
            "tweak after applying if the visual stack isn't right.";

        public bool IsApplicableTo(TMP_FontAsset asset, string sourceFontPath)
        {
            if (asset == null || string.IsNullOrEmpty(sourceFontPath)) return false;
            if (!File.Exists(sourceFontPath)) return false;
            // The cheap heuristic: does the asset's character table mention
            // Arabic block at all? We don't want to invoke Python just to
            // discover the font is irrelevant.
            foreach (var c in asset.characterTable)
                if (c.unicode >= 0x0600 && c.unicode <= 0x06FF) return true;
            return false;
        }

        public bool Extract(TMP_FontAsset asset, string sourceFontPath, FontFeaturePatch patch)
        {
            // Walk up from the Unity project root looking for a tools/
            // directory that contains the extractor script. Repos often
            // keep tools/ at the git root rather than inside one of the
            // Unity projects, so we check a couple of levels up.
            const string scriptName = "extract-arabic-mark-variants.py";
            string scriptPath = FindToolsScript(scriptName, maxParents: 4);
            if (scriptPath == null)
            {
                Debug.LogError(
                    $"[ArabicMarkVariantExtractor] could not find tools/{scriptName} " +
                    $"in or above the Unity project root.");
                return false;
            }
            // The Python script resolves the font path relative to its own
            // working directory, so we set cwd to the script's grandparent
            // (one above tools/) — typically the repo root.
            var workingDir = Directory.GetParent(Path.GetDirectoryName(scriptPath)!)!.FullName;

            // Pass an absolute font path so the script doesn't depend on
            // its own cwd matching anything in particular.
            var absFontPath = Path.IsPathRooted(sourceFontPath)
                ? sourceFontPath
                : Path.GetFullPath(sourceFontPath);

            EditorUtility.DisplayProgressBar(
                "Arabic Mark Variant Extractor", "running python ...", 0.5f);
            PythonRunner.RunResult result;
            try
            {
                result = PythonRunner.Run(scriptPath,
                    new[] { absFontPath },
                    workingDirectory: workingDir,
                    timeoutMs: 60_000);
            }
            finally { EditorUtility.ClearProgressBar(); }

            if (!result.launched)
            {
                Debug.LogError(
                    $"[ArabicMarkVariantExtractor] failed to launch python.\n" +
                    $"  command: {result.commandLine}\n" +
                    $"  stderr:  {result.stderr}");
                return false;
            }
            if (result.exitCode != 0)
            {
                Debug.LogError(
                    $"[ArabicMarkVariantExtractor] extractor exited with code {result.exitCode}\n" +
                    $"  stderr:  {result.stderr}");
                return false;
            }
            if (!string.IsNullOrWhiteSpace(result.stderr))
            {
                // Stderr is for human diagnostics; don't fail on it, just log.
                Debug.Log(
                    $"[ArabicMarkVariantExtractor] python stderr:\n{result.stderr.TrimEnd()}");
            }

            ExtractorOutput parsed;
            try
            {
                parsed = JsonUtility.FromJson<ExtractorOutput>(result.stdout);
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[ArabicMarkVariantExtractor] could not parse JSON from python:\n" +
                    $"  {ex.GetType().Name}: {ex.Message}\n" +
                    $"  stdout was: {result.stdout?.Substring(0, Math.Min(400, result.stdout.Length))}...");
                return false;
            }
            if (parsed == null || parsed.records == null)
            {
                Debug.LogError(
                    $"[ArabicMarkVariantExtractor] python returned empty / null records.\n" +
                    $"  stdout was: {result.stdout}");
                return false;
            }

            patch.extractorName = parsed.extractor ?? DisplayName;
            patch.sourceFontPath = absFontPath;
            patch.extractedAtIso = parsed.extractedAtIso ?? "";
            patch.markToMarkRecords.Clear();
            foreach (var r in parsed.records)
            {
                if (r.kind != "mark_to_mark") continue;
                patch.markToMarkRecords.Add(new FontFeaturePatch.MarkToMarkRecord
                {
                    baseMarkGlyphID = r.baseMarkGlyphID,
                    baseMarkName = r.baseMarkName,
                    baseMarkCodepoint = r.baseMarkCodepoint,
                    combiningMarkGlyphID = r.combiningMarkGlyphID,
                    combiningMarkName = r.combiningMarkName,
                    combiningMarkCodepoint = r.combiningMarkCodepoint,
                    baseAnchorX = r.baseAnchorX,
                    baseAnchorY = r.baseAnchorY,
                    combiningAdjustmentX = r.combiningAdjustmentX,
                    combiningAdjustmentY = r.combiningAdjustmentY,
                    sourceNote = r.sourceNote,
                    skip = false,
                });
            }

            if (parsed.warnings != null && parsed.warnings.Count > 0)
            {
                var msg = string.Join("\n  ", parsed.warnings);
                Debug.LogWarning(
                    $"[ArabicMarkVariantExtractor] {parsed.warnings.Count} warning(s):\n  {msg}");
            }
            Debug.Log(
                $"[ArabicMarkVariantExtractor] extracted {patch.markToMarkRecords.Count} Mark-to-Mark record(s) for {asset.name}");
            return true;
        }

        /// Walks up from the Unity project root looking for `tools/<name>`.
        /// Returns the absolute path to the script, or null if not found
        /// within `maxParents` levels.
        private static string FindToolsScript(string scriptName, int maxParents)
        {
            // Application.dataPath is "<project>/Assets" → project root is one up.
            var dir = new DirectoryInfo(Application.dataPath).Parent;
            for (int i = 0; i <= maxParents && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "tools", scriptName);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        // -- JsonUtility-compatible mirror of the Python script's output. --

        [Serializable]
        private class ExtractorOutput
        {
            public int schemaVersion;
            public string extractor;
            public string extractedAtIso;
            public string sourceFont;
            public string sourceFontFamily;
            public List<ExtractorRecord> records;
            public List<string> warnings;
        }

        [Serializable]
        private class ExtractorRecord
        {
            public string kind;
            public uint baseMarkGlyphID;
            public string baseMarkName;
            public uint baseMarkCodepoint;
            public uint combiningMarkGlyphID;
            public string combiningMarkName;
            public uint combiningMarkCodepoint;
            public float baseAnchorX;
            public float baseAnchorY;
            public float combiningAdjustmentX;
            public float combiningAdjustmentY;
            public string sourceNote;
        }
    }
}
