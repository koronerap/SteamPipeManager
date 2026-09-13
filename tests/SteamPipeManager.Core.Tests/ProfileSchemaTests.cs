using System.Text.Json;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Şema göçü, kullanıcının var olan <c>profiles.json</c>'ını okuduğu için hata payı yok:
/// yanlış bir göç oyunları, build hedeflerini ve depot'ları sessizce kaybettirir.
/// </summary>
public sealed class ProfileSchemaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spm_schema_{Guid.NewGuid():N}");

    public ProfileSchemaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Sağlayıcı alanı eklenmeden önce yazılmış gerçekçi bir v1 dosyası.</summary>
    private const string SchemaV1Json = """
        {
          "schemaVersion": 1,
          "profiles": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "displayName": "Example Studio",
              "steamUsername": "example_partner",
              "credentialMode": "SteamCmdCache",
              "steamId64": 76561197960265728,
              "apps": [
                {
                  "id": "22222222-2222-2222-2222-222222222222",
                  "title": "Skyward",
                  "notes": "",
                  "subApps": [
                    {
                      "id": "33333333-3333-3333-3333-333333333333",
                      "title": "Main Game",
                      "kind": "Main",
                      "steamAppId": 1200000,
                      "buildDescriptionTemplate": "fix-update",
                      "setLiveBranch": "debug",
                      "preview": false,
                      "depots": [
                        {
                          "depotId": 1200002,
                          "label": "Windows",
                          "contentRoot": "D:\\builds\\Skyward_Win",
                          "fileMappings": [
                            { "localPath": "*", "depotPath": ".", "recursive": true }
                          ],
                          "fileExclusions": [ "*.pdb" ],
                          "fileProperties": []
                        }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private async Task<ProfileDatabase> LoadV1FromDiskAsync()
    {
        var layout = new WorkspaceLayout(_dir);
        Directory.CreateDirectory(Path.GetDirectoryName(layout.ProfilesFile)!);
        await File.WriteAllTextAsync(layout.ProfilesFile, SchemaV1Json);

        return await new JsonProfileStore(layout).LoadAsync();
    }

    [Fact]
    public async Task A_version_1_file_is_read_without_losing_anything()
    {
        var database = await LoadV1FromDiskAsync();

        var profile = Assert.Single(database.Profiles);
        Assert.Equal("Example Studio", profile.DisplayName);
        Assert.Equal("example_partner", profile.SteamUsername);
        Assert.Equal(76561197960265728ul, profile.SteamId64);

        var app = Assert.Single(profile.Apps);
        Assert.Equal("Skyward", app.Title);

        var subApp = Assert.Single(app.SubApps);
        Assert.Equal(1200000u, subApp.SteamAppId);
        Assert.Equal("debug", subApp.SetLiveBranch);
        Assert.Equal("fix-update", subApp.BuildDescriptionTemplate);

        var depot = Assert.Single(subApp.Depots);
        Assert.Equal(1200002u, depot.DepotId);
        Assert.Equal("Windows", depot.Label);
        Assert.Equal(@"D:\builds\Skyward_Win", depot.ContentRoot);
        Assert.Equal(["*.pdb"], depot.FileExclusions);
        Assert.True(Assert.Single(depot.FileMappings).Recursive);
    }

    /// <summary>
    /// Sağlayıcı kavramı v2'de geldi. v1'de yazılmış her profil Steam'dir; enum'un
    /// varsayılanı da Steam olduğu için dosyada alan olmaması doğru sonucu veriyor.
    /// </summary>
    [Fact]
    public async Task A_version_1_profile_becomes_a_steam_profile()
    {
        var database = await LoadV1FromDiskAsync();

        Assert.Equal(PublishProviderId.Steam, Assert.Single(database.Profiles).Provider);
        Assert.Equal(ProfileSchema.Current, database.SchemaVersion);
    }

    [Fact]
    public void Upgrading_an_already_current_database_changes_nothing()
    {
        var database = new ProfileDatabase
        {
            SchemaVersion = ProfileSchema.Current,
            Profiles = [new UserProfile { DisplayName = "A", Provider = PublishProviderId.Epic }],
        };

        var upgraded = ProfileSchema.Upgrade(database);

        Assert.Equal(ProfileSchema.Current, upgraded.SchemaVersion);
        Assert.Equal(PublishProviderId.Epic, Assert.Single(upgraded.Profiles).Provider);
    }

    /// <summary>
    /// v1'den gelen bir dosya v2 ve v3 adımlarının <b>ikisinden de</b> geçmeli.
    /// Doğrudan güncel sürüme atlamak bugün çalışırdı çünkü adımların ikisi de veri
    /// taşımıyor; ama gerçek iş yapan bir adım eklendiğinde eski dosyalar onu
    /// sessizce atlardı.
    /// </summary>
    [Fact]
    public async Task A_version_1_file_passes_through_every_step()
    {
        var database = await LoadV1FromDiskAsync();

        Assert.Equal(ProfileSchema.Current, database.SchemaVersion);
        Assert.Equal(PublishProviderId.Steam, Assert.Single(database.Profiles).Provider);
    }

    /// <summary>
    /// Sürüm alanı hiç olmayan çok eski bir dosya 0 olarak okunur; bu da v1 sayılmalı,
    /// geçiş adımı bulunamadı diye patlamamalı.
    /// </summary>
    [Fact]
    public void A_database_with_no_version_is_treated_as_version_1()
    {
        var upgraded = ProfileSchema.Upgrade(new ProfileDatabase { SchemaVersion = 0 });

        Assert.Equal(ProfileSchema.Current, upgraded.SchemaVersion);
    }

    /// <summary>
    /// Epic alanları isteğe bağlı: v1/v2 dosyalarında yoklar ve okunduktan sonra da
    /// null kalmalılar. Steam kullanıcısının dosyasına Epic gürültüsü girmemeli.
    /// </summary>
    [Fact]
    public async Task Epic_fields_stay_empty_for_a_steam_profile()
    {
        var database = await LoadV1FromDiskAsync();

        var profile = Assert.Single(database.Profiles);
        Assert.Null(profile.Epic);
        Assert.Null(Assert.Single(profile.Apps).Epic);
    }

    /// <summary>
    /// Steam profilinin diske yazılan hâlinde Epic alanları hiç görünmemeli —
    /// <c>profiles.json</c> elle düzenlenebilir olmalı, boş bölümlerle şişmemeli.
    /// </summary>
    [Fact]
    public async Task A_steam_profile_writes_no_epic_sections()
    {
        var layout = new WorkspaceLayout(_dir);
        var store = new JsonProfileStore(layout);

        Directory.CreateDirectory(Path.GetDirectoryName(layout.ProfilesFile)!);
        await File.WriteAllTextAsync(layout.ProfilesFile, SchemaV1Json);

        await store.SaveAsync(await store.LoadAsync());

        var written = await File.ReadAllTextAsync(layout.ProfilesFile);
        Assert.DoesNotContain("\"epic\"", written, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Bir Epic profili kaydedilip geri okunduğunda alanları korunmalı.</summary>
    [Fact]
    public async Task An_epic_profile_round_trips()
    {
        var layout = new WorkspaceLayout(_dir);
        var store = new JsonProfileStore(layout);

        var artifact = new EpicArtifact
        {
            Title = "Windows",
            Kind = SubAppKind.Main,
            ArtifactId = "skyward-win",
            BuildRoot = @"D:uilds\Skyward_Win",
            AppLaunch = "Skyward.exe",
            Label = "Live",
            Platform = "Windows",
            BuildVersionTemplate = "{app}-{date}-{n}",
        };

        var app = new SteamApp
        {
            Title = "Skyward",
            Epic = new EpicGameSettings { ProductId = "prod-1", Artifacts = [artifact] },
        };

        var profile = new UserProfile
        {
            DisplayName = "Example Studio",
            Provider = PublishProviderId.Epic,
            Epic = new EpicProfileSettings { OrganizationId = "org-1", ClientId = "client-1" },
            Apps = [app],
        };

        await store.SaveAsync(new ProfileDatabase { Profiles = [profile] });

        var reloaded = Assert.Single((await store.LoadAsync()).Profiles);

        Assert.Equal(PublishProviderId.Epic, reloaded.Provider);
        Assert.Equal("org-1", reloaded.Epic!.OrganizationId);
        Assert.Equal("client-1", reloaded.Epic.ClientId);

        var reloadedArtifact = Assert.Single(Assert.Single(reloaded.Apps).Epic!.Artifacts);
        Assert.Equal("skyward-win", reloadedArtifact.ArtifactId);
        Assert.Equal(@"D:uilds\Skyward_Win", reloadedArtifact.BuildRoot);
        Assert.Equal("Live", reloadedArtifact.Label);
        Assert.Equal("Windows", reloadedArtifact.Platform);
    }

    /// <summary>
    /// Client secret hiçbir koşulda <c>profiles.json</c>'a yazılmamalı — dosyanın
    /// yedeklenebilir ve paylaşılabilir kalması Steam tarafında verilen bir sözdü.
    /// </summary>
    [Fact]
    public async Task The_client_secret_never_reaches_the_profile_file()
    {
        var layout = new WorkspaceLayout(_dir);
        var store = new JsonProfileStore(layout);

        await store.SaveAsync(new ProfileDatabase
        {
            Profiles =
            [
                new UserProfile
                {
                    DisplayName = "Example",
                    Provider = PublishProviderId.Epic,
                    Epic = new EpicProfileSettings { OrganizationId = "org", ClientId = "cid" },
                },
            ],
        });

        var written = await File.ReadAllTextAsync(layout.ProfilesFile);

        Assert.DoesNotContain("secret", written, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Daha yeni bir sürümden dönen kullanıcının dosyasını düşürmeye çalışmak veriyi
    /// bozardı; bilinmeyen sürüme dokunulmuyor.
    /// </summary>
    [Fact]
    public void A_newer_schema_is_left_alone()
    {
        var database = new ProfileDatabase { SchemaVersion = ProfileSchema.Current + 5 };

        Assert.Equal(ProfileSchema.Current + 5, ProfileSchema.Upgrade(database).SchemaVersion);
    }

    [Fact]
    public async Task Reading_a_version_1_file_does_not_rewrite_it()
    {
        var layout = new WorkspaceLayout(_dir);
        Directory.CreateDirectory(Path.GetDirectoryName(layout.ProfilesFile)!);
        await File.WriteAllTextAsync(layout.ProfilesFile, SchemaV1Json);

        await new JsonProfileStore(layout).LoadAsync();

        // Yalnızca açıp kapatmak kimsenin dosyasını dönüştürmemeli.
        var onDisk = await File.ReadAllTextAsync(layout.ProfilesFile);
        Assert.Equal(SchemaV1Json, onDisk);
    }

    [Fact]
    public async Task Saving_after_a_migration_writes_the_new_version()
    {
        var layout = new WorkspaceLayout(_dir);
        var store = new JsonProfileStore(layout);

        Directory.CreateDirectory(Path.GetDirectoryName(layout.ProfilesFile)!);
        await File.WriteAllTextAsync(layout.ProfilesFile, SchemaV1Json);

        await store.SaveAsync(await store.LoadAsync());

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(layout.ProfilesFile));

        Assert.Equal(
            ProfileSchema.Current,
            document.RootElement.GetProperty("schemaVersion").GetInt32());

        Assert.Equal(
            "Steam",
            document.RootElement.GetProperty("profiles")[0].GetProperty("provider").GetString());
    }
}
