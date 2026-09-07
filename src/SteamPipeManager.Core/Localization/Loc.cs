namespace SteamPipeManager.Core.Localization;

/// <summary>
/// Çekirdek katmandaki kullanıcıya görünen metinler için kısayol.
///
/// Doğrulama uyarıları, içe aktarma uyarıları ve build engelleme sebepleri doğrudan
/// arayüzde gösteriliyor; sabit metin bırakılsa uygulama dili değiştiğinde bu
/// mesajlar çevrilmeden kalırdı.
/// </summary>
public static class Loc
{
    public static string T(string key) => Localizer.Current.Get(key);

    public static string T(string key, params object[] args) => Localizer.Current.Format(key, args);
}
