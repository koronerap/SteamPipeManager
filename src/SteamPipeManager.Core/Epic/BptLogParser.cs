using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamPipeManager.Core.Epic;

/// <summary>
/// BuildPatchTool'un log dosyasını satır satır olaylara çevirir.
///
/// Biçim ölçümle çıkarıldı (bkz. docs/E0-BPT-FINDINGS.md), dokümantasyondan değil:
///
///   [2026.09.10-13.56.44:462][  0]LogBuildPatchTool: Display: Beginning chunk generation…
///   [2026.09.10-13.56.44:497][  2]LogPatchGeneration: Enumerated 8 files in 197 us
///   [2026.09.10-13.56.44:721][  8]LogDataScanner: @33030145: Scanner completed in 0 us…
///
/// Satır başındaki köşeli parantezli alanların ikincisi motor tick sayacı,
/// <b>yüzde değil</b> — sona doğru 100'e yaklaşması yanıltıcı.
///
/// Ayrıştırıcı durum tutuyor: toplam içerik boyutu verildiğinde tarama ilerlemesini
/// yüzdeye çeviriyor, çünkü araç yüzde bildirmiyor, yalnızca taranan bayt konumunu.
/// </summary>
public sealed partial class BptLogParser
{
    /// <summary>
    /// Build kökünün toplam boyutu. Verildiğinde <c>@konum</c> satırları yüzdeye
    /// çevrilebiliyor; verilmezse ilerleme yalnızca bayt konumu olarak taşınıyor.
    /// </summary>
    public long? TotalBytes { get; set; }

    public BptEvent? Feed(string rawLine)
    {
        if (rawLine is null)
        {
            return null;
        }

        var line = rawLine.TrimEnd();

        if (line.Length == 0)
        {
            return null;
        }

        var (timestamp, category, severity, text) = Split(line);

        BptEvent Event(BptEventKind kind, string message) =>
            new(kind, message, rawLine, timestamp) { Category = category };

        // --- Başarısızlıklar önce: hata satırları başka desenlere de uyabiliyor ---

        if (ExitPattern().Match(text) is { Success: true } exit)
        {
            var reason = exit.Groups["reason"].Value;

            return Event(BptEventKind.Failed, text) with
            {
                ExitReason = reason,
                ExitCode = int.TryParse(exit.Groups["code"].Value, out var code) ? code : null,
            };
        }

        if (text.Contains("Missing credentials", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("client credentials you are using are invalid", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("An authentication error occurred", StringComparison.OrdinalIgnoreCase))
        {
            return Event(BptEventKind.AuthenticationFailed, text);
        }

        if (RequiredArgumentPattern().Match(text) is { Success: true })
        {
            return Event(BptEventKind.ValidationFailed, text);
        }

        // --- Kuru çalıştırma kararı ---
        // -DryRun kendi biçiminde tek satırlık bir karar yazıyor. Ayrıntılı
        // [DRYRUN][ERROR] satırları yalnızca stdout'a gidiyor ama sebepler log'a da
        // ham kategorileriyle düşüyor, dolayısıyla log takibi yeterli.
        if (DryRunResultPattern().Match(text) is { Success: true } dryRun)
        {
            var passed = dryRun.Groups["result"].Value.Equals("PASS", StringComparison.OrdinalIgnoreCase);

            return Event(passed ? BptEventKind.DryRunPassed : BptEventKind.DryRunFailed, text);
        }

        // --- Aşama işaretleri ---

        if (StartedPattern().Match(text) is { Success: true } started)
        {
            return Event(BptEventKind.Started, text) with
            {
                BuildVersion = started.Groups["version"].Value,
                ArtifactId = started.Groups["artifact"].Value,
            };
        }

        if (EnumeratedPattern().Match(text) is { Success: true } enumerated)
        {
            return Event(BptEventKind.FilesEnumerated, text) with
            {
                FileCount = int.TryParse(enumerated.Groups["count"].Value, out var count) ? count : null,
            };
        }

        if (ScanProgressPattern().Match(text) is { Success: true } scan &&
            long.TryParse(scan.Groups["offset"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            return Event(BptEventKind.ScanProgress, text) with
            {
                ByteOffset = offset,
                Percent = TotalBytes is > 0 ? Math.Clamp(offset * 100.0 / TotalBytes.Value, 0, 100) : null,
            };
        }

        if (ManifestSavedPattern().Match(text) is { Success: true })
        {
            return Event(BptEventKind.ManifestSaved, text);
        }

        if (CompletePattern().Match(text) is { Success: true })
        {
            return Event(BptEventKind.Succeeded, text);
        }

        // Yapılandırma bloğu: aracın hangi ayarlarla çalıştığı. Hata ayıklarken
        // en çok işe yarayan kısım, o yüzden ayrı bir tür olarak işaretleniyor.
        if (ConfigurationPattern().Match(text) is { Success: true })
        {
            return Event(BptEventKind.Configuration, text);
        }

        // Sınıflandırılamayan satırlar da olay olarak geçiyor: log paneli ham çıktıyı
        // olduğu gibi gösterebilsin diye hiçbir satır düşürülmüyor.
        _ = severity;

        return Event(BptEventKind.Info, text);
    }

    /// <summary>
    /// Satırı zaman damgası, kategori, önem ve metin olarak ayırır.
    /// Erken satırlarda (motor açılmadan önce) köşeli parantezli önek olmayabiliyor.
    /// </summary>
    private static (DateTimeOffset? Timestamp, string? Category, string? Severity, string Text) Split(string line)
    {
        var rest = line;
        DateTimeOffset? timestamp = null;

        if (PrefixPattern().Match(rest) is { Success: true } prefix)
        {
            timestamp = ParseTimestamp(prefix.Groups["stamp"].Value);
            rest = rest[prefix.Length..];
        }

        string? category = null;
        string? severity = null;

        if (CategoryPattern().Match(rest) is { Success: true } categoryMatch)
        {
            category = categoryMatch.Groups["category"].Value;
            rest = rest[categoryMatch.Length..];

            if (SeverityPattern().Match(rest) is { Success: true } severityMatch)
            {
                severity = severityMatch.Groups["severity"].Value;
                rest = rest[severityMatch.Length..];
            }
        }

        return (timestamp, category, severity, rest.Trim());
    }

    /// <summary>
    /// Unreal biçimi: <c>2026.09.10-13.56.44:462</c>. Yerel saat olarak okunuyor —
    /// dosyanın kendisi saat dilimi taşımıyor.
    /// </summary>
    private static DateTimeOffset? ParseTimestamp(string value) =>
        DateTime.TryParseExact(
            value, "yyyy.MM.dd-HH.mm.ss:fff",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed))
            : null;

    [GeneratedRegex(@"^\[(?<stamp>[\d.\-:]+)\]\[\s*\d+\]")]
    private static partial Regex PrefixPattern();

    [GeneratedRegex(@"^(?<category>[A-Za-z][A-Za-z0-9_]*):\s*")]
    private static partial Regex CategoryPattern();

    [GeneratedRegex(@"^(?<severity>Display|Error|Warning|Verbose|VeryVerbose):\s*")]
    private static partial Regex SeverityPattern();

    /// <summary>Örn. <c>Tool exited with MissingCredentials (9)</c>.</summary>
    [GeneratedRegex(@"Tool exited with (?<reason>[A-Za-z]+)\s*\((?<code>\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ExitPattern();

    /// <summary>Örn. <c>ProductId is required for UploadBinary mode.</c></summary>
    [GeneratedRegex(@"\bis required for\b.*\bmode\b", RegexOptions.IgnoreCase)]
    private static partial Regex RequiredArgumentPattern();

    /// <summary>
    /// Örn. <c>Beginning chunk generation of version 1.2.0 of artifact my-artifact.</c>
    /// Yükleme modunun karşılığı henüz ölçülmedi; "Beginning" ile başlayan diğer
    /// biçimleri de yakalayabilmek için sürüm/artifact isteğe bağlı bırakıldı.
    /// </summary>
    [GeneratedRegex(
        @"Beginning\s+(?:chunk generation|upload)(?:\s+of version\s+(?<version>\S+?)\s+of artifact\s+(?<artifact>\S+?)\.)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex StartedPattern();

    [GeneratedRegex(@"Enumerated\s+(?<count>\d+)\s+files", RegexOptions.IgnoreCase)]
    private static partial Regex EnumeratedPattern();

    /// <summary>Örn. <c>@33030145: Scanner completed in 0 us with 0 collisions.</c></summary>
    [GeneratedRegex(@"^@(?<offset>\d+):\s*Scanner completed", RegexOptions.IgnoreCase)]
    private static partial Regex ScanProgressPattern();

    [GeneratedRegex(@"Saved manifest to\b", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestSavedPattern();

    [GeneratedRegex(@"(?:Chunk generation|Upload) complete\.", RegexOptions.IgnoreCase)]
    private static partial Regex CompletePattern();

    [GeneratedRegex(@"^-+Configuration for\s+(?<mode>\w+)-+$", RegexOptions.IgnoreCase)]
    private static partial Regex ConfigurationPattern();

    /// <summary>Örn. <c>[DRYRUN][RESULT] PASS</c>.</summary>
    [GeneratedRegex(@"\[DRYRUN\]\[RESULT\]\s*(?<result>PASS|FAIL)", RegexOptions.IgnoreCase)]
    private static partial Regex DryRunResultPattern();
}
