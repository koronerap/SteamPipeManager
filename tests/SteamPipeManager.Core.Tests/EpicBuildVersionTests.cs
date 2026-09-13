using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Models;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Epic'te sürüm numarasını uygulama üretiyor — Steam'de BuildID'yi Steam döndürdüğü
/// için böyle bir sorumluluk yoktu.
///
/// Kurallar ölçümden geliyor (docs/E0-BPT-FINDINGS.md, Bulgu 16 ve 18) — ve önemli bir
/// düzeltmeyle: ilk ölçüm offline <c>ChunkBuildDirectory</c> ile yapılmıştı, o mod
/// gevşek. Gerçek <c>UploadBinary</c> katı bir karakter kümesi dayatıyor
/// (<c>a-z A-Z 0-9 . + - _</c>). Testler katı olana göre.
/// </summary>
public sealed class EpicBuildVersionTests
{
    // --- Aracın gerçekte kaydedeceği değer ---

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3 beta", "1.2.3beta")]
    [InlineData("  1.0  ", "1.0")]
    [InlineData("a\tb", "ab")]
    public void Spaces_are_removed_because_the_tool_removes_them(string input, string expected) =>
        Assert.Equal(expected, EpicBuildVersion.Normalize(input));

    // --- Doğrulama ---

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.2.3-beta.4")]
    [InlineData("build_2026_09_10")]
    [InlineData("1.0+build.7")]
    public void A_plain_version_is_accepted(string version) =>
        Assert.True(EpicBuildVersion.Validate(version).CanBuild);

    /// <summary>
    /// ASCII dışı karakterler <c>UploadBinary</c> tarafından reddediliyor. Offline mod
    /// kabul ettiği için ilk ölçümde gözden kaçmıştı; gerçek yüklemede patlardı.
    /// </summary>
    [Theory]
    [InlineData("sürüm-1")]
    [InlineData("版本-1")]
    [InlineData("v1.0 β")]
    public void Non_ascii_versions_are_rejected(string version) =>
        Assert.False(EpicBuildVersion.Validate(version).CanSave);

    /// <summary>
    /// Araç boş sürümü kabul edip isimsiz bir build üretiyor; kullanıcı onu Epic
    /// tarafında tanıyamaz, o yüzden biz reddediyoruz.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_version_is_rejected(string? version)
    {
        var result = EpicBuildVersion.Validate(version);

        Assert.False(result.CanSave);
        Assert.Contains(result.Issues, i => i.Severity == IssueSeverity.Error);
    }

    /// <summary>
    /// <c>/</c> aracı durdurmuyor — hata vermeden manifest'i alt klasöre yazıyor.
    /// Sessiz olduğu için tehlikeli; burada peşinen kesiliyor.
    /// </summary>
    [Theory]
    [InlineData("1.2/3")]
    [InlineData(@"1.2\3")]
    [InlineData("1.2:3")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    public void Path_like_characters_are_rejected(string version)
    {
        var result = EpicBuildVersion.Validate(version);

        Assert.False(result.CanSave);
        Assert.Contains(result.Issues, i => i.Severity == IssueSeverity.Error);
    }

    /// <summary>
    /// Boşluk da reddediliyor. Offline modda sessizce siliniyordu ve bir süre bunu
    /// "uyarı" saymıştık; gerçek yükleme ise hata veriyor.
    /// </summary>
    [Fact]
    public void A_version_with_spaces_is_rejected()
    {
        var result = EpicBuildVersion.Validate("1.2.3 beta");

        Assert.False(result.CanSave);
        Assert.Contains(result.Issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void An_over_long_version_is_rejected()
    {
        var result = EpicBuildVersion.Validate(new string('v', EpicBuildVersion.MaxLength + 1));

        Assert.False(result.CanSave);
    }

    // --- Şablon ---

    [Fact]
    public void The_template_placeholders_are_filled()
    {
        var rendered = EpicBuildVersion.Render(
            "{app}-{kind}-{date}-{n}",
            appTitle: "Skyward",
            targetTitle: "Main",
            kind: SubAppKind.Demo,
            artifactId: "skyward-demo",
            counter: 7,
            now: new DateTimeOffset(2026, 9, 10, 14, 30, 0, TimeSpan.Zero));

        Assert.Equal("Skyward-Demo-2026-09-10-7", rendered);
    }

    // --- Serbest metnin temizlenmesi ---

    /// <summary>
    /// Oyun adlarında boşluk olması kural. Şablon doldurulurken sonuç temizlenmezse
    /// üretilen her sürüm reddedilirdi.
    /// </summary>
    [Theory]
    [InlineData("Pixel Racer", "Pixel-Racer")]
    [InlineData("Skyward", "Skyward")]
    [InlineData("Café Noir!", "Caf-Noir")]
    [InlineData("  bosluk  ", "bosluk")]
    [InlineData("a///b", "a-b")]
    [InlineData("", "")]
    public void Free_text_is_made_safe_for_a_version(string input, string expected) =>
        Assert.Equal(expected, EpicBuildVersion.Sanitize(input));

    /// <summary>
    /// Şablonun kendisi süslü parantez ve boşluk içeriyor; bunlar doldurulduktan sonra
    /// temizleniyor, yoksa varsayılan şablon bile geçersiz sürüm üretirdi.
    /// </summary>
    [Fact]
    public void A_template_with_spaces_still_produces_a_valid_version()
    {
        var rendered = EpicBuildVersion.Render(
            "{app} {kind} {n}", "Pixel Racer", "Windows", SubAppKind.Demo, "art", 3);

        Assert.Equal("Pixel-Racer-Demo-3", rendered);
        Assert.True(EpicBuildVersion.Validate(rendered).CanSave);
    }

    // --- Benzersizlik ---

    [Fact]
    public void The_counter_skips_versions_that_are_already_used()
    {
        var next = EpicBuildVersion.NextUnique(
            "v{n}",
            ["v1", "v2", "v3"],
            "Skyward", "Main", SubAppKind.Main, "art");

        Assert.Equal("v4", next);
    }

    /// <summary>
    /// Araç "1.0 beta" ile "1.0beta"yı aynı sürüm sayıyor; benzersizlik kontrolü de
    /// aynı gözle bakmalı, yoksa çakışan bir sürüm üretiriz.
    /// </summary>
    [Fact]
    public void Uniqueness_is_judged_the_way_the_tool_judges_it()
    {
        var next = EpicBuildVersion.NextUnique(
            "1.0 beta",
            ["1.0beta"],
            "Skyward", "Main", SubAppKind.Beta, "art");

        Assert.NotEqual("1.0 beta", next);
        Assert.DoesNotContain(
            EpicBuildVersion.Normalize(next),
            new[] { "1.0beta" },
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Şablonda sayaç yoksa çakışmayı sona eklenen bir numara çözüyor.</summary>
    [Fact]
    public void A_template_without_a_counter_still_avoids_collisions()
    {
        var next = EpicBuildVersion.NextUnique(
            "1.0.0",
            ["1.0.0", "1.0.0-2"],
            "Skyward", "Main", SubAppKind.Main, "art");

        Assert.Equal("1.0.0-3", next);
    }

    [Fact]
    public void With_no_history_the_first_version_is_used_as_is()
    {
        var next = EpicBuildVersion.NextUnique(
            "1.0.0", [], "Skyward", "Main", SubAppKind.Main, "art");

        Assert.Equal("1.0.0", next);
    }

    /// <summary>
    /// Hangi şablon yazılırsa yazılsın üretilen sürüm gönderilebilir olmalı — oyun adı
    /// boşluklu, Türkçe karakterli ya da şablonun kendisi boşluklu olsa bile.
    /// </summary>
    [Theory]
    [InlineData("{app}-{date}", "Skyward")]
    [InlineData("v{n}", "Skyward")]
    [InlineData("{app} {kind} {n}", "Pixel Racer")]
    [InlineData("{app}-{target}-{n}", "Şeker Fabrikası")]
    [InlineData("{app}.{n}", "Café Noir!")]
    public void Generated_versions_pass_validation(string template, string appTitle)
    {
        var next = EpicBuildVersion.NextUnique(
            template, [], appTitle, "Main Game", SubAppKind.Main, "art");

        Assert.True(
            EpicBuildVersion.Validate(next).CanSave,
            $"üretilen sürüm doğrulamadan geçmedi: '{next}'");
    }
}
