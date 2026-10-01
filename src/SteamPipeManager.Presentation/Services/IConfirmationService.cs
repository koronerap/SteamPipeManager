namespace SteamPipeManager.Presentation.Services;

/// <summary>
/// Geri alınamaz işlemler için onay. Arayüzden ayrıldı ki ViewModel'ler pencere
/// açmadan test edilebilsin; her arayüz (WPF, Avalonia) kendi penceresini veriyor.
/// </summary>
public interface IConfirmationService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false);
}
