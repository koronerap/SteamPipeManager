using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Publishing;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Üç ürün aynı koddan çıkıyor; aralarındaki fark yalnızca markalama ve hangi
/// sağlayıcıların açık olduğu.
/// </summary>
public sealed class ProductProfileTests
{
    [Fact]
    public void Steam_pipe_manager_shows_only_steam()
    {
        var product = ProductProfile.SteamPipeManager;

        Assert.True(product.Supports(PublishProviderId.Steam));
        Assert.False(product.Supports(PublishProviderId.Epic));
        Assert.False(product.HasProviderChoice);
        Assert.Equal(PublishProviderId.Steam, product.DefaultProvider);
    }

    [Fact]
    public void Epic_build_manager_shows_only_epic()
    {
        var product = ProductProfile.EpicBuildManager;

        Assert.True(product.Supports(PublishProviderId.Epic));
        Assert.False(product.Supports(PublishProviderId.Steam));
        Assert.False(product.HasProviderChoice);
        Assert.Equal(PublishProviderId.Epic, product.DefaultProvider);
    }

    [Fact]
    public void The_hub_shows_both_and_offers_a_choice()
    {
        var product = ProductProfile.PipeManagerHub;

        Assert.True(product.Supports(PublishProviderId.Steam));
        Assert.True(product.Supports(PublishProviderId.Epic));
        Assert.True(product.HasProviderChoice);
    }

    /// <summary>Epic içeren her ürün deneysel; doğrulama gerçek bir hesapla yapılmadı.</summary>
    [Theory]
    [InlineData(false)]
    public void Only_the_steam_product_is_not_experimental(bool _)
    {
        Assert.False(ProductProfile.SteamPipeManager.IsExperimental);
        Assert.True(ProductProfile.EpicBuildManager.IsExperimental);
        Assert.True(ProductProfile.PipeManagerHub.IsExperimental);
    }

    [Theory]
    [InlineData("SteamPipeManager", ProductId.SteamPipeManager)]
    [InlineData("EpicBuildManager", ProductId.EpicBuildManager)]
    [InlineData("PipeManagerHub", ProductId.PipeManagerHub)]
    [InlineData("  PipeManagerHub  ", ProductId.PipeManagerHub)]
    public void The_build_time_value_is_resolved(string value, ProductId expected) =>
        Assert.Equal(expected, ProductProfile.Parse(value).Id);

    /// <summary>
    /// Eksik ya da bozuk bir değer yüzünden uygulamanın açılmaması, çözülmeye
    /// çalışılan sorundan çok daha kötü olurdu.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BilinmeyenUrun")]
    public void An_unknown_value_falls_back_to_the_shipped_product(string? value) =>
        Assert.Equal(ProductProfile.Default.Id, ProductProfile.Parse(value).Id);

    /// <summary>
    /// Kullanıcı Hub'da Epic profili oluşturup Steam Pipe Manager'a dönerse profil
    /// gizlenmeli ama silinmemeli — veri kaybı, ürün değiştirmenin bedeli olamaz.
    /// </summary>
    [Fact]
    public void A_profile_from_another_product_is_hidden_not_lost()
    {
        var epicProfile = new UserProfile { Provider = PublishProviderId.Epic };
        var steamProfile = new UserProfile { Provider = PublishProviderId.Steam };

        Assert.False(ProductProfile.SteamPipeManager.CanShow(epicProfile));
        Assert.True(ProductProfile.SteamPipeManager.CanShow(steamProfile));

        Assert.True(ProductProfile.PipeManagerHub.CanShow(epicProfile));
        Assert.True(ProductProfile.PipeManagerHub.CanShow(steamProfile));
    }

    [Fact]
    public void Each_product_has_its_own_name()
    {
        var names = new[]
        {
            ProductProfile.SteamPipeManager.Name,
            ProductProfile.EpicBuildManager.Name,
            ProductProfile.PipeManagerHub.Name,
        };

        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.All(names, n => Assert.NotEmpty(n));
    }
}
