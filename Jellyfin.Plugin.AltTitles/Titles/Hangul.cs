using System.Text;

namespace Jellyfin.Plugin.AltTitles.Titles;

/// <summary>
/// Revised Romanization of Korean, syllable by syllable: 기생충 -> "Gisaengchung", 오징어 게임 -> "Ojingeo Geim".
/// The sound changes between syllables are left out; for searching that is close enough.
/// </summary>
public static class Hangul
{
    private static readonly string[] Initials =
        { "g", "kk", "n", "d", "tt", "r", "m", "b", "pp", "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h" };

    private static readonly string[] Medials =
        { "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae", "oe", "yo", "u", "wo", "we", "wi", "yu", "eu", "ui", "i" };

    private static readonly string[] Finals =
        { "", "k", "k", "k", "n", "n", "n", "t", "l", "k", "m", "l", "l", "l", "p", "l", "m", "p", "p", "t", "t", "ng", "t", "t", "k", "t", "p", "t" };

    public static bool Contains(string? text) => text is not null && text.Any(IsSyllable);

    /// <summary>The romanized text, or null when there is no Hangul in it.</summary>
    public static string? Romanize(string? text)
    {
        if (!Contains(text))
        {
            return null;
        }

        var sb = new StringBuilder();
        var wordStart = true;
        foreach (var c in text!)
        {
            if (IsSyllable(c))
            {
                var index = c - 0xAC00;
                var initial = index / (21 * 28);
                var medial = index % (21 * 28) / 28;
                var final = index % 28;
                var part = (initial == 5 && wordStart ? "r" : Initials[initial]) + Medials[medial] + Finals[final];
                sb.Append(wordStart && part.Length > 0 ? char.ToUpperInvariant(part[0]) + part[1..] : part);
                wordStart = false;
            }
            else
            {
                sb.Append(c);
                wordStart = !char.IsLetterOrDigit(c);
            }
        }

        return sb.ToString();
    }

    private static bool IsSyllable(char c) => c >= '가' && c <= '힣';
}
