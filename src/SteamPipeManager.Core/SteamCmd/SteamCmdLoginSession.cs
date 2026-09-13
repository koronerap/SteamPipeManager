using System.Diagnostics;
using System.Text;

using SteamPipeManager.Core.Localization;

using SteamPipeManager.Core.Publishing;

namespace SteamPipeManager.Core.SteamCmd;

public enum LoginStage
{
    Starting,
    SigningIn,

    /// <summary>Kullanıcıdan Steam Guard kodu bekleniyor.</summary>
    AwaitingGuardCode,

    /// <summary>Telefondaki Steam uygulamasından onay bekleniyor; yazılacak kod yok.</summary>
    AwaitingMobileConfirmation,

    Succeeded,
    Failed,
    Cancelled,
}

public sealed record LoginProgress(LoginStage Stage, string Message);

public sealed record LoginResult(
    LoginStage Stage,
    string Message,
    ulong? SteamId64 = null,
    LoginFailureReason? Reason = null)
{
    public bool Succeeded => Stage == LoginStage.Succeeded;
}

/// <summary>
/// Girişi tamamen uygulama içinden yürütür — görünür konsol penceresi olmadan.
///
/// Ölçüm sonucu (bkz. docs/M0-FINDINGS.md Bulgu 8): SteamCMD'nin <b>çıktısı</b> pipe'a
/// bağlıyken tamponlanıyor ama <b>girdisi</b> tamponsuz. Şifre istem beklenmeden stdin'e
/// yazıldığında SteamCMD onu ihtiyaç duyduğu anda okuyor. İlerleme yine
/// <c>console_log.txt</c> üzerinden izleniyor.
///
/// Şifre yalnızca bu çağrının süresi boyunca bellekte kalır; diske yazılmaz ve
/// komut satırına konmaz (process listesinde görünürdü).
/// </summary>
public sealed class SteamCmdLoginSession(SteamCmdInstallation installation)
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Steam Guard kodu istendiğinde çağrılır; null dönerse giriş iptal edilir.</summary>
    public required Func<CancellationToken, Task<string?>> GuardCodeProvider { get; init; }

    public async Task<LoginResult> LoginAsync(
        string username,
        string password,
        IProgress<LoginProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!installation.Exists)
        {
            return new LoginResult(
                LoginStage.Failed, Loc.T("SteamCmd.NotFoundAt", installation.ExecutablePath));
        }

        var logStart = installation.ResetConsoleLog();
        Directory.CreateDirectory(installation.LogsDirectory);

        var startInfo = new ProcessStartInfo(installation.ExecutablePath, $"+login {username} +quit")
        {
            WorkingDirectory = installation.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start SteamCMD.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        progress?.Report(new LoginProgress(LoginStage.Starting, Loc.T("Login.Starting")));

        // Şifre istem beklenmeden yazılıyor: istem bize zamanında ulaşmıyor ama
        // SteamCMD ihtiyaç duyduğunda stdin'den okuyor.
        await process.StandardInput.WriteLineAsync(password);
        await process.StandardInput.FlushAsync(timeout.Token);

        // stdout okunmazsa pipe dolup süreci bloklayabilir.
        var drain = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var drainError = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var result = await WatchAsync(process, username, logStart, progress, timeout);

        if (!process.HasExited)
        {
            KillQuietly(process);
        }

        await Task.WhenAll(drain, drainError);
        await process.WaitForExitAsync(CancellationToken.None);

        return result;
    }

    private async Task<LoginResult> WatchAsync(
        Process process,
        string username,
        long logStart,
        IProgress<LoginProgress>? progress,
        CancellationTokenSource timeout)
    {
        var parser = new SteamCmdLogParser();
        var tail = new LogTail(
            installation.ConsoleLogPath, TimeSpan.FromMilliseconds(200), logStart);
        var guardRequested = false;

        progress?.Report(new LoginProgress(LoginStage.SigningIn, Loc.T("Login.Connecting")));

        try
        {
            await foreach (var line in tail.ReadLinesAsync(() => process.HasExited, timeout.Token))
            {
                if (parser.Feed(line) is not { } evt)
                {
                    continue;
                }

                // console_log.txt tüm profiller arasında paylaşılıyor. Silinemediği bir
                // durumda önceki hesabın satırları okunabilir; o satırlara dayanıp
                // yanlış SteamID'yi (ve dolayısıyla yanlış avatarı) profile yazmamak için
                // hesap adı eşleşmeyen giriş olayları atlanıyor.
                if (evt.Username is { Length: > 0 } lineUser &&
                    !lineUser.Equals(username, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                switch (evt.Kind)
                {
                    case SteamCmdEventKind.LoginSucceeded:
                        return new LoginResult(
                            LoginStage.Succeeded,
                            Loc.T("Login.Success"),
                            evt.SteamAccountId is { } account ? SteamIdUtil.ToSteamId64(account) : null);

                    case SteamCmdEventKind.LoginFailed:
                        return new LoginResult(LoginStage.Failed, evt.Message, Reason: evt.FailureReason);

                    case SteamCmdEventKind.NeedsMobileConfirmation:
                        progress?.Report(new LoginProgress(
                            LoginStage.AwaitingMobileConfirmation, evt.Message));
                        break;

                    case SteamCmdEventKind.NeedsGuardCode when !guardRequested:
                        guardRequested = true;

                        progress?.Report(new LoginProgress(
                            LoginStage.AwaitingGuardCode, Loc.T("Login.GuardCode")));

                        var code = await GuardCodeProvider(timeout.Token);

                        if (code is null)
                        {
                            KillQuietly(process);
                            return new LoginResult(LoginStage.Cancelled, Loc.T("Login.Cancelled"));
                        }

                        await process.StandardInput.WriteLineAsync(code);
                        await process.StandardInput.FlushAsync(timeout.Token);

                        progress?.Report(new LoginProgress(LoginStage.SigningIn, Loc.T("Login.CodeSent")));
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);

            return new LoginResult(
                LoginStage.Failed,
                Loc.T("Login.Timeout"));
        }

        // Log satırlarından kesin sonuç çıkmadıysa exit code'a bakılır.
        return process.HasExited && process.ExitCode == 0
            ? new LoginResult(LoginStage.Succeeded, Loc.T("Login.Success"))
            : new LoginResult(
                LoginStage.Failed,
                Loc.T("Session.NotVerified", process.HasExited ? process.ExitCode : -1));
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
