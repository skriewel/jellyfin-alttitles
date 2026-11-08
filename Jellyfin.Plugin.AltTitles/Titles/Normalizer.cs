using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.AltTitles.Titles;

/// <summary>
/// Search form of a title: lower case, without accents, punctuation as single spaces. A compact form
/// (no spaces at all) lets "kiminonawa" find "Kimi no Na wa." and "spiderman" find "Spider-Man".
/// </summary>
public static class Normalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (c is '\'' or '’' or '‘' or '`' or '´')
            {
                continue; // "Howl's" == "Howls"
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (!space && sb.Length > 0)
            {
                sb.Append(' ');
                space = true;
            }
        }

        return sb.ToString().Trim().Normalize(NormalizationForm.FormC);
    }

    public static string Compact(string normalized) => normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
}
