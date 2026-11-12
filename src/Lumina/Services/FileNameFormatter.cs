using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Lumina.Services;

/// <summary>
/// Expands file name patterns. Tokens: {yyyy} {yy} {MM} {dd} {HH} {mm} {ss} {app} {n}.
/// </summary>
public static partial class FileNameFormatter
{
    public const string TokenHelp = "{yyyy} {MM} {dd} {HH} {mm} {ss}  |  {app} = captured app  |  {n} = counter";

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex TokenRegex();

    public static string Format(string pattern, string? app, int counter, DateTime now)
    {
        string result = TokenRegex().Replace(pattern ?? "", m => m.Groups[1].Value switch
        {
            "yyyy" or "yy" or "MM" or "dd" or "HH" or "mm" or "ss" => now.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture),
            "app" => string.IsNullOrWhiteSpace(app) ? "Screen" : app!,
            "n" => counter.ToString("D3", CultureInfo.InvariantCulture),
            _ => m.Value,
        });

        var invalid = Path.GetInvalidFileNameChars();
        result = new string(result.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrEmpty(result) ? "Lumina" : result;
    }
}
