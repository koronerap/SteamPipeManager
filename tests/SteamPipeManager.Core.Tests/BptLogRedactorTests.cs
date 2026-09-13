using SteamPipeManager.Core.Epic;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Log maskeleme. Amaç, kullanıcının log'unu paylaşabilmesi: teknik satırlar kalmalı
/// (hata ayıklamanın tamamı orada), kime ait olduğu gitmeli.
/// </summary>
public sealed class BptLogRedactorTests
{
    [Fact]
    public void Identity_parameters_lose_their_values_but_keep_their_names()
    {
        var line = "-mode=UploadBinary -OrganizationId=o-12345 -ProductId=p-abcde " +
                   "-ArtifactId=win-x64 -ClientId=c-98765 -BuildVersion=1.2.3";

        var redacted = BptLogRedactor.Redact(line);

        Assert.DoesNotContain("o-12345", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("p-abcde", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("c-98765", redacted, StringComparison.Ordinal);

        // Hangi parametrenin verildiği teknik bilgi; kalmalı.
        Assert.Contains("-OrganizationId=", redacted, StringComparison.Ordinal);
        Assert.Contains("-mode=UploadBinary", redacted, StringComparison.Ordinal);

        // Sürüm gizli bir şey değil ve hata ayıklarken gerekiyor.
        Assert.Contains("1.2.3", redacted, StringComparison.Ordinal);
    }

    /// <summary>Yapılandırma bloğu parametre değil, "Alan: değer" biçiminde yazıyor.</summary>
    [Fact]
    public void The_configuration_block_is_masked_too()
    {
        var block = """
            LogUploadBinary:    OrganizationId: o-12345
            LogUploadBinary:    ProductId: p-abcde
            LogUploadBinary:    ChunkWindowSize: 1048576
            """;

        var redacted = BptLogRedactor.Redact(block);

        Assert.DoesNotContain("o-12345", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("p-abcde", redacted, StringComparison.Ordinal);

        // Teknik ayar gizli değil; kalmalı.
        Assert.Contains("1048576", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void The_user_name_disappears_from_paths()
    {
        var line = @"BuildRoot=C:\Users\jdoe\builds\Game -CloudDir=D:\cloud";

        var redacted = BptLogRedactor.Redact(line, userName: "jdoe");

        Assert.DoesNotContain("jdoe", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<kullanıcı>", redacted, StringComparison.Ordinal);

        // Kullanıcıya bağlı olmayan yol kalabilir; sorunu anlamaya yarıyor.
        Assert.Contains(@"D:\cloud", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void The_user_name_disappears_outside_paths_as_well()
    {
        var redacted = BptLogRedactor.Redact("Signed in as jdoe on machine", userName: "jdoe");

        Assert.DoesNotContain("jdoe", redacted, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Asıl değer burada: ilerleme ve hata satırları dokunulmadan kalmalı, yoksa
    /// gönderilen log işe yaramaz.
    /// </summary>
    [Fact]
    public void The_technical_lines_survive_untouched()
    {
        var log = """
            [2026.09.10-13.56.44:497][  2]LogPatchGeneration: Enumerated 8 files in 197 us
            [2026.09.10-13.56.44:721][  8]LogDataScanner: @33030145: Scanner completed in 0 us with 0 collisions.
            [2026.09.10-13.53.05:363][  0]LogBuildPatchTool: Error: Tool exited with MissingCredentials (9)
            [2026.09.10-13.56.47:869][100]LogBuildPatchTool: Display: Chunk generation complete.
            """;

        Assert.Equal(log, BptLogRedactor.Redact(log, userName: "someone-not-here"));
    }

    [Fact]
    public void An_empty_log_stays_empty() =>
        Assert.Equal("", BptLogRedactor.Redact(""));

    /// <summary>
    /// Log'u alan kişi neyin gizlendiğini bilmeli; eksik bilgiyi hata sanmasın.
    /// </summary>
    [Fact]
    public void The_header_explains_what_was_masked()
    {
        var header = BptLogRedactor.Header("1.2.3", DateTimeOffset.Now);

        Assert.Contains("1.2.3", header, StringComparison.Ordinal);
        Assert.Contains("gizlendi", header, StringComparison.Ordinal);
    }
}
