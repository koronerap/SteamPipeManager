using System.Text.RegularExpressions;

namespace BptProbe;

/// <summary>
/// BPT'nin genel yardım çıktısındaki mod listesini okur.
///
/// Modları elle listelemek yerine araca sormanın sebebi somut: dokümantasyondaki
/// isimler sürümler arasında değişmiş (1.6'da <c>LabelBuild</c>, 1.8.8'de
/// <c>LabelBinary</c>), ve tahmin edilen isimler sessizce genel yardıma düşüyor.
/// </summary>
public static partial class ModeList
{
    public static IReadOnlyList<string> Parse(string generalHelpOutput)
    {
        var modes = new List<string>();

        foreach (Match match in ModePattern().Matches(generalHelpOutput))
        {
            var mode = match.Groups["mode"].Value;

            if (!modes.Contains(mode, StringComparer.OrdinalIgnoreCase))
            {
                modes.Add(mode);
            }
        }

        return modes;
    }

    /// <summary>
    /// Yardım metnindeki <c>  -mode=UploadBinary    Açıklama…</c> satırları.
    /// Yalnızca satır başındaki tanım satırları alınır; açıklama metninin içinde
    /// geçen mod adları toplanmasın diye satır başı çapası kullanılıyor.
    /// </summary>
    [GeneratedRegex(@"^\s*-mode=(?<mode>[A-Za-z][A-Za-z0-9_]*)\s", RegexOptions.Multiline)]
    private static partial Regex ModePattern();
}
