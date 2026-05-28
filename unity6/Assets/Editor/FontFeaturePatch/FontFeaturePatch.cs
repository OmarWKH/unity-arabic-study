using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace ArabicStudy.Editor.FontFeaturePatch
{
    /// Persisted record of font-feature patches extracted for a single TMP
    /// font asset. Lives on disk as a sibling asset to its target. Records
    /// are stored separately from the font asset so we can re-apply them if
    /// TMP touches the feature tables (e.g., dynamic atlas re-population)
    /// and can also Revert just our additions without disturbing whatever
    /// TMP imported on its own.
    [CreateAssetMenu(
        fileName = "FontFeaturePatch",
        menuName = "Arabic Study/Font Feature Patch",
        order = 1100)]
    public sealed class FontFeaturePatch : ScriptableObject
    {
        public TMP_FontAsset targetFontAsset;

        [Tooltip("Identifier of the extractor that produced this patch. Used for traceability only.")]
        public string extractorName = "";

        [Tooltip("Path to the source TTF the extractor was pointed at.")]
        public string sourceFontPath = "";

        [Tooltip("UTC ISO-8601 timestamp at extraction time.")]
        public string extractedAtIso = "";

        public List<MarkToMarkRecord> markToMarkRecords = new();

        // Future extractors append here:
        // public List<GlyphPairAdjustmentRecord> glyphPairAdjustmentRecords = new();
        // public List<LigatureRecord> ligatureRecords = new();

        [Serializable]
        public struct MarkToMarkRecord
        {
            public uint baseMarkGlyphID;
            public string baseMarkName;        // diagnostic only
            public uint baseMarkCodepoint;     // diagnostic only

            public uint combiningMarkGlyphID;
            public string combiningMarkName;
            public uint combiningMarkCodepoint;

            public float baseAnchorX;
            public float baseAnchorY;
            public float combiningAdjustmentX;
            public float combiningAdjustmentY;

            [TextArea(2, 4)]
            public string sourceNote;

            [Tooltip("Skip when applying. Useful for staging multiple variants and toggling between them.")]
            public bool skip;
        }
    }
}
