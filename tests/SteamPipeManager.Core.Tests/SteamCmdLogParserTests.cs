using SteamPipeManager.Core.SteamCmd;

namespace SteamPipeManager.Core.Tests;

public class SteamCmdLogParserTests
{
    private static SteamCmdEvent[] Parse(params string[] lines) =>
        [.. new SteamCmdLogParser().FeedAll(lines)];

    [Fact]
    public void Strips_timestamp_prefix()
    {
        var (timestamp, text) = SteamCmdLogParser.SplitTimestamp("[2026-09-06 15:57:10] Loading Steam API...");

        Assert.Equal("Loading Steam API...", text);
        Assert.NotNull(timestamp);
        Assert.Equal(new DateTime(2026, 9, 6, 15, 57, 10), timestamp.Value.DateTime);
    }

    [Fact]
    public void Handles_line_without_timestamp()
    {
        var (timestamp, text) = SteamCmdLogParser.SplitTimestamp("Connecting anonymously to Steam Public...");

        Assert.Null(timestamp);
        Assert.Equal("Connecting anonymously to Steam Public...", text);
    }

    /// <summary>
    /// Gerçek console_log.txt'te ifade ve sonucu ayrı satırlara bölünüyor;
    /// parser bunları birleştirmeli.
    /// </summary>
    [Fact]
    public void Joins_statement_with_outcome_on_next_line()
    {
        var events = Parse(
            "[2026-09-06 15:57:10] Logging in user 'studio_partner' to Steam Public...",
            "[2026-09-06 15:57:13] OK");

        Assert.Equal(SteamCmdEventKind.LoginStarted, events[0].Kind);
        Assert.Equal(SteamCmdEventKind.LoginSucceeded, events[1].Kind);
    }

    [Fact]
    public void Joins_statement_with_failure_on_next_line()
    {
        var events = Parse(
            "[2026-09-06 15:57:10] Logging in user 'studio_partner' to Steam Public...",
            "[2026-09-06 15:57:13] FAILED (Invalid Password)");

        var failure = events.Last();
        Assert.Equal(SteamCmdEventKind.LoginFailed, failure.Kind);
        Assert.Equal(LoginFailureReason.InvalidPassword, failure.FailureReason);
    }

    [Theory]
    [InlineData("FAILED (Invalid Password)", LoginFailureReason.InvalidPassword)]
    [InlineData("FAILED (Rate Limit Exceeded)", LoginFailureReason.RateLimitExceeded)]
    [InlineData("FAILED (Two-factor code mismatch)", LoginFailureReason.TwoFactorMismatch)]
    [InlineData("FAILED (Account Login Denied Need Two Factor)", LoginFailureReason.AccountLoginDeniedNeedTwoFactor)]
    [InlineData("FAILED (Something Else)", LoginFailureReason.Unknown)]
    public void Classifies_login_failure_reasons(string line, LoginFailureReason expected)
    {
        Assert.Equal(expected, Parse(line).Single().FailureReason);
    }

    [Fact]
    public void Captures_build_id_from_success_line()
    {
        var evt = Parse("Successfully finished appID 1200000 build (BuildID 19283746)").Single();

        Assert.Equal(SteamCmdEventKind.BuildSucceeded, evt.Kind);
        Assert.Equal(19283746u, evt.BuildId);
    }

    [Fact]
    public void Captures_depot_id()
    {
        var evt = Parse("Building depot 1200002 ...").Single();

        Assert.Equal(SteamCmdEventKind.DepotProgress, evt.Kind);
        Assert.Equal(1200002u, evt.DepotId);
    }

    [Fact]
    public void Captures_update_state_progress()
    {
        var evt = Parse(" Update state (0x61) downloading, progress: 52.79 (34129768 / 64647928)").Single();

        Assert.Equal(SteamCmdEventKind.DepotProgress, evt.Kind);
        Assert.Equal(52.79, evt.Percent);
    }

    [Theory]
    [InlineData("password:")]
    [InlineData("Cached credentials not found.")]
    public void Flags_password_prompt_as_needing_interaction(string line)
    {
        // Etkileşimsiz bir çalıştırmada bu satırı görmek oturumun düştüğü anlamına gelir.
        Assert.Equal(SteamCmdEventKind.NeedsInteraction, Parse(line).Single().Kind);
    }

    [Theory]
    [InlineData("Steam Guard code:")]
    [InlineData("Two-factor code:")]
    [InlineData("Enter the current code from your Steam Guard Mobile Authenticator app:")]
    public void Flags_guard_prompt_separately(string line)
    {
        // Uygulama içi girişte bu, kullanıcıdan kod isteneceği anlamına gelir.
        Assert.Equal(SteamCmdEventKind.NeedsGuardCode, Parse(line).Single().Kind);
    }

    [Theory]
    [InlineData("This account is protected by a Steam Guard mobile authenticator.")]
    [InlineData("Please confirm the login in the Steam Mobile app on your phone.")]
    [InlineData("Waiting for confirmation...")]
    public void Flags_mobile_confirmation(string line)
    {
        // Bu akışta yazılacak kod yok; telefondan onay bekleniyor.
        Assert.Equal(SteamCmdEventKind.NeedsMobileConfirmation, Parse(line).Single().Kind);
    }

    /// <summary>
    /// Gerçek çıktıda giriş hatası "ERROR (...)" biçiminde geliyor, "FAILED" değil.
    /// Ayrıca hata metni "Two-factor code" içerdiğinde kod isteği sanılmamalı.
    /// </summary>
    [Theory]
    [InlineData("Logging in user 'x' [U:1:0] to Steam Public...ERROR (Invalid Password)", LoginFailureReason.InvalidPassword)]
    [InlineData("ERROR (Rate Limit Exceeded)", LoginFailureReason.RateLimitExceeded)]
    [InlineData("FAILED (Two-factor code mismatch)", LoginFailureReason.TwoFactorMismatch)]
    public void Real_error_form_is_recognised(string line, LoginFailureReason expected)
    {
        var evt = Parse(line).Last();

        Assert.Equal(SteamCmdEventKind.LoginFailed, evt.Kind);
        Assert.Equal(expected, evt.FailureReason);
    }

    [Fact]
    public void Run_errors_are_not_login_failures()
    {
        Assert.Equal(SteamCmdEventKind.Error, Parse("ERROR! Failed to init SteamAPI").Single().Kind);
    }

    [Fact]
    public void Detects_scanning_and_uploading_phases()
    {
        var events = Parse("Scanning content", "Uploading content...");

        Assert.Equal(SteamCmdEventKind.ScanningContent, events[0].Kind);
        Assert.Equal(SteamCmdEventKind.UploadingContent, events[1].Kind);
    }

    /// <summary>
    /// M0 Bulgu 5: güncelleyici katmanı sistem diline göre yerelleşiyor.
    /// Bu satırlar sınıflandırılmamalı, sadece bilgi olarak akmalı.
    /// </summary>
    [Theory]
    [InlineData("[  0%] Güncellemeler denetleniyor...")]
    [InlineData("[----] Yükleme doğrulanıyor...")]
    [InlineData("[----] Update complete, launching...")]
    public void Localized_bootstrapper_lines_stay_informational(string line)
    {
        Assert.Equal(SteamCmdEventKind.Info, Parse(line).Single().Kind);
    }

    [Fact]
    public void Ignores_blank_lines()
    {
        Assert.Empty(Parse("", "   ", "[2026-09-06 15:57:10] "));
    }

    /// <summary>Gerçek bir anonim oturum logunun tamamı (M0'da kaydedildi).</summary>
    [Fact]
    public void Parses_recorded_real_session_log()
    {
        var events = Parse(
            "[2026-09-06 15:57:10] Client version: 1788292693",
            "[2026-09-06 15:57:10] Loading Steam API...",
            "[2026-09-06 15:57:10] OK",
            "[2026-09-06 15:57:10] ",
            "Connecting anonymously to Steam Public...",
            "[2026-09-06 15:57:11] OK",
            "[2026-09-06 15:57:11] Waiting for client config...",
            "[2026-09-06 15:57:13] OK",
            "[2026-09-06 15:57:13] Waiting for user info...",
            "[2026-09-06 15:57:13] OK",
            "[2026-09-06 15:57:15] Success! App '1007' fully installed.",
            "[2026-09-06 15:57:15] Unloading Steam API...");

        Assert.Contains(events, e => e.Kind == SteamCmdEventKind.LoginStarted);
        Assert.Contains(events, e => e.Kind == SteamCmdEventKind.LoginSucceeded);
        Assert.DoesNotContain(events, e => e.IsFailure);
        Assert.DoesNotContain(events, e => e.Kind == SteamCmdEventKind.NeedsInteraction);
    }
}
