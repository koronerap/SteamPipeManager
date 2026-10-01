namespace SteamPipeManager.Presentation.Services;

/// <summary>
/// Klasör/dosya seçme diyalogları.
///
/// Asenkron, çünkü Avalonia'nın (ve macOS/Linux'un) dosya seçicileri asenkron;
/// WPF uygulaması sonucu hemen döndürüyor.
/// </summary>
public interface IDialogService
{
    Task<string?> PickFolderAsync(string title, string? initialDirectory = null);

    /// <summary>
    /// Çalıştırılabilir bir dosya seçtirir. Uzantı platforma göre değişiyor
    /// (Windows'ta <c>.exe</c>, Linux/macOS'ta uzantısız ya da <c>.sh</c>), filtreyi
    /// her arayüz kendisi kuruyor.
    /// </summary>
    Task<string?> PickExecutableAsync(string title, string? initialDirectory = null);

    /// <summary>Kaydedilecek dosyanın yerini sorar; iptal edilirse null.</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedFileName, string extension);
}
