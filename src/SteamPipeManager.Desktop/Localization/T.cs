using Avalonia.Data;
using Avalonia.Markup.Xaml;
using SteamPipeManager.Presentation.Localization;

namespace SteamPipeManager.Desktop.Localization;

/// <summary>
/// AXAML'de metin çekmek için: <c>Text="{loc:T Profile.Title}"</c>.
///
/// Sabit metin yerine bir bağlama döndürüyor; dil değiştiğinde arayüz yeniden
/// açılmadan güncelleniyor (Localizer "Item[]" bildirimi gönderiyor).
/// </summary>
public sealed class T : MarkupExtension
{
    public T()
    {
    }

    public T(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = AppLocalizer.Instance,
            Mode = BindingMode.OneWay,
        };
}
