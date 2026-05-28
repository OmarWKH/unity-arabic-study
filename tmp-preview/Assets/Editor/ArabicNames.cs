using System.Collections.Generic;

namespace ArabicStudy.Editor
{
    /// Curated Unicode name lookup for the codepoints most relevant to Arabic
    /// text rendering. Covers the full Arabic alphabet, all common harakat,
    /// hamza/alef variants, tatweel, teh marbuta, alef maksura, and the
    /// RTL / shaping control characters that come up debugging RTLTMPro.
    /// ASCII letters and digits get formulaic names. Presentation Forms
    /// ranges get a generic block label since their per-codepoint names are
    /// deterministic but verbose.
    ///
    /// The standard library doesn't expose Unicode names, so this is
    /// hand-rolled to just what we actually need.
    public static class ArabicNames
    {
        public static string UnicodeName(uint cp)
        {
            if (cp < 0x80)
            {
                if (cp >= 'A' && cp <= 'Z') return $"LATIN CAPITAL LETTER {(char)cp}";
                if (cp >= 'a' && cp <= 'z') return $"LATIN SMALL LETTER {(char)('A' + cp - 'a')}";
                if (cp >= '0' && cp <= '9') return $"DIGIT {(char)cp}";
                if (cp == ' ') return "SPACE";
                return "";
            }
            if (_names.TryGetValue(cp, out var n)) return n;
            if (cp >= 0xFB50 && cp <= 0xFDFF) return "ARABIC PRES.FORM-A";
            if (cp >= 0xFE70 && cp <= 0xFEFF) return "ARABIC PRES.FORM-B";
            if (cp >= 0x0600 && cp <= 0x06FF) return "ARABIC";
            return "";
        }

        private static readonly Dictionary<uint, string> _names = new()
        {
            // hamza + alef family
            { 0x0621, "ARABIC LETTER HAMZA" },
            { 0x0622, "ARABIC LETTER ALEF WITH MADDA ABOVE" },
            { 0x0623, "ARABIC LETTER ALEF WITH HAMZA ABOVE" },
            { 0x0624, "ARABIC LETTER WAW WITH HAMZA ABOVE" },
            { 0x0625, "ARABIC LETTER ALEF WITH HAMZA BELOW" },
            { 0x0626, "ARABIC LETTER YEH WITH HAMZA ABOVE" },
            // 28 base letters
            { 0x0627, "ARABIC LETTER ALEF" },
            { 0x0628, "ARABIC LETTER BEH" },
            { 0x0629, "ARABIC LETTER TEH MARBUTA" },
            { 0x062A, "ARABIC LETTER TEH" },
            { 0x062B, "ARABIC LETTER THEH" },
            { 0x062C, "ARABIC LETTER JEEM" },
            { 0x062D, "ARABIC LETTER HAH" },
            { 0x062E, "ARABIC LETTER KHAH" },
            { 0x062F, "ARABIC LETTER DAL" },
            { 0x0630, "ARABIC LETTER THAL" },
            { 0x0631, "ARABIC LETTER REH" },
            { 0x0632, "ARABIC LETTER ZAIN" },
            { 0x0633, "ARABIC LETTER SEEN" },
            { 0x0634, "ARABIC LETTER SHEEN" },
            { 0x0635, "ARABIC LETTER SAD" },
            { 0x0636, "ARABIC LETTER DAD" },
            { 0x0637, "ARABIC LETTER TAH" },
            { 0x0638, "ARABIC LETTER ZAH" },
            { 0x0639, "ARABIC LETTER AIN" },
            { 0x063A, "ARABIC LETTER GHAIN" },
            { 0x0640, "ARABIC TATWEEL" },
            { 0x0641, "ARABIC LETTER FEH" },
            { 0x0642, "ARABIC LETTER QAF" },
            { 0x0643, "ARABIC LETTER KAF" },
            { 0x0644, "ARABIC LETTER LAM" },
            { 0x0645, "ARABIC LETTER MEEM" },
            { 0x0646, "ARABIC LETTER NOON" },
            { 0x0647, "ARABIC LETTER HEH" },
            { 0x0648, "ARABIC LETTER WAW" },
            { 0x0649, "ARABIC LETTER ALEF MAKSURA" },
            { 0x064A, "ARABIC LETTER YEH" },
            // harakat
            { 0x064B, "ARABIC FATHATAN" },
            { 0x064C, "ARABIC DAMMATAN" },
            { 0x064D, "ARABIC KASRATAN" },
            { 0x064E, "ARABIC FATHA" },
            { 0x064F, "ARABIC DAMMA" },
            { 0x0650, "ARABIC KASRA" },
            { 0x0651, "ARABIC SHADDA" },
            { 0x0652, "ARABIC SUKUN" },
            { 0x0653, "ARABIC MADDAH ABOVE" },
            { 0x0654, "ARABIC HAMZA ABOVE" },
            { 0x0655, "ARABIC HAMZA BELOW" },
            { 0x0656, "ARABIC SUBSCRIPT ALEF" },
            { 0x0670, "ARABIC LETTER SUPERSCRIPT ALEF" },
            // RTL / shaping controls
            { 0x200C, "ZERO WIDTH NON-JOINER" },
            { 0x200D, "ZERO WIDTH JOINER" },
            { 0x200E, "LEFT-TO-RIGHT MARK" },
            { 0x200F, "RIGHT-TO-LEFT MARK" },
            { 0x202B, "RIGHT-TO-LEFT EMBEDDING" },
            { 0x202C, "POP DIRECTIONAL FORMATTING" },
        };
    }
}
