using SteamPipeManager.Core.Models;

namespace SteamPipeManager.Core.Tests;

public class ValidationTests
{
    private static SubApp ValidSubApp(uint appId = 1000, uint depotId = 1001) => new()
    {
        Title = "Ana Oyun",
        SteamAppId = appId,
        Depots = [new DepotConfig { DepotId = depotId, ContentRoot = @"D:\content" }],
    };

    private static UserProfile ValidProfile(params SteamApp[] apps) => new()
    {
        DisplayName = "Studio",
        SteamUsername = "studio_user",
        Apps = [.. apps],
    };

    private static SteamApp App(string title, params SubApp[] subApps) =>
        new() { Title = title, SubApps = [.. subApps] };

    [Fact]
    public void Valid_profile_has_no_errors()
    {
        var result = ProfileValidator.ValidateProfile(
            ValidProfile(App("Oyun", ValidSubApp())), checkFileSystem: false);

        Assert.False(result.HasErrors);
        Assert.True(result.CanBuild);
    }

    [Fact]
    public void Rejects_duplicate_app_id_across_games()
    {
        var profile = ValidProfile(
            App("Oyun A", ValidSubApp(appId: 1000, depotId: 1001)),
            App("Oyun B", ValidSubApp(appId: 1000, depotId: 1002)));

        var result = ProfileValidator.ValidateProfile(profile, checkFileSystem: false);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, e => e.Message.Contains("Validate.DuplicateAppId"));
    }

    [Fact]
    public void Rejects_duplicate_depot_id()
    {
        var profile = ValidProfile(
            App("Oyun A", ValidSubApp(appId: 1000, depotId: 9999)),
            App("Oyun B", ValidSubApp(appId: 2000, depotId: 9999)));

        var result = ProfileValidator.ValidateProfile(profile, checkFileSystem: false);

        Assert.Contains(result.Errors, e => e.Message.Contains("Validate.DuplicateDepotId"));
    }

    [Fact]
    public void Rejects_default_branch()
    {
        var subApp = ValidSubApp();
        subApp.SetLiveBranch = "default";

        var result = ProfileValidator.ValidateSubApp(subApp, checkFileSystem: false);

        Assert.Contains(result.Errors, e => e.Message.Contains("Validate.ForbiddenBranch"));
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("")]
    [InlineData("playtest_branch")]
    public void Accepts_other_branches(string branch)
    {
        var subApp = ValidSubApp();
        subApp.SetLiveBranch = branch;

        Assert.False(ProfileValidator.ValidateSubApp(subApp, checkFileSystem: false).HasErrors);
    }

    [Fact]
    public void Rejects_more_than_one_main_sub_app()
    {
        var a = ValidSubApp(1000, 1001);
        var b = ValidSubApp(2000, 2001);
        Assert.Equal(SubAppKind.Main, a.Kind);
        Assert.Equal(SubAppKind.Main, b.Kind);

        var result = ProfileValidator.ValidateProfile(
            ValidProfile(App("Oyun", a, b)), checkFileSystem: false);

        Assert.Contains(result.Errors, e => e.Message.Contains("Validate.TooManyMain"));
    }

    [Fact]
    public void Allows_main_plus_demo()
    {
        var main = ValidSubApp(1000, 1001);
        var demo = ValidSubApp(2000, 2001);
        demo.Kind = SubAppKind.Demo;

        var result = ProfileValidator.ValidateProfile(
            ValidProfile(App("Oyun", main, demo)), checkFileSystem: false);

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Rejects_sub_app_without_depots()
    {
        var subApp = ValidSubApp();
        subApp.Depots.Clear();

        Assert.True(ProfileValidator.ValidateSubApp(subApp, checkFileSystem: false).HasErrors);
    }

    [Fact]
    public void Rejects_missing_content_root()
    {
        var subApp = new SubApp
        {
            Title = "X",
            SteamAppId = 1,
            Depots = [new DepotConfig { DepotId = 2 }],
        };

        Assert.Contains(
            ProfileValidator.ValidateSubApp(subApp, checkFileSystem: false).Errors,
            e => e.Message.Contains("Validate.NoContentRoot"));
    }

    [Fact]
    public void Depot_inherits_sub_app_content_root()
    {
        var subApp = new SubApp
        {
            Title = "X",
            SteamAppId = 1,
            ContentRoot = @"D:\content",
            Depots = [new DepotConfig { DepotId = 2 }],
        };

        Assert.False(ProfileValidator.ValidateSubApp(subApp, checkFileSystem: false).HasErrors);
    }

    [Fact]
    public void Empty_content_directory_blocks_build_but_not_save()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spm_empty_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var subApp = ValidSubApp();
            subApp.Depots[0].ContentRoot = dir;

            var result = ProfileValidator.ValidateSubApp(subApp, checkFileSystem: true);

            // Boş depot yüklemek yayındaki içeriği siler; build engellenir ama kayıt serbest.
            Assert.False(result.CanBuild);
            Assert.True(result.CanSave);
            Assert.Contains(result.Issues, i => i.Message.Contains("Validate.ContentEmpty"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Populated_content_directory_allows_build()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spm_full_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(Path.Combine(dir, "game.exe"), "x");

            var subApp = ValidSubApp();
            subApp.Depots[0].ContentRoot = dir;

            Assert.True(ProfileValidator.ValidateSubApp(subApp, checkFileSystem: true).CanBuild);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_content_directory_blocks_build()
    {
        var subApp = ValidSubApp();
        subApp.Depots[0].ContentRoot = Path.Combine(Path.GetTempPath(), $"spm_missing_{Guid.NewGuid():N}");

        var result = ProfileValidator.ValidateSubApp(subApp, checkFileSystem: true);

        Assert.False(result.CanBuild);
        Assert.True(result.CanSave);
    }

    [Fact]
    public void Rejects_empty_profile_fields()
    {
        var result = ProfileValidator.ValidateProfile(new UserProfile(), checkFileSystem: false);

        Assert.Equal(2, result.Errors.Count());
    }
}
