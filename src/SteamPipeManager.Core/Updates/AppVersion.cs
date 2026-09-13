using System.Globalization;

namespace SteamPipeManager.Core.Updates;

/// <summary>
/// Sürüm karşılaştırması. <see cref="Version"/> yetmiyor: GitHub etiketleri "v1.0.2"
/// biçiminde, önsürümler "-beta.1" taşıyabiliyor ve derleme "+abc123" gibi bir
/// kaynak kimliği ekleyebiliyor.
/// </summary>
public sealed record AppVersion(int Major, int Minor, int Patch, string? PreRelease = null)
    : IComparable<AppVersion>
{
    public static AppVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();

        if (value[0] is 'v' or 'V')
        {
            value = value[1..];
        }

        // Derleme meta verisi sıralamaya katılmaz.
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        string? preRelease = null;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = value[(dash + 1)..];
            value = value[..dash];

            if (preRelease.Length == 0)
            {
                return null;
            }
        }

        var parts = value.Split('.');

        // "1.0" ve dosya sürümü biçimindeki "1.0.1.0" da kabul ediliyor; dördüncü
        // bileşen yayın numaralandırmasında kullanılmıyor.
        if (parts.Length is < 2 or > 4)
        {
            return null;
        }

        var numbers = new int[3];

        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            if (i < 3)
            {
                numbers[i] = number;
            }
        }

        return new AppVersion(numbers[0], numbers[1], numbers[2], preRelease);
    }

    public bool IsPreRelease => PreRelease is not null;

    public bool IsNewerThan(AppVersion other) => CompareTo(other) > 0;

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

        if (core != 0)
        {
            return core;
        }

        // Aynı numarada önsürüm, asıl sürümden önce gelir: 1.1.0-beta < 1.1.0.
        return (PreRelease, other.PreRelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            ({ } left, { } right) => ComparePreRelease(left, right),
        };
    }

    /// <summary>SemVer kuralı: nokta ile ayrılmış parçalar; sayısal olanlar sayı olarak.</summary>
    private static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');

        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var aNumber);
            var bNumeric = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bNumber);

            var result = (aNumeric, bNumeric) switch
            {
                (true, true) => aNumber.CompareTo(bNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };

            if (result != 0)
            {
                return result;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() =>
        PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
