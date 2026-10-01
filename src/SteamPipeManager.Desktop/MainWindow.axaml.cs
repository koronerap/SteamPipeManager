using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Desktop;

public sealed partial class MainWindow : Window
{
    private bool _closeApproved;
    private bool _closing;

    /// <summary>Saydamlık hiç yoksa kullanılan düz zemin (Linux'ta çoğu masaüstü).</summary>
    private static readonly IBrush SolidBackground = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1F));

    /// <summary>
    /// Akrilik ve bulanıklıkta zemin biraz kapatılıyor: arkadaki açık renkli pencereler
    /// metni okunmaz yapmasın. Mica'da Windows bu tonu kendisi veriyor.
    /// </summary>
    private static readonly IBrush TintedBackground = new SolidColorBrush(Color.FromArgb(0xB8, 0x1C, 0x1C, 0x1F));

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        ApplyBackdrop(ActualTransparencyLevel);
    }

    /// <summary>
    /// WPF sürümündeki buzlu (Mica) zeminin karşılığı. Windows 11'de Mica, Windows 10 ve
    /// macOS'ta akrilik, bulanıklığı destekleyen Linux masaüstlerinde bulanıklık; hiçbiri
    /// yoksa düz koyu zemin. Sistem hangisini verdiyse ona göre arka plan seçiliyor.
    /// </summary>
    private void ApplyBackdrop(WindowTransparencyLevel level)
    {
        Background =
            level == WindowTransparencyLevel.Mica ? Brushes.Transparent
            : level == WindowTransparencyLevel.AcrylicBlur || level == WindowTransparencyLevel.Blur ? TintedBackground
            : SolidBackground;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ActualTransparencyLevelProperty)
        {
            ApplyBackdrop(ActualTransparencyLevel);
        }
    }

    /// <summary>
    /// Bekleyen otomatik kayıt kapanmadan önce yazılıyor. Kapanış bir kez iptal edilip
    /// kayıt beklenmeden tamamlanıyor, sonra pencere yeniden kapatılıyor — kaydı
    /// bloklayarak beklemek arayüz iş parçacığını kilitlerdi (WPF sürümünde yaşandı).
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel || _closeApproved || DataContext is not ShellViewModel shell || !shell.HasUnsavedChanges)
        {
            return;
        }

        e.Cancel = true;

        if (_closing)
        {
            return;
        }

        _closing = true;

        try
        {
            if (!await shell.PrepareToCloseAsync())
            {
                return;
            }
        }
        finally
        {
            _closing = false;
        }

        _closeApproved = true;
        Close();
    }

    /// <summary>
    /// Kısayollar: Ctrl+B build, Ctrl+Shift+B önizleme, Ctrl+S kaydet, Ctrl+1..9 hedef
    /// seçimi. macOS'ta Ctrl yerine Cmd de kabul ediliyor.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (DataContext is not ShellViewModel shell || !shell.IsSteamWorkspace)
        {
            return;
        }

        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (!command)
        {
            return;
        }

        var workspace = shell.Workspace;
        var build = workspace.BuildPanel;

        switch (e.Key)
        {
            case Key.B when shift && build.PreviewCommand.CanExecute(null):
                build.PreviewCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.B when build.BuildCommand.CanExecute(null):
                build.BuildCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.S when workspace.SaveCommand.CanExecute(null):
                workspace.SaveCommand.Execute(null);
                e.Handled = true;
                break;

            case >= Key.D1 and <= Key.D9:
                var index = e.Key - Key.D1;

                if (index < workspace.SubApps.Count)
                {
                    workspace.SelectedCard = workspace.SubApps[index];
                }

                e.Handled = true;
                break;
        }
    }
}
