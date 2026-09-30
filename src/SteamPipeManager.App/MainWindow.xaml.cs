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

    private bool _closeApproved;
    private bool _closing;

    /// <summary>
    /// Build hedefi ayarları otomatik kaydediliyor ama yazma kısa bir gecikmeyle
    /// yapılıyor; son düzenlemeden hemen sonra kapatılırsa kaybolmasın diye kapanış
    /// bekleyen kayıt bitene kadar erteleniyor.
    ///
    /// Kayıt burada <b>beklenerek</b> (bloklayarak) yapılamaz: kayıt tamamlanmak için
    /// arayüz iş parçacığına dönmek istiyor, arayüz iş parçacığı da kaydı bekliyorsa
    /// ikisi birbirini sonsuza kadar bekler. Eski sürümde tam olarak bu oluyordu:
    /// pencere kayboluyor, süreç açık kalıyor ve kayıt dosyasını kilitli tutarak
    /// sonraki açılışlardaki kayıtları da bozuyordu.
    ///
    /// Bu yüzden kapanış bir kez iptal ediliyor, kayıt beklenmeden (async) bitiriliyor
    /// ve pencere yeniden kapatılıyor.
    /// </summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (e.Cancel || _closeApproved ||
            DataContext is not ShellViewModel shell || !shell.HasUnsavedChanges)
        {
            return;
        }

        e.Cancel = true;

        // Kayıt sürerken çarpıya yeniden basılırsa ikinci bir kayıt başlatılmaz.
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

        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // Pencere bu arada başka bir yoldan (ör. uygulama kapanışı) kapandı.
        }
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
