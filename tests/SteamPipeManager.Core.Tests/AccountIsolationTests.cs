using System.ComponentModel;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.SteamCmd;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Tüm profiller tek SteamCMD kurulumunu, dolayısıyla tek <c>console_log.txt</c>'yi
/// paylaşıyor. Bir profilin giriş satırının başka bir profile mal edilmesi, o profile
/// yanlış SteamID'nin (ve yanlış avatarın) yazılması demek — bu testler o yolu kapatıyor.
/// </summary>
public sealed class AccountIsolationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spm_iso_{Guid.NewGuid():N}");

    public AccountIsolationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Login_line_carries_the_account_name()
    {
        var parser = new SteamCmdLogParser();

        var evt = parser.Feed(
            "Logging in user 'example_partner' [U:1:12345] to Steam Public...OK");

        Assert.NotNull(evt);
        Assert.Equal(SteamCmdEventKind.LoginSucceeded, evt.Kind);
        Assert.Equal("example_partner", evt.Username);
        Assert.Equal(12345u, evt.SteamAccountId);
    }

    [Fact]
    public void Account_name_survives_a_split_login_line()
    {
        var parser = new SteamCmdLogParser();

        // Satırın ilk yarısı girişin başladığını bildirir, hesap adı orada da okunur.
        var started = parser.Feed("Logging in user 'example_dev' [U:1:777] to Steam Public...");
        Assert.Equal(SteamCmdEventKind.LoginStarted, started!.Kind);
        Assert.Equal("example_dev", started.Username);

        var evt = parser.Feed("OK");

        Assert.NotNull(evt);
        Assert.Equal(SteamCmdEventKind.LoginSucceeded, evt.Kind);
        Assert.Equal("example_dev", evt.Username);
        Assert.Equal(777u, evt.SteamAccountId);
    }

    [Fact]
    public void Two_accounts_do_not_share_a_steam_id()
    {
        var parser = new SteamCmdLogParser();

        var first = parser.Feed("Logging in user 'example_partner' [U:1:111] to Steam Public...OK");
        var second = parser.Feed("Logging in user 'example_dev' [U:1:222] to Steam Public...OK");

        Assert.NotEqual(
            SteamIdUtil.ToSteamId64(first!.SteamAccountId!.Value),
            SteamIdUtil.ToSteamId64(second!.SteamAccountId!.Value));
    }

    [Fact]
    public void Reset_returns_zero_when_the_log_can_be_deleted()
    {
        var installation = new SteamCmdInstallation(_dir);
        Directory.CreateDirectory(installation.LogsDirectory);
        File.WriteAllText(installation.ConsoleLogPath, "eski içerik\r\n");

        Assert.Equal(0, installation.ResetConsoleLog());
        Assert.False(File.Exists(installation.ConsoleLogPath));
    }

    [Fact]
    public void Reset_skips_the_old_content_when_the_log_is_locked()
    {
        var installation = new SteamCmdInstallation(_dir);
        Directory.CreateDirectory(installation.LogsDirectory);

        var stale = "Logging in user 'example_partner' [U:1:111] to Steam Public...OK\r\n";
        File.WriteAllText(installation.ConsoleLogPath, stale);

        // Kapanmakta olan bir SteamCMD sürecinin dosyayı hâlâ tutmasını taklit eder:
        // silme de boşaltma da başarısız olur.
        using var holder = new FileStream(
            installation.ConsoleLogPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var start = installation.ResetConsoleLog();

        Assert.Equal(new FileInfo(installation.ConsoleLogPath).Length, start);
        Assert.True(start > 0);
    }

    [Fact]
    public async Task Tail_started_past_the_old_content_never_reports_the_previous_account()
    {
        var path = Path.Combine(_dir, "console_log.txt");
        var stale = "Logging in user 'example_partner' [U:1:111] to Steam Public...OK\r\n";
        File.WriteAllText(path, stale);

        var tail = new LogTail(
            path, TimeSpan.FromMilliseconds(10), startPosition: new FileInfo(path).Length);

        File.AppendAllText(path, "Logging in user 'example_dev' [U:1:222] to Steam Public...OK\r\n");

        var lines = new List<string>();
        var done = false;

        await foreach (var line in tail.ReadLinesAsync(() => done))
        {
            lines.Add(line);
            done = true;
        }

        Assert.DoesNotContain(lines, l => l.Contains("example_partner"));
        Assert.Contains(lines, l => l.Contains("example_dev"));
    }
}

/// <summary>
/// Arayüz model nesnelerine doğrudan yazıyor. Otomatik kaydetme ve listelerin
/// tazelenmesi bu bildirimlere dayanıyor.
/// </summary>
public sealed class ModelNotificationTests
{
    private static List<string> Watch(INotifyPropertyChanged model)
    {
        var seen = new List<string>();
        model.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");
        return seen;
    }

    [Fact]
    public void SubApp_reports_edited_fields()
    {
        var subApp = new SubApp();
        var seen = Watch(subApp);

        subApp.Title = "Demo";
        subApp.SteamAppId = 1300020;
        subApp.SetLiveBranch = "beta";
        subApp.Preview = true;
        subApp.ContentRoot = @"D:\content";
        subApp.Kind = SubAppKind.Demo;
        subApp.BuildDescriptionTemplate = "{app}";

        Assert.Equal(
            ["Title", "SteamAppId", "SetLiveBranch", "Preview", "ContentRoot", "Kind",
             "BuildDescriptionTemplate"],
            seen);
    }

    [Fact]
    public void DepotConfig_reports_edited_fields()
    {
        var depot = new DepotConfig();
        var seen = Watch(depot);

        depot.DepotId = 1300021;
        depot.Label = "Windows";
        depot.ContentRoot = @"D:\content";

        Assert.Equal(["DepotId", "Label", "ContentRoot"], seen);
    }

    [Fact]
    public void Setting_the_same_value_stays_quiet()
    {
        var depot = new DepotConfig { Label = "Windows" };
        var seen = Watch(depot);

        depot.Label = "Windows";

        Assert.Empty(seen);
    }

    [Fact]
    public void Depot_list_reports_additions_and_removals()
    {
        var subApp = new SubApp();
        var changes = 0;
        subApp.Depots.CollectionChanged += (_, _) => changes++;

        var depot = new DepotConfig { DepotId = 1 };
        subApp.Depots.Add(depot);
        subApp.Depots.Remove(depot);

        Assert.Equal(2, changes);
    }

    [Fact]
    public void SteamApp_reports_a_rename()
    {
        var app = new SteamApp { Title = "Skyward" };
        var seen = Watch(app);

        app.Title = "Skyward Deluxe";

        Assert.Equal(["Title"], seen);
    }
}
