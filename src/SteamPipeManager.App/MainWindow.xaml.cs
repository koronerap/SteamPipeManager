using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using SteamPipeManager.App.ViewModels;
using Wpf.Ui.Controls;

namespace SteamPipeManager.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Build hedefi ayarları otomatik kaydediliyor ama yazma kısa bir gecikmeyle
    /// yapılıyor; son düzenlemeden hemen sonra kapatılırsa kaybolmasın diye
    /// kapanışta bekleyen kayıt diske geçirilir.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.Workspace.FlushAsync().GetAwaiter().GetResult();
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Kısayollar code-behind'da işleniyor: WPF'te <c>InputBinding</c> görsel ağacın
    /// parçası olmadığı için DataContext'i miras almaz ve iç içe ViewModel'lerin
    /// komutlarına XAML'den bağlanamaz.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (DataContext is not ShellViewModel shell || !shell.IsWorkspace)
        {
            return;
        }

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (!ctrl)
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
                SelectSubApp(workspace, e.Key - Key.D1);
                e.Handled = true;
                break;
        }
    }

    private static void SelectSubApp(SubAppWorkspaceViewModel workspace, int index)
    {
        if (index >= 0 && index < workspace.SubApps.Count)
        {
            workspace.SelectedCard = workspace.SubApps[index];
        }
    }
}
