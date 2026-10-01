using SteamPipeManager.Core.Localization;

namespace SteamPipeManager.Presentation.Localization;

/// <summary>
/// ViewModel'lerin ve arayüzlerin eriştiği tekil örnek. Çekirdek katmanla aynı
/// örnektir, böylece oradaki mesajlar da aynı dille çevrilir.
/// </summary>
public static class AppLocalizer
{
    public static Localizer Instance => Localizer.Current;
}
