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
