using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace ArabicStudy.Editor.FontFeaturePatch
{
    /// Merges patch records into a TMP font asset's feature tables, and
    /// removes them later. Lives in pure data-mutation land — doesn't
    /// invoke the extractor, doesn't read the source font.
    ///
    /// Identity for Revert is by exact tuple match on (baseMarkGlyphID,
    /// combiningMarkGlyphID, anchor x/y, adjustment x/y). If TMP's own
    /// importer happens to add a record with the same values, Revert will
    /// remove it — but for the Arabic cases we care about, TMP failed to
    /// import these in the first place (that's the whole reason we're
    /// patching), so the collision is theoretical.
    public static class FontFeaturePatchApplicator
    {
        public struct Result
        {
            public int added;
            public int updated;
            public int skipped;
            public int removed;
            public int notFound;
        }

        public static Result Apply(FontFeaturePatch patch)
        {
            var result = new Result();
            if (patch == null || patch.targetFontAsset == null) return result;

            var asset = patch.targetFontAsset;
            var table = EnsureFeatureTable(asset);
            var list = table.MarkToMarkAdjustmentRecords ?? new List<MarkToMarkAdjustmentRecord>();

            foreach (var pr in patch.markToMarkRecords)
            {
                if (pr.skip) { result.skipped++; continue; }

                var idx = FindIndex(list, pr);
                var record = ToTmp(pr);
                if (idx >= 0)
                {
                    // Update in place. Same identity (same glyph pair +
                    // same values), so the operation is a no-op in normal
                    // cases; keeps Apply idempotent after edits.
                    list[idx] = record;
                    result.updated++;
                }
                else
                {
                    // Also dedup by glyph-pair alone — if TMP imported its
                    // own record for this pair we'd rather override than
                    // duplicate (TMP's importer doesn't dedup either).
                    var pairIdx = FindPairIndex(list, pr);
                    if (pairIdx >= 0)
                    {
                        list[pairIdx] = record;
                        result.updated++;
                    }
                    else
                    {
                        list.Add(record);
                        result.added++;
                    }
                }
            }

            table.MarkToMarkAdjustmentRecords = list;
            FinaliseChanges(asset);
            return result;
        }

        public static Result Revert(FontFeaturePatch patch)
        {
            var result = new Result();
            if (patch == null || patch.targetFontAsset == null) return result;

            var asset = patch.targetFontAsset;
            var table = asset.fontFeatureTable;
            if (table?.MarkToMarkAdjustmentRecords == null) return result;
            var list = table.MarkToMarkAdjustmentRecords;

            foreach (var pr in patch.markToMarkRecords)
            {
                var idx = FindIndex(list, pr);
                if (idx >= 0) { list.RemoveAt(idx); result.removed++; }
                else result.notFound++;
            }

            FinaliseChanges(asset);
            return result;
        }

        // ---------- helpers ----------

        private static TMP_FontFeatureTable EnsureFeatureTable(TMP_FontAsset asset)
        {
            if (asset.fontFeatureTable == null)
            {
                // TMP normally creates this lazily; we want it to exist.
                // Setting via SerializedObject is the safe way if the
                // property is null on a freshly-created asset.
                var so = new SerializedObject(asset);
                so.FindProperty("m_FontFeatureTable")?.FindPropertyRelative("m_MarkToMarkAdjustmentRecords");
                so.ApplyModifiedProperties();
            }
            return asset.fontFeatureTable;
        }

        private static MarkToMarkAdjustmentRecord ToTmp(FontFeaturePatch.MarkToMarkRecord pr)
        {
            return new MarkToMarkAdjustmentRecord
            {
                baseMarkGlyphID = pr.baseMarkGlyphID,
                combiningMarkGlyphID = pr.combiningMarkGlyphID,
                baseMarkGlyphAnchorPoint = new GlyphAnchorPoint
                {
                    xCoordinate = pr.baseAnchorX,
                    yCoordinate = pr.baseAnchorY,
                },
                combiningMarkPositionAdjustment = new MarkPositionAdjustment
                {
                    xPositionAdjustment = pr.combiningAdjustmentX,
                    yPositionAdjustment = pr.combiningAdjustmentY,
                },
            };
        }

        /// Exact-match: glyph pair + all four numeric values match.
        /// Floating-point comparison is fine because the patch and font
        /// asset both store these as 32-bit floats with the same provenance.
        private static int FindIndex(List<MarkToMarkAdjustmentRecord> list,
            FontFeaturePatch.MarkToMarkRecord pr)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (r.baseMarkGlyphID == pr.baseMarkGlyphID
                    && r.combiningMarkGlyphID == pr.combiningMarkGlyphID
                    && r.baseMarkGlyphAnchorPoint.xCoordinate == pr.baseAnchorX
                    && r.baseMarkGlyphAnchorPoint.yCoordinate == pr.baseAnchorY
                    && r.combiningMarkPositionAdjustment.xPositionAdjustment == pr.combiningAdjustmentX
                    && r.combiningMarkPositionAdjustment.yPositionAdjustment == pr.combiningAdjustmentY)
                    return i;
            }
            return -1;
        }

        /// Glyph-pair match only — useful for Apply when we want to
        /// override an existing entry rather than duplicate it.
        private static int FindPairIndex(List<MarkToMarkAdjustmentRecord> list,
            FontFeaturePatch.MarkToMarkRecord pr)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (r.baseMarkGlyphID == pr.baseMarkGlyphID
                    && r.combiningMarkGlyphID == pr.combiningMarkGlyphID)
                    return i;
            }
            return -1;
        }

        private static void FinaliseChanges(TMP_FontAsset asset)
        {
            EditorUtility.SetDirty(asset);
            // Tell TMP to rebuild its internal lookup dictionaries from
            // the now-modified feature table.
            try { asset.ReadFontAssetDefinition(); }
            catch { /* method exists on most TMP versions, defensive */ }
            AssetDatabase.SaveAssetIfDirty(asset);
        }
    }
}
