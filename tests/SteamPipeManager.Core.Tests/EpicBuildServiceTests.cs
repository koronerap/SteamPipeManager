using System.Runtime.CompilerServices;
using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Models;
using Xunit.Abstractions;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Epic yayınlama akışı.
///
/// Geçerli kimlik bilgimiz olmadığı için doğrulanan şey <b>akışın karar mantığı</b>:
/// neyin engellendiği, hangi durumun hangi sonuca çevrildiği, yükleme ile etiketleme
/// arasındaki kısmi başarısızlığın doğru raporlandığı. Başarılı yükleme yolu ancak
/// gerçek bir Epic hesabıyla doğrulanabilir.
/// </summary>
public sealed class EpicBuildServiceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"spm_epicbuild_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_work))
        {
            try
            {
                Directory.Delete(_work, recursive: true);
            }
            catch (IOException)
            {
                // Temizlik başarısız olsa da sonuç etkilenmiyor.
            }
        }
    }

    private static string? FindTool([CallerFilePath] string callerPath = "")
    {
        var dir = Path.GetDirectoryName(callerPath);

        while (dir is not null)
        {
            var tests = Path.Combine(dir, "tests");

            if (Directory.Exists(tests))
            {
                foreach (var candidate in Directory.EnumerateDirectories(tests, "BuildPatchTool_*"))
                {
                    if (BptInstallation.LocateExecutable(candidate) is { } exe)
                    {
                        return exe;
                    }
                }
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private BptInstallation Tool() =>
        new(FindTool() ?? Path.Combine(_work, "missing", "BuildPatchTool.exe"));

    private bool ToolPresent => FindTool() is not null;

    private string CreateContent()
    {
        var root = Path.Combine(_work, "content");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Game.exe"), new string('x', 4096));

        return root;
    }

    private EpicBuildRequest Request(Action<EpicArtifact>? configure = null)
    {
        var artifact = new EpicArtifact
        {
            Title = "Windows",
            ArtifactId = "spm-artifact",
            BuildRoot = CreateContent(),
            AppLaunch = "Game.exe",
            Platform = "Windows",
            BuildVersionTemplate = "1.0.{n}",
        };

        configure?.Invoke(artifact);

        var app = new SteamApp
        {
            Title = "Skyward",
            Epic = new EpicGameSettings { ProductId = "prod", Artifacts = [artifact] },
        };

        var profile = new UserProfile
        {
            DisplayName = "Example",
            Provider = PublishProviderId.Epic,
            Epic = new EpicProfileSettings { OrganizationId = "org", ClientId = "cid" },
            Apps = [app],
        };

        return new EpicBuildRequest(
            profile, app, artifact,
            new EpicCredentials("org", "prod", "spm-artifact", "cid", "not-a-real-secret"));
    }

    private EpicBuildService Service() => new(Tool(), Path.Combine(_work, "logs"));

    // --- Çalıştırmadan önce engellenenler ---

    [Fact]
    public async Task A_target_with_no_artifact_id_never_starts_the_tool()
    {
        var outcome = await Service().BuildAsync(Request(a => a.ArtifactId = ""));

        Assert.False(outcome.Started);
        Assert.NotNull(outcome.BlockedReason);
        Assert.Empty(outcome.Events);
    }

    /// <summary>
    /// Araç boş içeriği zaten reddediyor; biz önceden söylüyoruz ki kullanıcı süreç
    /// başlatıp beklemesin.
    /// </summary>
    [Fact]
    public async Task An_empty_content_folder_is_caught_before_running()
    {
        var empty = Path.Combine(_work, "empty");
        Directory.CreateDirectory(empty);

        var outcome = await Service().BuildAsync(Request(a => a.BuildRoot = empty));

        Assert.False(outcome.Started);
    }

    [Fact]
    public async Task A_missing_executable_inside_the_content_folder_is_caught()
    {
        var outcome = await Service().BuildAsync(Request(a => a.AppLaunch = "NotThere.exe"));

        Assert.False(outcome.Started);
    }

    /// <summary>Etiket varsa platform şart — <c>LabelBinary</c> ikisini birlikte istiyor.</summary>
    [Fact]
    public async Task A_label_without_a_platform_is_caught()
    {
        var outcome = await Service().BuildAsync(Request(a =>
        {
            a.Label = "Live";
            a.Platform = "";
        }));

        Assert.False(outcome.Started);
    }

    [Fact]
    public async Task A_missing_tool_blocks_the_build()
    {
        var service = new EpicBuildService(
            new BptInstallation(Path.Combine(_work, "yok", "BuildPatchTool.exe")),
            Path.Combine(_work, "logs"));

        var outcome = await service.BuildAsync(Request());

        Assert.False(outcome.Started);
    }

    // --- Sürüm üretimi ---

    [Fact]
    public async Task The_version_is_generated_from_the_template_and_avoids_used_ones()
    {
        if (!ToolPresent)
        {
            output.WriteLine("ATLANDI: BuildPatchTool yok.");
            return;
        }

        var service = Service();
        service.KnownVersions = _ => ["1.0.1", "1.0.2"];

        var outcome = await service.BuildAsync(Request());

        // Kimlik geçersiz olduğu için build başarısız olacak, ama sürüm seçimi
        // çalıştırmadan önce yapılıyor ve kayda geçiyor.
        Assert.Equal("1.0.3", outcome.Record.EpicBuildVersion);
    }

    [Fact]
    public async Task An_explicit_version_is_used_as_given()
    {
        if (!ToolPresent)
        {
            return;
        }

        var outcome = await Service().BuildAsync(Request() with { BuildVersion = "9.9.9" });

        Assert.Equal("9.9.9", outcome.Record.EpicBuildVersion);
    }

    [Fact]
    public async Task An_invalid_explicit_version_blocks_the_build()
    {
        var outcome = await Service().BuildAsync(Request() with { BuildVersion = "1.0/2" });

        Assert.False(outcome.Started);
        Assert.Empty(outcome.Events);
    }

    // --- Gerçek araca karşı ---

    [Fact]
    public async Task Rejected_credentials_end_the_build_before_labelling()
    {
        if (!ToolPresent)
        {
            output.WriteLine("ATLANDI: BuildPatchTool yok.");
            return;
        }

        var outcome = await Service().BuildAsync(Request(a => a.Label = "Live"));

        Assert.True(outcome.Started);
        Assert.Equal(BuildOutcome.Failed, outcome.Record.Outcome);

        // Yükleme başarısız olduğu için etiketlemeye hiç geçilmemeli; aksi hâlde
        // "yüklendi ama etiketlenemedi" diye yanlış bir mesaj gösterirdik.
        Assert.False(outcome.UploadedButNotLabelled);

        output.WriteLine($"sonuç={outcome.Record.Outcome} sebep={outcome.Record.FailureReason}");
        Assert.NotNull(outcome.Record.FailureReason);
    }

    [Fact]
    public async Task The_record_carries_what_the_history_screen_needs()
    {
        if (!ToolPresent)
        {
            return;
        }

        var outcome = await Service().BuildAsync(Request());
        var record = outcome.Record;

        Assert.Equal(PublishProviderId.Epic, record.Provider);
        Assert.Equal("spm-artifact", record.EpicArtifactId);
        Assert.NotNull(record.EpicBuildVersion);
        Assert.NotNull(record.FinishedAt);
        Assert.NotNull(record.LogFilePath);
        Assert.True(File.Exists(record.LogFilePath), "log dosyası yazılmadı");
    }

    /// <summary>
    /// Her çalıştırma kendi log dosyasını alıyor (<c>-abslog</c>). Yükleme ve etiketleme
    /// ayrı adımlar olduğu için ayrı dosyalara yazıyorlar.
    /// </summary>
    [Fact]
    public async Task Each_run_writes_its_own_log_file()
    {
        if (!ToolPresent)
        {
            return;
        }

        var service = Service();

        await service.BuildAsync(Request() with { BuildVersion = "1.0.0" });
        await service.BuildAsync(Request() with { BuildVersion = "2.0.0" });

        var logs = Directory.GetFiles(Path.Combine(_work, "logs"), "*.log", SearchOption.AllDirectories);

        Assert.Equal(2, logs.Length);
        Assert.Contains(logs, l => Path.GetFileName(l).Contains("1.0.0", StringComparison.Ordinal));
        Assert.Contains(logs, l => Path.GetFileName(l).Contains("2.0.0", StringComparison.Ordinal));
    }

    /// <summary>Aynı anda iki build çalışmamalı; ikisi de aynı kimlik oturumunu kullanıyor.</summary>
    [Fact]
    public async Task Only_one_build_runs_at_a_time()
    {
        if (!ToolPresent)
        {
            return;
        }

        var service = Service();

        var first = service.BuildAsync(Request() with { BuildVersion = "1.0.0" });
        var second = await service.BuildAsync(Request() with { BuildVersion = "2.0.0" });

        await first;

        Assert.False(second.Started);
        Assert.NotNull(second.BlockedReason);
    }
}
