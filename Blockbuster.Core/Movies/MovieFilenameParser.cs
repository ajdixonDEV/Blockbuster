using System.Text.RegularExpressions;

namespace Blockbuster.Core.Movies;

public static partial class MovieFilenameParser
{
    private static readonly string[] NoiseTokens =
    [
        "1080p", "2160p", "720p", "480p", "4k", "uhd", "bluray", "blu-ray", "brrip",
        "webrip", "web-rip", "webdl", "web-dl", "hdr", "dv", "remux", "x264", "x265",
        "h264", "h265", "hevc", "av1", "aac", "dts", "truehd", "atmos"
    ];

    public static ParsedMovieFileName Parse(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var yearMatch = YearRegex().Matches(name).LastOrDefault();
        int? year = yearMatch is null ? null : int.Parse(yearMatch.Value, System.Globalization.CultureInfo.InvariantCulture);
        var cut = yearMatch?.Index ?? name.Length;
        var title = name[..cut].Replace('.', ' ').Replace('_', ' ').Trim(' ', '-', '[', '(', '{');

        if (year is null)
        {
            var noise = NoiseTokens
                .Select(token => name.IndexOf(token, StringComparison.OrdinalIgnoreCase))
                .Where(index => index > 0)
                .DefaultIfEmpty(name.Length)
                .Min();
            title = name[..noise].Replace('.', ' ').Replace('_', ' ').Trim(' ', '-', '[', '(', '{');
        }

        title = WhitespaceRegex().Replace(title, " ");
        return new ParsedMovieFileName(string.IsNullOrWhiteSpace(title) ? name : title, year);
    }

    public static string NormalizeTitle(string title)
    {
        var characters = title.Normalize(System.Text.NormalizationForm.FormD)
            .Where(character => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant);
        return new string(characters.ToArray());
    }

    [GeneratedRegex(@"(?<!\d)(?:18|19|20)\d{2}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
