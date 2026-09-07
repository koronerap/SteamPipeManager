using System.Diagnostics;

using SteamPipeManager.Core.Localization;

namespace SteamPipeManager.Core.SteamCmd;

public enum SessionState
{
    /// <summary>Henüz kontrol edilmedi.</summary>
    Unknown,

    /// <summary>Oturum cache'li; build etkileşimsiz çalışabilir.</summary>
    Active,

    /// <summary>Giriş gerekiyor — görünür konsol login akışı çalıştırılmalı.</summary>
    LoginRequired,

    /// <summary>Kontrol zaman aşımına uğradı ya da beklenmedik bir hata verdi.</summary>
    CheckFailed,
}

public sealed record SessionCheckResult(SessionState State, string Detail)
{
    public bool CanBuild => State == SessionState.Active;

    /// <summary>Giriş satırından okunan hesap numarası; avatar çekmek için kullanılır.</summary>
    public ulong? SteamId64 { get; init; }
}

/// <summary>
/// SteamCMD oturumunu yönetir.
///
/// İlk giriş kasıtlı olarak <b>görünür bir konsol penceresinde</b> yapılır: şifre ve Steam
/// Guard istemleri yönlendirilmiş pipe üzerinden zamanında ulaşmıyor (M0 Bulgu 3) ve bu
/// yöntemde şifre uygulamanın süreç belleğine hiç girmiyor.
/// </summary>
public sealed class SteamCmdSessionService(SteamCmdInstallation installation)
{
    public SteamCmdInstallation Installation { get; } = installation;

    public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Build öncesi zorunlu ön kontrol. Bu adım olmadan, oturumu düşmüş bir hesapla
    /// başlatılan build görünmeyen bir şifre isteminde süresiz bekler.
    /// </summary>
    public async Task<SessionCheckResult> CheckAsync(string steamUsername, CancellationToken ct = default)
    {
        if (!Installation.Exists)
        {
            return new SessionCheckResult(
                SessionState.CheckFailed, Loc.T("SteamCmd.NotFoundAt", Installation.ExecutablePath));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);

        var runner = new SteamCmdRunner(Installation) { StallTimeout = CheckTimeout };

        SteamCmdRunResult result;

        try
        {
            result = await runner.RunAsync($"+login {steamUsername} +quit", ct: timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SessionCheckResult(SessionState.CheckFailed, ex.Message);
        }

        if (result.SawInteractionPrompt)
        {
            return new SessionCheckResult(
                SessionState.LoginRequired, Loc.T("Session.PasswordAsked"));
        }

        if (result.LoginFailure is { } failure)
        {
            var state = failure.FailureReason == LoginFailureReason.RateLimitExceeded
                ? SessionState.CheckFailed
                : SessionState.LoginRequired;

            return new SessionCheckResult(state, failure.Message);
        }

        if (result.Events.FirstOrDefault(e => e.Kind == SteamCmdEventKind.LoginSucceeded) is { } success)
        {
            return new SessionCheckResult(SessionState.Active, Loc.T("Session.Active"))
            {
                SteamId64 = success.SteamAccountId is { } account
                    ? SteamIdUtil.ToSteamId64(account)
                    : null,
            };
        }

        if (result.TimedOut)
        {
            return new SessionCheckResult(
                SessionState.CheckFailed, Loc.T("Session.Timeout"));
        }

        // Giriş satırı görülmediyse emin olamayız; build'i riske atmaktansa giriş istenir.
        return new SessionCheckResult(
            SessionState.LoginRequired,
            Loc.T("Session.NotVerified", result.ExitCode));
    }

    /// <summary>
    /// SteamCMD'yi kendi konsol penceresinde başlatır. Kullanıcı şifresini ve Steam Guard
    /// kodunu doğrudan oraya yazar; uygulama araya girmez.
    /// </summary>
    public Process StartInteractiveLogin(string steamUsername)
    {
        var startInfo = new ProcessStartInfo(Installation.ExecutablePath, $"+login {steamUsername}")
        {
            WorkingDirectory = Installation.Directory,
            // Yönlendirme yok, gizli pencere yok: gerçek bir konsol gerekiyor ki
            // SteamCMD istemlerini göstersin ve klavyeden okuyabilsin.
            UseShellExecute = true,
            CreateNoWindow = false,
        };

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not open the SteamCMD sign-in window.");
    }

    /// <summary>
    /// İlk kurulumda SteamCMD kendini günceller ve <see cref="SteamCmdInstallation.RestartRequiredExitCode"/>
    /// ile çıkar; asıl sonuç ikinci çalıştırmadan gelir (M0 Bulgu 1).
    /// </summary>
    public async Task<bool> BootstrapAsync(
        IProgress<SteamCmdEvent>? progress = null,
        CancellationToken ct = default)
    {
        var runner = new SteamCmdRunner(Installation) { StallTimeout = TimeSpan.FromMinutes(10) };

        var first = await runner.RunAsync("+quit", progress, ct);

        if (first.ExitCode != SteamCmdInstallation.RestartRequiredExitCode)
        {
            return first.ExitCode == 0;
        }

        var second = await runner.RunAsync("+quit", progress, ct);
        return second.ExitCode == 0;
    }
}
