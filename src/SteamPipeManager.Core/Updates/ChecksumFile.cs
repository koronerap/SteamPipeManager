using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SteamPipeManager.Core.Updates;

/// <summary>
/// <c>sha256sum</c> biçimindeki sağlama dosyası: her satırda
/// <c>&lt;64 hex&gt;  &lt;dosya adı&gt;</c> (ikili kip için ad önünde <c>*</c>).
/// </summary>
public static partial class ChecksumFile
{
    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>\S.*?)\s*$")]
    private static partial Regex LinePattern();

    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            var match = LinePattern().Match(raw.TrimEnd('\r'));

            if (match.Success)
            {
                result[match.Groups["name"].Value] = match.Groups["hash"].Value.ToLowerInvariant();
            }
        }

        return result;
    }

    public static string Format(string hash, string fileName) => $"{hash.ToLowerInvariant()}  {fileName}";

    public static string ComputeFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
