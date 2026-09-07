using System.Globalization;
using System.Text.RegularExpressions;

using SteamPipeManager.Core.Localization;

namespace SteamPipeManager.Core.SteamCmd;

/// <summary>
/// <c>console_log.txt</c> satırlarını tipli olaylara çevirir.
///
/// Durum tutar, çünkü SteamCMD bir ifadeyi ve sonucunu ayrı satırlara bölebiliyor:
/// <code>
/// [2026-09-06 15:57:10] Loading Steam API...
/// [2026-09-06 15:57:10] OK
/// </code>
/// Sonuç satırı geldiğinde bekleyen ifadeyle birleştirilir.
///
/// Desenler yalnızca Steam <b>istemci</b> katmanının İngilizce çıktısına dayanır;
/// güncelleyici katmanı sistem diline göre yerelleştiği için (M0 Bulgu 5) oradan
/// hiçbir desen kullanılmaz.
/// </summary>
public sealed partial class SteamCmdLogParser
{
    private string? _pendingStatement;

    /// <summary>Bir satır hiç olay üretmeyebilir (bekleyen ifade olarak tutulur).</summary>
    public SteamCmdEvent? Feed(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return null;
        }

        var (timestamp, text) = SplitTimestamp(rawLine);
        text = text.Trim();

        if (text.Length == 0)
        {
            return null;
        }

        // SteamPipe ilerlemeyi nokta nokta basıyor; yüzde içermeyen saf nokta satırları
        // ("....." gibi) log panelini doldurmaktan başka bir şey yapmıyor.
        if (text.All(c => c == '.'))
        {
            return null;
        }

        // Önceki satırın sonucu olarak gelen "OK" / "FAILED ..." birleştirilir.
        if (IsOutcomeOnly(text) && _pendingStatement is { } pending)
        {
            _pendingStatement = null;
            return Classify($"{pending}{text}", rawLine, timestamp);
        }

        // "...", ":" veya boşlukla biten satırlar sonucu bir sonraki satırda gelir.
        if (text.EndsWith("...", StringComparison.Ordinal))
        {
            _pendingStatement = text;
            return Classify(text, rawLine, timestamp);
        }

        _pendingStatement = null;
        return Classify(text, rawLine, timestamp);
    }

    public IEnumerable<SteamCmdEvent> FeedAll(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (Feed(line) is { } evt)
            {
                yield return evt;
            }
        }
    }

    private static bool IsOutcomeOnly(string text) =>
        text is "OK" || text.StartsWith("FAILED", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Satır başındaki <c>[yyyy-MM-dd HH:mm:ss]</c> damgalarını ayırır.
    ///
    /// Damga <b>birden fazla</b> olabiliyor: SteamPipe kendi build satırlarını
    /// <c>[tarih]: </c> önekiyle yazıyor, steamcmd de console_log.txt'e yazarken
    /// kendi damgasını başa ekliyor. Gerçek çıktıda şöyle görünüyor:
    /// <code>[2026-09-06 18:26:07] [2026-09-06 18:26:07]: Successfully finished …</code>
    /// </summary>
    internal static (DateTimeOffset? Timestamp, string Text) SplitTimestamp(string rawLine)
    {
        DateTimeOffset? timestamp = null;
        var text = rawLine;

        while (TimestampPattern().Match(text) is { Success: true } match)
        {
            // İlk damga steamcmd'nin; sonrakiler SteamPipe'ın kendi önekleri.
            timestamp ??= DateTimeOffset.TryParseExact(
                match.Groups["ts"].Value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var value)
                ? value
                : null;

            text = text[match.Length..];
        }

        return (timestamp, text);
    }

    private static SteamCmdEvent Classify(string text, string rawLine, DateTimeOffset? timestamp)
    {
        SteamCmdEvent Event(SteamCmdEventKind kind, string? message = null) =>
            new(kind, message ?? text, rawLine, timestamp);

        if (BuildSucceededPattern().Match(text) is { Success: true } success)
        {
            // Preview build'lerde BuildID yok: hiçbir içerik yüklenmediği için Steam
            // bir build kaydı oluşturmuyor. Gerçek çıktı:
            //   "Successfully finished AppID 1300000 build preview."
            return Event(SteamCmdEventKind.BuildSucceeded) with
            {
                BuildId = uint.TryParse(success.Groups["build"].Value, out var buildId) ? buildId : null,
            };
        }

        if (BuildStartedPattern().Match(text) is { Success: true } started)
        {
            return Event(SteamCmdEventKind.BuildStarted) with
            {
                BuildId = null,
            };
        }

        // SteamPipe ilerlemeyi "......... 668.1MB (93%)" biçiminde basıyor.
        if (ScanProgressPattern().Match(text) is { Success: true } scan)
        {
            return Event(
                SteamCmdEventKind.DepotProgress,
                scan.Value.Trim()) with
            {
                Percent = double.TryParse(
                    scan.Groups["pct"].Value, CultureInfo.InvariantCulture, out var scanned)
                    ? scanned
                    : null,
            };
        }

        // Hata kontrolü Steam Guard istemlerinden ÖNCE gelmeli: "FAILED (Two-factor code
        // mismatch)" gibi bir hata mesajı da "Two-factor code" içeriyor ve sıralama ters
        // olduğunda kod isteği sanılıyordu.
        // "ERROR!" ile başlayan satırlar ise giriş değil çalıştırma hatasıdır.
        if (!text.StartsWith("ERROR!", StringComparison.OrdinalIgnoreCase) &&
            LoginFailurePattern().Match(text) is { Success: true } failure)
        {
            return Event(SteamCmdEventKind.LoginFailed) with
            {
                FailureReason = ClassifyFailure(failure.Groups["reason"].Value),
            };
        }

        // Steam Guard iki farklı akış üretiyor ve ikisi de "mobile authenticator" ifadesini
        // içerebiliyor. Ayırt edici olan istem biçimi: kod istenirken satır iki nokta ile
        // biter ("...Authenticator app:"), telefon onayında ise düz bir bilgi cümlesidir.
        var looksLikePrompt =
            text.EndsWith(':') ||
            text.Contains("Enter the current code", StringComparison.OrdinalIgnoreCase);

        if (looksLikePrompt &&
            (text.Contains("Steam Guard", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("Two-factor code", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("authenticator", StringComparison.OrdinalIgnoreCase)))
        {
            return Event(SteamCmdEventKind.NeedsGuardCode, "Steam Guard kodu gerekiyor.");
        }

        if (text.Contains("mobile authenticator", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Steam Mobile app", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Waiting for confirmation", StringComparison.OrdinalIgnoreCase))
        {
            return Event(
                SteamCmdEventKind.NeedsMobileConfirmation,
                Loc.T("Login.Mobile.Title"));
        }

        if (text.Contains("Logged in OK", StringComparison.OrdinalIgnoreCase))
        {
            return Event(SteamCmdEventKind.LoginSucceeded);
        }

        // Giriş ifadesi ile sonucu ayrı satırlarda gelebildiği için, birleştirilmiş
        // metnin "OK" ile bitmesi başarıyı gösterir. Anonim giriş de aynı yolu izler.
        var isLoginStatement =
            text.StartsWith("Logging in user", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Connecting anonymously", StringComparison.OrdinalIgnoreCase);

        if (isLoginStatement)
        {
            // Giriş satırı hesap numarasını da taşıyor:
            //   Logging in user 'example_partner' [U:1:000000000] to Steam Public...OK
            var accountId = SteamAccountPattern().Match(text) is { Success: true } account &&
                            uint.TryParse(account.Groups["acct"].Value, out var parsed)
                ? parsed
                : (uint?)null;

            return Event(text.EndsWith("OK", StringComparison.Ordinal)
                ? SteamCmdEventKind.LoginSucceeded
                : SteamCmdEventKind.LoginStarted) with
            {
                SteamAccountId = accountId,
            };
        }

        // Yönlendirilmiş çalıştırmada bu satırı görmek oturumun düştüğü anlamına gelir:
        // SteamCMD girdi bekliyor ama biz cevap veremiyoruz (M0 Bulgu 3).
        if (text.Contains("password:", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Cached credentials not found", StringComparison.OrdinalIgnoreCase))
        {
            return Event(
                SteamCmdEventKind.NeedsInteraction,
                Loc.T("Session.PasswordAsked"));
        }

        if (DepotBuildPattern().Match(text) is { Success: true } depot)
        {
            return Event(SteamCmdEventKind.DepotProgress) with
            {
                DepotId = uint.TryParse(depot.Groups["depot"].Value, out var depotId) ? depotId : null,
            };
        }

        if (text.Contains("Scanning content", StringComparison.OrdinalIgnoreCase))
        {
            return Event(SteamCmdEventKind.ScanningContent);
        }

        if (text.Contains("Uploading content", StringComparison.OrdinalIgnoreCase))
        {
            return Event(SteamCmdEventKind.UploadingContent);
        }

        if (UpdateStatePattern().Match(text) is { Success: true } state)
        {
            return Event(SteamCmdEventKind.DepotProgress) with
            {
                Percent = double.TryParse(
                    state.Groups["pct"].Value, CultureInfo.InvariantCulture, out var pct)
                    ? pct
                    : null,
            };
        }

        if (text.StartsWith("ERROR!", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Failed to ", StringComparison.OrdinalIgnoreCase))
        {
            return Event(SteamCmdEventKind.Error);
        }

        return Event(SteamCmdEventKind.Info);
    }

    private static LoginFailureReason ClassifyFailure(string reason) => reason switch
    {
        var r when r.Contains("Invalid Password", StringComparison.OrdinalIgnoreCase)
            => LoginFailureReason.InvalidPassword,
        var r when r.Contains("Rate Limit", StringComparison.OrdinalIgnoreCase)
            => LoginFailureReason.RateLimitExceeded,
        var r when r.Contains("Two-factor", StringComparison.OrdinalIgnoreCase)
            => LoginFailureReason.TwoFactorMismatch,
        var r when r.Contains("Account Login Denied", StringComparison.OrdinalIgnoreCase)
            => LoginFailureReason.AccountLoginDeniedNeedTwoFactor,
        _ => LoginFailureReason.Unknown,
    };

    /// <summary>Damgadan sonra iki nokta gelebilir: SteamPipe <c>[tarih]: </c> yazıyor.</summary>
    [GeneratedRegex(@"^\[(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]:?[ \t]*")]
    private static partial Regex TimestampPattern();

    /// <summary>
    /// Gerçek çıktıda iki biçim var:
    ///   "Successfully finished AppID 1300000 build preview."   (preview — BuildID yok)
    ///   "Successfully finished AppID 1000 build (BuildID 12345)" (gerçek build)
    /// </summary>
    [GeneratedRegex(
        @"Successfully finished appID (?<app>\d+) build(?:\s+preview)?(?:\s*\(BuildID (?<build>\d+)\))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex BuildSucceededPattern();

    [GeneratedRegex(@"\[U:1:(?<acct>\d+)\]")]
    private static partial Regex SteamAccountPattern();

    [GeneratedRegex(@"Starting appID (?<app>\d+) build", RegexOptions.IgnoreCase)]
    private static partial Regex BuildStartedPattern();

    /// <summary>SteamPipe tarama/yükleme ilerlemesi: <c>......... 668.1MB (93%)</c></summary>
    [GeneratedRegex(@"(?<size>[\d.]+)\s*(?<unit>[KMGT]B)\s*\((?<pct>\d+)%\)", RegexOptions.IgnoreCase)]
    private static partial Regex ScanProgressPattern();

    /// <summary>
    /// Gerçek çıktıda giriş hatası <c>ERROR (Invalid Password)</c> biçiminde geliyor;
    /// dokümantasyondaki <c>FAILED (...)</c> biçimi de destekleniyor.
    /// Parantezli sebep ya da satır sonu şart: aksi halde "Failed to init SteamAPI" gibi
    /// sıradan hata metinleri login hatası sanılırdı.
    /// </summary>
    [GeneratedRegex(@"\b(?:FAILED|ERROR)\b[ \t]*(?:\((?<reason>[^)]*)\)|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LoginFailurePattern();

    [GeneratedRegex(@"Building depot (?<depot>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DepotBuildPattern();

    [GeneratedRegex(@"Update state \(0x[0-9a-fA-F]+\) [^,]+, progress: (?<pct>[\d.]+)")]
    private static partial Regex UpdateStatePattern();
}
