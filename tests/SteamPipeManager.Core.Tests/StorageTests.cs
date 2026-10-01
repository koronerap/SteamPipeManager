using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.Core.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"spm_store_{Guid.NewGuid():N}");

    private WorkspaceLayout Layout => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_file_yields_empty_database()
    {
        var db = await new JsonProfileStore(Layout).LoadAsync();

        Assert.Empty(db.Profiles);

        // Yeni bir veritabanı güncel şemayla doğar; sürüm sabiti değiştiğinde bu da
        // onunla birlikte gelmeli, yoksa sabit bir sayı testi her göçte kırılır.
        Assert.Equal(ProfileSchema.Current, db.SchemaVersion);
    }

    [Fact]
    public async Task Round_trips_full_profile_tree()
    {
        var store = new JsonProfileStore(Layout);

        var db = new ProfileDatabase
        {
            Profiles =
            [
                new UserProfile
                {
                    DisplayName = "Studio Ana Hesap",
                    SteamUsername = "studio_user",
                    Apps =
                    [
                        new SteamApp
                        {
                            Title = "Pixel Racer",
                            SubApps =
                            [
                                new SubApp
                                {
                                    Title = "Ana Oyun",
                                    Kind = SubAppKind.Main,
                                    SteamAppId = 1300000,
                                    SetLiveBranch = "debug",
                                    Depots =
                                    [
                                        new DepotConfig
                                        {
                                            DepotId = 1300001,
                                            Label = "Windows",
                                            ContentRoot = @"D:\SteamPipe (Pixel Racer)\tools\ContentBuilder\content\Windows",
                                            FileExclusions = ["*.pdb"],
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        await store.SaveAsync(db);
        var loaded = await store.LoadAsync();

        var subApp = loaded.Profiles.Single().Apps.Single().SubApps.Single();
        Assert.Equal("Ana Oyun", subApp.Title);
        Assert.Equal(SubAppKind.Main, subApp.Kind);
        Assert.Equal(1300000u, subApp.SteamAppId);
        Assert.Equal("debug", subApp.SetLiveBranch);

        var depot = subApp.Depots.Single();
        Assert.Equal("Windows", depot.Label);
        Assert.Equal(@"D:\SteamPipe (Pixel Racer)\tools\ContentBuilder\content\Windows", depot.ContentRoot);
        Assert.Equal(["*.pdb"], depot.FileExclusions);
    }

    [Fact]
    public async Task Enums_are_written_as_readable_names()
    {
        var store = new JsonProfileStore(Layout);

        await store.SaveAsync(new ProfileDatabase
        {
            Profiles = [new UserProfile { Apps = [new SteamApp { SubApps = [new SubApp { Kind = SubAppKind.Playtest }] }] }],
        });

        var json = await File.ReadAllTextAsync(store.FilePath);

        Assert.Contains("\"Playtest\"", json);
        Assert.DoesNotContain("\"kind\": 2", json);
    }

    [Fact]
    public async Task Windows_paths_stay_readable_in_json()
    {
        var store = new JsonProfileStore(Layout);

        await store.SaveAsync(new ProfileDatabase
        {
            Profiles =
            [
                new UserProfile
                {
                    Apps = [new SteamApp { SubApps = [new SubApp { ContentRoot = @"D:\SteamPipe (Demo)\content" }] }],
                },
            ],
        });

        var json = await File.ReadAllTextAsync(store.FilePath);

        // UnsafeRelaxedJsonEscaping sayesinde parantez ve tırnak kaçışları dosyayı okunmaz hale getirmiyor.
        Assert.Contains(@"D:\\SteamPipe (Demo)\\content", json);
        Assert.DoesNotContain("\\u0028", json);
    }

    [Fact]
    public async Task Corrupt_file_reports_which_file_failed()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Layout.ProfilesFile, "{ bu json değil");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => new JsonProfileStore(Layout).LoadAsync());

        Assert.Contains("profiles.json", ex.Message);
    }

    [Fact]
    public async Task Save_leaves_no_temp_file_behind()
    {
        var store = new JsonProfileStore(Layout);
        await store.SaveAsync(new ProfileDatabase());

        Assert.False(File.Exists(store.FilePath + ".tmp"));
        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task Settings_round_trip_with_defaults()
    {
        var store = new JsonSettingsStore(Layout);

        var defaults = await store.LoadAsync();
        Assert.Equal(AppTheme.System, defaults.Theme);
        Assert.False(defaults.SetupCompleted);
        Assert.Equal(120, defaults.StallWarningSeconds);

        defaults.Theme = AppTheme.Dark;
        defaults.SteamCmdPath = @"D:\sdk\tools\ContentBuilder\builder\steamcmd.exe";
        defaults.SetupCompleted = true;
        await store.SaveAsync(defaults);

        var loaded = await store.LoadAsync();
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(@"D:\sdk\tools\ContentBuilder\builder\steamcmd.exe", loaded.SteamCmdPath);
        Assert.True(loaded.SetupCompleted);
    }

    [Fact]
    public void Workspace_paths_use_reference_script_naming()
    {
        var layout = Layout;
        var profileId = Guid.NewGuid();
        var appId = Guid.NewGuid();
        var subApp = new SubApp { SteamAppId = 1200000 };

        Assert.EndsWith(Path.Combine("scripts", "app_1200000.vdf"), layout.AppScriptPath(profileId, appId, subApp));
        Assert.EndsWith(
            Path.Combine("scripts", "depot_1200002.vdf"),
            layout.DepotScriptPath(profileId, appId, subApp.Id, 1200002));
        Assert.EndsWith("output", layout.OutputDirectory(profileId, appId, subApp.Id));
    }
}
