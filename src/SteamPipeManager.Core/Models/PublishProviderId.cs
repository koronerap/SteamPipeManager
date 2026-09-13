namespace SteamPipeManager.Core.Models;

/// <summary>
/// Bir profilin build'lerini hangi mağazaya gönderdiği.
///
/// Profil düzeyinde tutuluyor çünkü kimlik doğrulama sağlayıcıya ait: Steam'de SteamCMD'nin
/// önbelleklediği oturum, Epic'te Dev Portal'dan alınan client id/secret. Aynı profilin iki
/// mağazaya birden gönderdiği bir durum yok — oyun aynı olsa bile hesaplar ayrı.
/// </summary>
public enum PublishProviderId
{
    Steam,

    /// <summary>
    /// Epic Games Store, BuildPatchTool üzerinden. Deneysel: gerçek bir Epic
    /// yayıncı hesabına karşı henüz doğrulanmadı.
    /// </summary>
    Epic,
}
