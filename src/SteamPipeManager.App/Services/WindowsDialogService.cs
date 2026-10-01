// WPF projesinin örtük using'leri System.IO'yu kapsamıyor.
using System.IO;
using Microsoft.Win32;
using SteamPipeManager.Presentation.Services;

namespace SteamPipeManager.App.Services;

/// <summary>WPF/Windows diyalogları; sonucu hemen döndürüyor.</summary>
public sealed class WindowsDialogService : IDialogService
{
    public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) =>
        Task.FromResult(PickFolder(title, initialDirectory));

    public Task<string?> PickExecutableAsync(string title, string? initialDirectory = null) =>
        Task.FromResult(PickExecutable(title, initialDirectory));

    public Task<string?> PickSaveFileAsync(string title, string suggestedFileName, string extension) =>
        Task.FromResult(PickSaveFile(title, suggestedFileName, $"*.{extension}|*.{extension}"));

    private static string? PickFolder(string title, string? initialDirectory = null)
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

    private static string? PickSaveFile(string title, string suggestedFileName, string filter)
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

    private static string? PickExecutable(string title, string? initialDirectory = null)
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
