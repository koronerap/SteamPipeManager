using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using SteamPipeManager.Presentation.Services;

namespace SteamPipeManager.Desktop.Services;

/// <summary>Avalonia'nın dosya seçicileri; macOS ve Linux'ta sistemin kendi diyalogları.</summary>
public sealed class AvaloniaDialogService(Func<Window> owner) : IDialogService
{
    private IStorageProvider Storage => owner().StorageProvider;

    private async Task<IStorageFolder?> FolderOrNull(string? path) =>
        path is { Length: > 0 } && Directory.Exists(path)
            ? await Storage.TryGetFolderFromPathAsync(path)
            : null;

    public async Task<string?> PickFolderAsync(string title, string? initialDirectory = null)
    {
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await FolderOrNull(initialDirectory),
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>
    /// Linux ve macOS'ta çalıştırılabilir dosyaların uzantısı yok (BuildPatchTool) ya da
    /// <c>.sh</c> (steamcmd.sh); filtre koymak doğru dosyayı gizlerdi. Seçilen dosyayı
    /// çağıran taraf doğruluyor.
    /// </summary>
    public async Task<string?> PickExecutableAsync(string title, string? initialDirectory = null)
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await FolderOrNull(initialDirectory),
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedFileName, string extension)
    {
        var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(extension) { Patterns = [$"*.{extension}"] }],
        });

        return file?.TryGetLocalPath();
    }
}

/// <summary>Onay penceresi; geri alınamaz işlemlerden önce.</summary>
public sealed class AvaloniaConfirmationService(Func<Window> owner) : IConfirmationService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false) =>
        new ConfirmationDialog(title, message, confirmText, isDestructive).ShowDialog<bool>(owner());
}

public sealed class AvaloniaAppLifetime(IClassicDesktopStyleApplicationLifetime desktop) : IAppLifetime
{
    public void Shutdown() => desktop.Shutdown();
}
