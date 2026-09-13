using System.Runtime.CompilerServices;
using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Publishing;
using Xunit.Abstractions;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Epic kimlik kontrolü. Gerçek BuildPatchTool'a karşı koşuyor; geçerli kimlik
/// bilgimiz olmadığı için doğrulanan şey <b>reddedilme yolu</b> — yani aracın
/// kararını doğru okuduğumuz ve kullanıcıya doğru durumu gösterdiğimiz.
///
/// Geçerli kimlikle "Active" dönüşü ancak gerçek bir Epic hesabıyla ölçülebilir;
/// Epic desteğinin "experimental" kalmasının sebeplerinden biri bu.
/// </summary>
public sealed class BptSessionServiceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"spm_epicsess_{Guid.NewGuid():N}");

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

    private BptInstallation? Tool()
    {
        if (FindTool() is { } exe)
        {
            return new BptInstallation(exe);
        }

        output.WriteLine("ATLANDI: BuildPatchTool bulunamadı.");
        return null;
    }

    private static EpicCredentials BogusCredentials() =>
        new("spm-test-org", "spm-test-product", "spm-test-artifact", "spm-test-client", "not-a-real-secret");

    /// <summary>
    /// Epic geçersiz kimliği reddettiğinde kullanıcıya "giriş gerekiyor" gösterilmeli —
    /// "kontrol başarısız" değil. Ayrım önemli: birincisini kullanıcı düzeltebilir,
    /// ikincisi bizim tarafımızda bir sorunu işaret eder.
    /// </summary>
    [Fact]
    public async Task Rejected_credentials_are_reported_as_needing_sign_in()
    {
        if (Tool() is not { } tool)
        {
            return;
        }

        var logPath = Path.Combine(_work, "check.log");

        var result = await new BptSessionService(tool).CheckAsync(BogusCredentials(), logPath);

        output.WriteLine($"durum={result.State} ayrıntı={result.Detail}");

        Assert.Equal(SessionState.LoginRequired, result.State);
        Assert.False(result.CanBuild);
        Assert.NotEmpty(result.Detail);
    }

    [Fact]
    public async Task The_check_writes_its_own_log_and_uploads_nothing()
    {
        if (Tool() is not { } tool)
        {
            return;
        }

        var logPath = Path.Combine(_work, "own-check.log");

        await new BptSessionService(tool).CheckAsync(BogusCredentials(), logPath);

        Assert.True(File.Exists(logPath));

        var text = await File.ReadAllTextAsync(logPath);

        // -DryRun chunk bile üretmiyor; yükleme aşamasına dair hiçbir iz olmamalı.
        Assert.Contains("DRYRUN", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Beginning chunk generation", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kimlik bilgileri geçici bir klasörle doğrulanıyor; kontrolden sonra geride
    /// dosya bırakılmamalı.
    /// </summary>
    [Fact]
    public async Task The_scratch_folder_is_cleaned_up()
    {
        if (Tool() is not { } tool)
        {
            return;
        }

        var before = Directory.GetDirectories(Path.GetTempPath(), "spm_epic_check_*").Length;

        await new BptSessionService(tool).CheckAsync(
            BogusCredentials(), Path.Combine(_work, "scratch.log"));

        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "spm_epic_check_*").Length);
    }

    [Fact]
    public async Task A_missing_tool_is_a_check_failure_not_a_credential_problem()
    {
        var missing = new BptInstallation(Path.Combine(_work, "yok", "BuildPatchTool.exe"));

        var result = await new BptSessionService(missing).CheckAsync(
            BogusCredentials(), Path.Combine(_work, "unused.log"));

        Assert.Equal(SessionState.CheckFailed, result.State);
    }
}
