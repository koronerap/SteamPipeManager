using System.Windows.Data;
using System.Windows.Markup;
using SteamPipeManager.Core.Localization;

using SteamPipeManager.Presentation.Localization;

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
