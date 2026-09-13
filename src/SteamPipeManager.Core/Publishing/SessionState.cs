namespace SteamPipeManager.Core.Publishing;

/// <summary>
/// Bir profilin şu anda yayın yapmaya hazır olup olmadığı.
///
/// Sağlayıcıdan bağımsız, çünkü soru ikisinde de aynı — cevabın nasıl bulunduğu farklı:
///
///   * Steam: SteamCMD'nin önbelleklediği oturum hâlâ geçerli mi (<c>+login</c> denenir)
///   * Epic: client id/secret hâlâ çalışıyor mu (<c>UploadBinary -DryRun</c> çalıştırılır)
///
/// Arayüzdeki profil kartı bu modele bağlı olduğu için iki sağlayıcıyı da değişmeden
/// gösterebiliyor.
/// </summary>
public enum SessionState
{
    /// <summary>Henüz kontrol edilmedi.</summary>
    Unknown,

    /// <summary>Kimlik doğrulanmış; build etkileşimsiz çalışabilir.</summary>
    Active,

    /// <summary>Giriş ya da kimlik bilgisi girilmesi gerekiyor.</summary>
    LoginRequired,

    /// <summary>Kontrol zaman aşımına uğradı ya da beklenmedik bir hata verdi.</summary>
    CheckFailed,
}

public sealed record SessionCheckResult(SessionState State, string Detail)
{
    public bool CanBuild => State == SessionState.Active;

    /// <summary>
    /// Yalnızca Steam: giriş satırından okunan hesap numarası, avatar çekmek için.
    /// Epic profillerinde null.
    /// </summary>
    public ulong? SteamId64 { get; init; }
}
