using SteamPipeManager.Core.Models;
using SteamPipeManager.Presentation.Services;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Telefona aktarım: masaüstü ile Android uygulaması aynı biçimi konuşmalı. Android'in
/// ürettiği metin burada, masaüstünün ürettiği Android testlerinde (PairingTest.kt) açılıyor.
/// </summary>
public sealed class PhonePairingTests
{
    private static readonly PhonePairing.Payload Sample = new(
        1,
        [new(730, "Counter-Strike 2", 0), new(570, "Dota 2", null), new(4000, "Ğüşİöç Oyun", 1)],
        ["KEY-A", "KEY-B"],
        "TRY");

    /// <summary>
    /// Tuz 0..15, nonce 100..111, kod 12345678, 1000 tur; üreten: Android (Kotlin, Pairing.encode).
    /// Masaüstünün aynı girdiyle ürettiğiyle bayt bayt aynı çıktı (aynı JSON sırası, ikisi de zlib).
    /// </summary>
    private const string AndroidVector =
        "SPMPULSE1.AAECAwQFBgcICQoLDA0OD2RlZmdoaWprbG1ubzpYCZujUSzwQvVNWOjxpbdjK1R_8NYCpfVjs7AYp20l261CM6J3V79e-DGEviQNQpqNp6T9jYy1rclfDm2niCscU0a_LTNNb6xA4lUaXUB2ODaL3AQVCkAnItzEkh9zRLoOI94VSWAootEMouUdBFamTzwpgNU9jj_03m8WzFyhAjdaVIWqVtA2wXDPaJcFwueBgWOUw2QJIdXL";

    [Fact]
    public void What_the_android_app_sealed_opens_here()
    {
        var opened = PhonePairing.Open(AndroidVector, "12345678", iterations: 1000);

        Assert.NotNull(opened);
        Assert.True(Sample.Matches(opened));
    }

    [Fact]
    public void A_sealed_payload_opens_only_with_its_code()
    {
        var code = PhonePairing.NewCode();
        var text = PhonePairing.Seal(Sample, code, iterations: 1000);

        Assert.StartsWith(PhonePairing.Prefix, text);
        Assert.True(Sample.Matches(PhonePairing.Open(text, PhonePairing.FormatCode(code), iterations: 1000)!));

        var wrong = ((int.Parse(code) + 1) % 100_000_000).ToString("D8");
        Assert.Null(PhonePairing.Open(text, wrong, iterations: 1000));
        Assert.Null(PhonePairing.Open(text[..^4] + "AAAA", code, iterations: 1000));
        Assert.Null(PhonePairing.Open("https://example.com", code, iterations: 1000));
    }

    [Fact]
    public void Codes_have_eight_digits_and_read_in_two_halves()
    {
        var code = PhonePairing.NewCode();

        Assert.Matches("^[0-9]{8}$", code);
        Assert.Equal("1234 5678", PhonePairing.FormatCode("12345678"));
    }

    /// <summary>
    /// Her oyun anahtarlı ilk profilinin anahtarıyla; aynı anahtar bir kez gidiyor (aynı
    /// şirketin iki profili); anahtarsız oyun yalnızca oyuncu sayısı için.
    /// </summary>
    [Fact]
    public void Games_point_at_their_profiles_key_and_each_key_is_sent_once()
    {
        var (companyA, teammate, companyB, noKey) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        AnalyticsTarget Game(uint appId, Guid owner) =>
            new(appId, $"Game {appId}", $"Game {appId}", SubAppKind.Main, [new AnalyticsOwner(owner, "p")]);

        var keys = new Dictionary<Guid, string> { [companyA] = "KEY-A", [teammate] = "KEY-A", [companyB] = "KEY-B" };

        var payload = PhonePairing.Build(
            [Game(1, companyA), Game(2, companyB), Game(3, teammate), Game(4, noKey)],
            id => keys.GetValueOrDefault(id),
            currency: null);

        Assert.Equal(["KEY-A", "KEY-B"], payload.Keys);
        Assert.Equal([0, 1, 0, null], payload.Games.Select(g => g.Key));
        Assert.Null(payload.Currency);
    }

    [Fact]
    public void The_qr_holds_the_text_as_a_square_of_modules()
    {
        var modules = PhonePairing.Modules(PhonePairing.Seal(Sample, "12345678", iterations: 1000));

        Assert.Equal(modules.GetLength(0), modules.GetLength(1));
        Assert.True(modules.GetLength(0) >= 21);

        // Sol üstteki konum işaretinin köşesi koyu.
        Assert.True(modules[0, 0]);
    }
}
