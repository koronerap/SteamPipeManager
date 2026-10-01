using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SteamPipeManager.Presentation.Localization;

namespace SteamPipeManager.Desktop.Services;

/// <summary>
/// Basit onay penceresi. Avalonia'da hazır bir mesaj kutusu yok; bu, WPF sürümündeki
/// MessageBox'ın karşılığı. Varsayılan seçenek "vazgeç": Enter yanlışlıkla silmesin.
/// </summary>
public sealed class ConfirmationDialog : Window
{
    public ConfirmationDialog(string title, string message, string confirmText, bool isDestructive)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var cancel = new Button
        {
            Content = AppLocalizer.Instance.Get("Common.Cancel"),
            IsDefault = true,
            IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0),
        };
        cancel.Click += (_, _) => Close(false);

        var confirm = new Button { Content = confirmText };
        confirm.Classes.Add(isDestructive ? "danger" : "accent");
        confirm.Click += (_, _) => Close(true);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children = { confirm, cancel },
                },
            },
        };
    }
}
