using System.Windows.Data;
using System.Windows.Markup;
using SteamPipeManager.Core.Localization;

namespace SteamPipeManager.App.Localization;

/// <summary>
/// XAML'de metin çekmek için: <c>Text="{loc:T Profile.Title}"</c>.
///
/// Sabit metin yerine <see cref="Binding"/> döndürür; böylece dil değiştiğinde
/// arayüz yeniden açılmadan güncellenir.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = AppLocalizer.Instance,
            Mode = BindingMode.OneWay,
        };

        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>
/// XAML'in erişebilmesi için tekil örnek. Çekirdek katmanla aynı örnektir, böylece
/// oradaki mesajlar da aynı dille çevrilir.
/// </summary>
public static class AppLocalizer
{
    public static Localizer Instance => Localizer.Current;
}
