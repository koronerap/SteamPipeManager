using SteamPipeManager.Core.Models;

namespace SteamPipeManager.Core.Publishing;

/// <summary>
/// Aynı koddan çıkan üç üründen hangisi çalışıyor.
///
/// Üç ayrı uygulama yazmak yerine tek çözümün üç yayın profili var; aralarındaki tek
/// fark markalama ve hangi sağlayıcıların açık olduğu. Böylece üç indirme,
/// üç kat bakım maliyeti olmadan çıkıyor.
/// </summary>
public enum ProductId
{
    /// <summary>Yalnızca Steam. Bugün yayında olan ürün.</summary>
    SteamPipeManager,

    /// <summary>Yalnızca Epic. Deneysel.</summary>
    EpicBuildManager,

    /// <summary>İkisi birden.</summary>
    PipeManagerHub,
}

/// <summary>
/// Çalışan ürünün kimliği ve yetenekleri.
///
/// Derleme zamanında assembly meta verisine yazılıyor, çalışma zamanında okunuyor —
/// koşullu derleme (<c>#if</c>) yerine bu tercih edildi: tek bir kod yolu var,
/// hepsi her üründe derleniyor ve test ediliyor, yalnızca görünürlük değişiyor.
/// </summary>
public sealed record ProductProfile(
    ProductId Id,
    string Name,
    IReadOnlyList<PublishProviderId> Providers)
{
    public static readonly ProductProfile SteamPipeManager = new(
        ProductId.SteamPipeManager,
        "Steam Pipe Manager",
        [PublishProviderId.Steam]);

    public static readonly ProductProfile EpicBuildManager = new(
        ProductId.EpicBuildManager,
        "Epic Build Manager",
        [PublishProviderId.Epic]);

    public static readonly ProductProfile PipeManagerHub = new(
        ProductId.PipeManagerHub,
        "Pipe Manager Hub",
        [PublishProviderId.Steam, PublishProviderId.Epic]);

    /// <summary>Meta veri okunamadığında kullanılan ürün: bugün yayında olan.</summary>
    public static ProductProfile Default => SteamPipeManager;

    public bool Supports(PublishProviderId provider) => Providers.Contains(provider);

    /// <summary>Birden çok sağlayıcı varsa kullanıcıya seçim sunulur.</summary>
    public bool HasProviderChoice => Providers.Count > 1;

    /// <summary>Yeni profil oluştururken varsayılan sağlayıcı.</summary>
    public PublishProviderId DefaultProvider => Providers[0];

    /// <summary>Epic desteği gerçek bir hesaba karşı doğrulanana kadar deneysel.</summary>
    public bool IsExperimental => Supports(PublishProviderId.Epic);

    /// <summary>
    /// Assembly meta verisindeki ürün adından çözer. Tanınmayan ya da eksik değer
    /// varsayılana düşüyor — yanlış bir değer yüzünden uygulamanın açılmaması,
    /// çözülmeye çalışılan sorundan çok daha kötü olurdu.
    /// </summary>
    public static ProductProfile Parse(string? value) => value?.Trim() switch
    {
        "EpicBuildManager" => EpicBuildManager,
        "PipeManagerHub" => PipeManagerHub,
        "SteamPipeManager" => SteamPipeManager,
        _ => Default,
    };

    /// <summary>
    /// Bir profilin bu üründe kullanılabilir olup olmadığı.
    ///
    /// Önemli: kullanıcı Hub'da Epic profili oluşturup sonra Steam Pipe Manager'a
    /// dönerse o profil <b>silinmemeli</b>, yalnızca gizlenmeli. Veri kaybı,
    /// ürünü değiştirmenin bedeli olamaz.
    /// </summary>
    public bool CanShow(UserProfile profile) => Supports(profile.Provider);
}
