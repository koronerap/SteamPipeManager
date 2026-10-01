using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Platform;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Core.Updates;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Linux ve macOS desteğinin platforma bağlı kararları. Bu makinede yalnızca Windows
/// var; testler başka bir platformu <see cref="HostPlatform"/> değeriyle taklit ediyor.
/// Gerçek bir Linux/macOS'ta davranış ayrıca doğrulanmalı.
/// </summary>
public sealed class PlatformTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spm-platform-" + Guid.NewGuid().ToString("N"));

    public PlatformTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Theory]
    [InlineData(HostOs.Windows, "x64", "win-x64")]
    [InlineData(HostOs.Linux, "x64", "linux-x64")]
    [InlineData(HostOs.MacOS, "arm64", "osx-arm64")]
    [InlineData(HostOs.MacOS, "x64", "osx-x64")]
    public void Runtime_ids_match_dotnet(HostOs os, string architecture, string expected)
    {
        Assert.Equal(expected, new HostPlatform(os, architecture).RuntimeId);
    }

    // ---------------------------------------------------------------- SteamCMD

    [Fact]
    public void Each_platform_downloads_its_own_SteamCMD_package()
    {
        Assert.EndsWith("/steamcmd.zip", SteamCmdLayout.DownloadUrl(HostPlatform.WindowsX64).ToString());
        Assert.EndsWith("/steamcmd_linux.tar.gz", SteamCmdLayout.DownloadUrl(HostPlatform.LinuxX64).ToString());
        Assert.EndsWith("/steamcmd_osx.tar.gz", SteamCmdLayout.DownloadUrl(HostPlatform.MacArm64).ToString());
    }

    [Fact]
    public void On_Linux_the_shell_launcher_is_found_in_the_SDK_builder_folder()
    {
        var expected = Touch("sdk", "tools", "ContentBuilder", "builder_linux", "steamcmd.sh");
        Touch("sdk", "tools", "ContentBuilder", "builder", "steamcmd.exe");

        Assert.Equal(expected, SteamCmdProvisioner.LocateExecutable(Path.Combine(_root, "sdk"), HostPlatform.LinuxX64));
        Assert.Equal(expected, SteamCmdProvisioner.LocateExecutable(expected, HostPlatform.LinuxX64));
    }

    [Fact]
    public void On_macOS_the_osx_builder_is_used_and_the_Windows_exe_is_refused()
    {
        var mac = Touch("cb", "builder_osx", "steamcmd.sh");
        var windows = Touch("cb", "builder", "steamcmd.exe");

        Assert.Equal(mac, SteamCmdProvisioner.LocateExecutable(Path.Combine(_root, "cb"), HostPlatform.MacArm64));
        Assert.Null(SteamCmdProvisioner.LocateExecutable(windows, HostPlatform.MacArm64));
    }

    [Fact]
    public void A_tar_gz_SteamCMD_package_is_extracted()
    {
        var archive = Path.Combine(_root, "steamcmd_linux.tar.gz");

        using (var file = File.Create(archive))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "steamcmd.sh")
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\n")),
            };
            tar.WriteEntry(entry);
        }

        var target = Path.Combine(_root, "installed");
        Directory.CreateDirectory(target);
        SteamCmdProvisioner.Extract(archive, target);

        Assert.Equal("#!/bin/sh\n", File.ReadAllText(Path.Combine(target, "steamcmd.sh")));
    }

    [Fact]
    public void On_Windows_logs_and_sessions_stay_in_the_install_folder()
    {
        var installation = new SteamCmdInstallation(Path.Combine(_root, "steamcmd.exe"), HostPlatform.WindowsX64);

        Assert.Null(installation.HomeDirectory);
        Assert.Equal(Path.Combine(_root, "logs", "console_log.txt"), installation.ConsoleLogPath);
        Assert.Equal(Path.Combine(_root, "config", "config.vdf"), installation.ConfigPath);
    }

    [Fact]
    public void On_Linux_SteamCMD_gets_its_own_home_and_logs_there()
    {
        var home = Path.Combine(_root, "home");
        var installation = new SteamCmdInstallation(Path.Combine(_root, "steamcmd.sh"), HostPlatform.LinuxX64, home);

        Assert.Equal(Path.Combine(home, "Steam", "logs", "console_log.txt"), installation.ConsoleLogPath);
        Assert.Equal(Path.Combine(home, "Steam", "config", "config.vdf"), installation.ConfigPath);

        var startInfo = new ProcessStartInfo("steamcmd.sh");
        installation.Prepare(startInfo);

        Assert.Equal(home, startInfo.Environment["HOME"]);
        Assert.True(Directory.Exists(home));
    }

    /// <summary>macOS'ta Steam istemcisiyle aynı yer; kendi ev dizini onu ayırıyor.</summary>
    [Fact]
    public void On_macOS_the_log_is_under_the_isolated_home_not_the_users_Steam_client()
    {
        var home = Path.Combine(_root, "home");
        var installation = new SteamCmdInstallation(Path.Combine(_root, "steamcmd.sh"), HostPlatform.MacArm64, home);

        Assert.Equal(
            Path.Combine(home, "Library", "Application Support", "Steam", "logs", "console_log.txt"),
            installation.ConsoleLogPath);
    }

    [Fact]
    public void The_terminal_sign_in_script_cannot_be_broken_out_of_by_the_username()
    {
        var installation = new SteamCmdInstallation("/opt/steam cmd/steamcmd.sh", HostPlatform.LinuxX64, "/data/home");

        var script = SteamCmdSessionService.InteractiveLoginScript(installation, "evil'; rm -rf ~; '", "/data/home/done");

        Assert.Contains("+login 'evil'\\''; rm -rf ~; '\\'''", script);
        Assert.Contains("'/opt/steam cmd/steamcmd.sh'", script);
        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.DoesNotContain("\r", script);
    }

    // ---------------------------------------------------------------- BuildPatchTool

    [Theory]
    [InlineData(HostOs.Windows, "Win64", "BuildPatchTool.exe")]
    [InlineData(HostOs.Linux, "Linux", "BuildPatchTool")]
    [InlineData(HostOs.MacOS, "Mac", "BuildPatchTool")]
    public void Each_platform_picks_its_own_BuildPatchTool_binary_from_the_zip(HostOs os, string folder, string file)
    {
        var platform = new HostPlatform(os, "x64");

        // Epic'in zip'i üç ikiliyi birden taşıyor.
        Touch("BuildPatchTool_1.8.8", "Engine", "Binaries", "Win64", "BuildPatchTool.exe");
        Touch("BuildPatchTool_1.8.8", "Engine", "Binaries", "Linux", "BuildPatchTool");
        Touch("BuildPatchTool_1.8.8", "Engine", "Binaries", "Mac", "BuildPatchTool");

        var expected = Path.Combine(_root, "BuildPatchTool_1.8.8", "Engine", "Binaries", folder, file);

        Assert.Equal(expected, BptInstallation.LocateExecutable(Path.Combine(_root, "BuildPatchTool_1.8.8"), platform));
        Assert.Equal(expected, BptInstallation.LocateExecutable(_root, platform));
    }

    [Fact]
    public void On_Linux_the_name_is_case_sensitive()
    {
        var wrong = Touch("buildpatchtool");

        Assert.Null(BptInstallation.LocateExecutable(wrong, HostPlatform.LinuxX64));
    }

    // ---------------------------------------------------------------- Secret depoları

    private sealed class RecordingRunner(Func<string, IReadOnlyList<string>, string?, CommandResult> respond) : ICommandRunner
    {
        public List<(string Program, IReadOnlyList<string> Arguments, string? Input)> Calls { get; } = [];

        public CommandResult Run(string program, IReadOnlyList<string> arguments, string? standardInput = null)
        {
            Calls.Add((program, arguments, standardInput));
            return respond(program, arguments, standardInput);
        }
    }

    /// <summary>Secret komut satırında görünmesin: secret-tool onu stdin'den okuyor.</summary>
    [Fact]
    public void Linux_secret_service_gets_the_secret_on_stdin_not_in_the_arguments()
    {
        var runner = new RecordingRunner((_, _, _) => new CommandResult(0, ""));
        new LinuxSecretServiceBackend(runner).Write("profile1", "top-secret");

        var (program, arguments, input) = Assert.Single(runner.Calls);

        Assert.Equal("secret-tool", program);
        Assert.Equal("store", arguments[0]);
        Assert.DoesNotContain(arguments, a => a.Contains("top-secret"));
        Assert.Equal("top-secret", input);
    }

    [Fact]
    public void Linux_secret_service_lookup_trims_the_newline_and_treats_failure_as_missing()
    {
        var found = new LinuxSecretServiceBackend(new RecordingRunner((_, _, _) => new CommandResult(0, "abc\n")));
        var missing = new LinuxSecretServiceBackend(new RecordingRunner((_, _, _) => new CommandResult(1, "")));

        Assert.Equal("abc", found.Read("p"));
        Assert.Null(missing.Read("p"));
        Assert.False(missing.Has("p"));
    }

    [Fact]
    public void Mac_keychain_items_are_scoped_to_the_app_service_and_the_profile()
    {
        var runner = new RecordingRunner((_, args, _) => new CommandResult(0, args.Contains("-w") && args[0] == "find-generic-password" ? "s3cr3t\n" : ""));
        var keychain = new MacKeychainBackend(runner);

        keychain.Write("profile1", "s3cr3t");
        Assert.Equal("s3cr3t", keychain.Read("profile1"));
        keychain.Delete("profile1");

        Assert.All(runner.Calls, call =>
        {
            Assert.Equal("security", call.Program);
            Assert.Contains(MacKeychainBackend.Service, call.Arguments);
            Assert.Contains("profile1", call.Arguments);
        });

        Assert.Equal(
            ["add-generic-password", "find-generic-password", "delete-generic-password"],
            runner.Calls.Select(c => c.Arguments[0]));
    }

    [Fact]
    public void A_failed_keychain_write_is_reported_not_swallowed()
    {
        var keychain = new MacKeychainBackend(new RecordingRunner((_, _, _) => new CommandResult(45, "")));

        Assert.Throws<InvalidOperationException>(() => keychain.Write("p", "s"));
    }

    [Fact]
    public void The_owner_only_file_fallback_round_trips_and_says_it_is_not_encrypted()
    {
        var path = Path.Combine(_root, "epic-secrets.json");
        var store = new EpicSecretStore(path, new OwnerOnlyFileBackend(path));
        var profile = Guid.NewGuid();

        store.Write(profile, "plain-but-private");

        Assert.False(store.IsEncrypted);
        Assert.Equal(SecretStorageKind.OwnerOnlyFile, store.Kind);
        Assert.Equal("plain-but-private", store.Read(profile));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        store.Remove(profile);
        Assert.False(store.Has(profile));
    }

    [Fact]
    public void Windows_keeps_using_DPAPI()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new EpicSecretStore(Path.Combine(_root, "s.json"), platform: HostPlatform.WindowsX64);

        Assert.Equal(SecretStorageKind.WindowsDpapi, store.Kind);
        Assert.True(store.IsEncrypted);
    }

    // ---------------------------------------------------------------- Güncelleyici

    [Fact]
    public void Windows_package_name_does_not_change_for_existing_installs()
    {
        // 1.1.0 istemcileri bu adı arıyor; değişirse kendilerini güncelleyemezler.
        var target = UpdateTarget.For(ProductProfile.SteamPipeManager, HostPlatform.WindowsX64);

        Assert.Equal("SteamPipeManager-win-x64.zip", target.PackageName);
        Assert.Equal(UpdateAssets.PackageName(ProductId.SteamPipeManager), target.PackageName);
        Assert.Equal("SteamPipeManager.exe", target.ExecutableRelativePath);
    }

    [Fact]
    public void Linux_and_macOS_packages_are_tar_gz_and_macOS_runs_from_the_app_bundle()
    {
        var linux = UpdateTarget.For(ProductProfile.PipeManagerHub, HostPlatform.LinuxX64);
        var mac = UpdateTarget.For(ProductProfile.EpicBuildManager, HostPlatform.MacArm64);

        Assert.Equal("PipeManagerHub-linux-x64.tar.gz", linux.PackageName);
        Assert.Equal("PipeManagerHub", linux.ExecutableRelativePath);
        Assert.Equal("EpicBuildManager-osx-arm64.tar.gz", mac.PackageName);
        Assert.Equal("Epic Build Manager.app", mac.BundleName);
        Assert.Equal(Path.Combine("Contents", "MacOS", "EpicBuildManager"), mac.ExecutableRelativePath);
    }

    [Fact]
    public void On_macOS_the_app_root_is_the_bundle_and_a_loose_binary_is_a_development_build()
    {
        var mac = UpdateTarget.For(ProductProfile.SteamPipeManager, HostPlatform.MacArm64);
        var bundle = Path.Combine(_root, "Steam Pipe Manager.app");

        Assert.Equal(bundle, mac.AppRootOf(Path.Combine(bundle, "Contents", "MacOS", "SteamPipeManager")));
        Assert.Null(mac.AppRootOf(Path.Combine(_root, "bin", "SteamPipeManager")));
        Assert.Null(mac.AppRootOf(Path.Combine(bundle, "Contents", "MacOS", "SomethingElse")));
    }

    private string MakeTarGz(params (string Name, string Content)[] files)
    {
        var path = Path.Combine(_root, $"pkg-{Guid.NewGuid():N}.tar.gz");

        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip);

        foreach (var (name, content) in files)
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                Mode = (UnixFileMode)0b111_101_101,
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            });
        }

        return path;
    }

    [Fact]
    public void A_macOS_package_is_staged_from_inside_its_app_bundle()
    {
        var mac = UpdateTarget.For(ProductProfile.SteamPipeManager, HostPlatform.MacArm64);
        var package = MakeTarGz(
            ("Steam Pipe Manager.app/Contents/MacOS/SteamPipeManager", "new"),
            ("Steam Pipe Manager.app/Contents/Info.plist", "<plist/>"));

        var staged = UpdateInstaller.Stage(package, Path.Combine(_root, "staging"), mac.ExecutableRelativePath);

        Assert.EndsWith("Steam Pipe Manager.app", staged);
        Assert.Equal("new", File.ReadAllText(Path.Combine(staged, mac.ExecutableRelativePath)));
    }

    [Fact]
    public void A_tar_entry_that_escapes_the_staging_folder_is_refused()
    {
        var package = MakeTarGz(("PipeManagerHub", "bin"), ("../../evil", "x"));

        var ex = Assert.Throws<UpdateException>(() =>
            UpdateInstaller.Stage(package, Path.Combine(_root, "staging"), "PipeManagerHub"));

        Assert.Equal(UpdateFailure.PackageInvalid, ex.Failure);
        Assert.False(File.Exists(Path.Combine(_root, "evil")));
    }

    [Fact]
    public async Task Each_platform_looks_for_its_own_package_in_the_release()
    {
        var server = new FakeReleaseServer().Route(UpdateCheckerTests.Latest, UpdateCheckerTests.ReleaseJson("v9.0.0",
            "SteamPipeManager-win-x64.zip", "SteamPipeManager-linux-x64.tar.gz", "SHA256SUMS.txt"));
        var client = new GitHubReleaseClient(new HttpClient(server), UpdateSource.Default);

        var linux = await new UpdateChecker(client, UpdateTarget.For(ProductProfile.SteamPipeManager, HostPlatform.LinuxX64))
            .CheckAsync(new AppVersion(1, 1, 0));
        var mac = await new UpdateChecker(client, UpdateTarget.For(ProductProfile.SteamPipeManager, HostPlatform.MacArm64))
            .CheckAsync(new AppVersion(1, 1, 0));

        Assert.Equal("SteamPipeManager-linux-x64.tar.gz", linux.Package?.Name);
        Assert.Equal(UpdateAvailability.ManualOnly, mac.Availability);
    }
}

/// <summary>
/// Canlı log beklenen yerde hiç görünmezse (bir platformda SteamCMD log'unu başka yere
/// yazıyorsa) sonuç kaybolmamalı ve çalışan build "takıldı" sanılıp öldürülmemeli.
/// </summary>
public sealed class MissingLiveLogTests
{
    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

    [Fact]
    public async Task The_result_is_read_from_stdout_when_the_live_log_never_appears()
    {
        // Sahte araç bu senaryoda hiç log yazmıyor, yalnızca stdout.
        using var fake = FakeSteamCmd.Create("success+nolog");

        var runner = new SteamCmdRunner(fake.Installation) { StallTimeout = TimeSpan.FromSeconds(1) };
        var result = await runner.RunAsync("+quit", ct: Timeout());

        Assert.False(result.TimedOut);
        Assert.NotNull(result.SuccessEvent);
        Assert.Equal(4242u, result.SuccessEvent!.BuildId);
    }
}
