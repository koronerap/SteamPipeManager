using System.Diagnostics;

using SteamPipeManager.Core.Localization;
using SteamPipeManager.Core.Platform;
using SteamPipeManager.Core.Publishing;

namespace SteamPipeManager.Core.SteamCmd;

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

        // Hesap adı taşıyan bir giriş satırı yalnızca sorduğumuz hesaba aitse kabul edilir:
        // paylaşılan console_log.txt'te başka bir profilin satırı kalmış olabilir.
        var success = result.Events.FirstOrDefault(e =>
            e.Kind == SteamCmdEventKind.LoginSucceeded &&
            (e.Username is not { Length: > 0 } user ||
             user.Equals(steamUsername, StringComparison.OrdinalIgnoreCase)));

        if (success is not null)
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
    /// SteamCMD'yi kendi konsol penceresinde açar ve kapanmasını bekler. Kullanıcı
    /// şifresini ve Steam Guard kodunu doğrudan oraya yazar; uygulama araya girmez.
    ///
    /// Windows'ta konsol penceresi sürecin kendisi. Linux ve macOS'ta bir terminal
    /// uygulaması açılıyor; terminaller genellikle hemen döndüğü için (macOS'ta
    /// Terminal, Linux'ta gnome-terminal) süreç beklenemiyor. Onun yerine betik
    /// bitince bir işaret dosyası bırakıyor ve o bekleniyor.
    /// </summary>
    /// <summary>Terminalde girişin en fazla sürebileceği süre.</summary>
    public static readonly TimeSpan InteractiveLoginLimit = TimeSpan.FromMinutes(15);

    public async Task RunInteractiveLoginAsync(string steamUsername, CancellationToken ct = default)
    {
        if (installation.Platform.IsWindows)
        {
            var startInfo = new ProcessStartInfo(installation.ExecutablePath, $"+login {steamUsername}")
            {
                WorkingDirectory = installation.Directory,
                // Yönlendirme yok, gizli pencere yok: gerçek bir konsol gerekiyor ki
                // SteamCMD istemlerini göstersin ve klavyeden okuyabilsin.
                UseShellExecute = true,
                CreateNoWindow = false,
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not open the SteamCMD sign-in window.");

            await process.WaitForExitAsync(ct);
            return;
        }

        var home = installation.HomeDirectory!;
        Directory.CreateDirectory(home);
        UnixPermissions.EnsureExecutable(installation.ExecutablePath);

        var done = Path.Combine(home, $".spm-login-{Guid.NewGuid():N}.done");
        var script = Path.Combine(home, installation.Platform.IsMac ? "spm-login.command" : "spm-login.sh");

        File.WriteAllText(script, InteractiveLoginScript(installation, steamUsername, done));
        UnixPermissions.EnsureExecutable(script);

        using (TerminalLauncher.Open(script, installation.Platform))
        {
            // Terminal betik bitmeden kapatılırsa işaret hiç gelmez; sonsuza kadar
            // beklememek için üst sınır. Steam Guard onayı için bol bir süre.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(InteractiveLoginLimit);

            while (!File.Exists(done))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), limit.Token);
            }
        }

        TryDelete(done);
    }

    /// <summary>
    /// Terminalde çalışacak betik. Kullanıcı adı tek tırnak içinde: kabuğun onu komut
    /// olarak yorumlamasına izin verilmiyor.
    /// </summary>
    internal static string InteractiveLoginScript(SteamCmdInstallation installation, string steamUsername, string doneMarker) =>
        "#!/bin/sh\n" +
        $"export HOME={ShellQuote(installation.HomeDirectory!)}\n" +
        $"cd {ShellQuote(installation.Directory)}\n" +
        $"{ShellQuote(installation.ExecutablePath)} +login {ShellQuote(steamUsername)} +quit\n" +
        $"touch {ShellQuote(doneMarker)}\n";

    /// <summary>POSIX kabuk için tek tırnaklı değer; içindeki tek tırnak <c>'\''</c> olur.</summary>
    internal static string ShellQuote(string value) => "'" + value.Replace("'", @"'\''") + "'";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Ev dizininde küçük bir artık kalır.
        }
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
