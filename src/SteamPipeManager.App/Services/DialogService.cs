// WPF projesinin örtük using'leri System.IO'yu kapsamıyor.
using System.IO;
using Microsoft.Win32;

namespace SteamPipeManager.App.Services;

/// <summary>
/// Klasör/dosya seçme diyalogları. Arayüzden ayrıldı ki ViewModel'ler test edilebilsin.
/// </summary>
public interface IDialogService
{
    string? PickFolder(string title, string? initialDirectory = null);

    string? PickExecutable(string title, string? initialDirectory = null);

    /// <summary>Kaydedilecek dosyanın yerini sorar; iptal edilirse null.</summary>
    string? PickSaveFile(string title, string suggestedFileName, string filter);
}

public sealed class WindowsDialogService : IDialogService
{
    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        if (Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(string title, string suggestedFileName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = suggestedFileName,
            Filter = filter,
            AddExtension = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickExecutable(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "steamcmd.exe|steamcmd.exe|Uygulamalar (*.exe)|*.exe",
            CheckFileExists = true,
        };

        if (Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
