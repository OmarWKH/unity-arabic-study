using TMPro;

namespace ArabicStudy.Editor.FontFeaturePatch
{
    /// Pluggable extractor that pulls some category of feature-table data
    /// out of a source font and produces a FontFeaturePatch describing what
    /// should be added to a TMP font asset.
    ///
    /// Implementations live in `Extractors/` and are discovered by the
    /// FontFeaturePatcherWindow via reflection — drop a new class in, it
    /// shows up in the UI.
    public interface IFontFeatureExtractor
    {
        /// Short human-readable name shown in the patcher window.
        string DisplayName { get; }

        /// Longer description shown under the extractor's row in the UI.
        string Description { get; }

        /// Whether this extractor is meaningful for the given font asset.
        /// Used to gate the UI — e.g., an Arabic extractor returns false
        /// for a font with no Arabic codepoints.
        bool IsApplicableTo(TMP_FontAsset asset, string sourceFontPath);

        /// Run the extraction. Should populate `patch` with whatever
        /// records this extractor produces. Returns true on success.
        /// Errors should be reported via Debug.LogError and return false.
        bool Extract(TMP_FontAsset asset, string sourceFontPath, FontFeaturePatch patch);
    }
}
