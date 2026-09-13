using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.SteamCmd;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Sahte steamcmd üzerinden uçtan uca: süreç başlatma, canlı log takibi, olay
/// ayrıştırma, donma dedektörü ve iptal. Gerçek Steam hesabı gerekmez.
/// </summary>
[Collection("SteamCmd")]
public class SteamCmdRunnerTests
{
    private static CancellationToken Timeout(int seconds = 30) =>
        new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    [Fact]
    public async Task Successful_build_reports_build_id()
    {
        using var fake = FakeSteamCmd.Create("success");
        var runner = new SteamCmdRunner(fake.Installation);

        var result = await runner.RunAsync("+quit", ct: Timeout());

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.NotNull(result.SuccessEvent);
        Assert.Equal(4242u, result.SuccessEvent!.BuildId);
    }

    [Fact]
    public async Task Streams_events_while_running()
    {
        using var fake = FakeSteamCmd.Create("success");
        var runner = new SteamCmdRunner(fake.Installation);
        var seen = new List<SteamCmdEventKind>();

        var progress = new Progress<SteamCmdEvent>(e => seen.Add(e.Kind));
        var result = await runner.RunAsync("+quit", progress, Timeout());

        Assert.Contains(SteamCmdEventKind.LoginSucceeded, result.Events.Select(e => e.Kind));
        Assert.Contains(SteamCmdEventKind.ScanningContent, result.Events.Select(e => e.Kind));
        Assert.Contains(SteamCmdEventKind.UploadingContent, result.Events.Select(e => e.Kind));
    }

    [Fact]
    public async Task Login_failure_is_surfaced_with_reason()
    {
        using var fake = FakeSteamCmd.Create("loginfail");
        var runner = new SteamCmdRunner(fake.Installation);

        var result = await runner.RunAsync("+quit", ct: Timeout());

        Assert.Null(result.SuccessEvent);
        Assert.NotNull(result.LoginFailure);
        Assert.Equal(LoginFailureReason.InvalidPassword, result.LoginFailure!.FailureReason);
    }

    /// <summary>
    /// Oturum düştüğünde SteamCMD görünmeyen bir istemde sonsuza kadar bekler;
    /// runner bunu görüp süreci öldürmeli, kilitlenmemeli.
    /// </summary>
    [Fact]
    public async Task Interaction_prompt_aborts_instead_of_hanging()
    {
        using var fake = FakeSteamCmd.Create("prompt");
        var runner = new SteamCmdRunner(fake.Installation);

        var start = DateTimeOffset.UtcNow;
        var result = await runner.RunAsync("+quit", ct: Timeout());
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.True(result.SawInteractionPrompt);
        Assert.True(elapsed < TimeSpan.FromSeconds(20), $"çok uzun sürdü: {elapsed}");
    }

    /// <summary>Log büyümeyi kesince çalıştırma sonlandırılmalı.</summary>
    [Fact]
    public async Task Stalled_run_is_killed_by_watchdog()
    {
        using var fake = FakeSteamCmd.Create("stall");
        var runner = new SteamCmdRunner(fake.Installation) { StallTimeout = TimeSpan.FromSeconds(3) };

        var start = DateTimeOffset.UtcNow;
        var result = await runner.RunAsync("+quit", ct: Timeout());
        var elapsed = DateTimeOffset.UtcNow - start;

        Assert.True(result.TimedOut);
        Assert.Null(result.SuccessEvent);
        Assert.True(elapsed < TimeSpan.FromSeconds(20), $"çok uzun sürdü: {elapsed}");
    }

    [Fact]
    public async Task Cancellation_stops_the_run()
    {
        using var fake = FakeSteamCmd.Create("stall");
        var runner = new SteamCmdRunner(fake.Installation) { StallTimeout = TimeSpan.FromMinutes(5) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync("+quit", ct: cts.Token);

        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task Build_error_is_reported()
    {
        using var fake = FakeSteamCmd.Create("buildfail");
        var runner = new SteamCmdRunner(fake.Installation);

        var result = await runner.RunAsync("+quit", ct: Timeout());

        Assert.Equal(8, result.ExitCode);
        Assert.Null(result.SuccessEvent);
        Assert.Contains(result.Events, e => e.Kind == SteamCmdEventKind.Error);
    }

    [Fact]
    public async Task Raw_stdout_is_captured_for_the_archive()
    {
        using var fake = FakeSteamCmd.Create("success");
        var runner = new SteamCmdRunner(fake.Installation);

        var result = await runner.RunAsync("+quit", ct: Timeout());

        Assert.Contains("Successfully finished appID", result.RawOutput);
    }

    [Fact]
    public async Task Session_check_accepts_active_session()
    {
        using var fake = FakeSteamCmd.Create("success");
        var service = new SteamCmdSessionService(fake.Installation);

        var check = await service.CheckAsync("tester", Timeout());

        Assert.Equal(SessionState.Active, check.State);
        Assert.True(check.CanBuild);
    }

    [Fact]
    public async Task Session_check_requires_login_when_prompted()
    {
        using var fake = FakeSteamCmd.Create("prompt");
        var service = new SteamCmdSessionService(fake.Installation) { CheckTimeout = TimeSpan.FromSeconds(20) };

        var check = await service.CheckAsync("tester", Timeout(40));

        Assert.Equal(SessionState.LoginRequired, check.State);
        Assert.False(check.CanBuild);
    }

    [Fact]
    public async Task Session_check_treats_rate_limit_as_check_failure()
    {
        // Rate limit'te tekrar giriş denemek durumu kötüleştirir; giriş akışına yönlendirilmez.
        using var fake = FakeSteamCmd.Create("ratelimit");
        var service = new SteamCmdSessionService(fake.Installation);

        var check = await service.CheckAsync("tester", Timeout());

        Assert.Equal(SessionState.CheckFailed, check.State);
    }

    [Fact]
    public async Task Session_check_reports_missing_executable()
    {
        var missing = new SteamCmdInstallation(
            Path.Combine(Path.GetTempPath(), $"yok_{Guid.NewGuid():N}", "steamcmd.exe"));

        var check = await new SteamCmdSessionService(missing).CheckAsync("tester", Timeout());

        Assert.Equal(SessionState.CheckFailed, check.State);
        Assert.Contains("SteamCmd.NotFoundAt", check.Detail);
    }

    /// <summary>Kurulum sırasında exit 7 hata değil; ikinci çalıştırma yapılmalı (M0 Bulgu 1).</summary>
    [Fact]
    public async Task Bootstrap_retries_after_restart_exit_code()
    {
        using var fake = FakeSteamCmd.Create("restart");
        var service = new SteamCmdSessionService(fake.Installation);

        // "restart" senaryosu 7 ile çıkar ve kendini "success"e çevirir;
        // bootstrap ikinci çalıştırmayı yapıp başarıyı görmeli.
        Assert.True(await service.BootstrapAsync(ct: Timeout(60)));
    }
}

[CollectionDefinition("SteamCmd", DisableParallelization = true)]
public class SteamCmdCollection;
