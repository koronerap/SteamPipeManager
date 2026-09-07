using System.Windows;

namespace SteamPipeManager.App.Services;

/// <summary>
/// Geri alınamaz işlemler için onay. Arayüzden ayrıldı ki ViewModel'ler pencere
/// açmadan test edilebilsin.
/// </summary>
public interface IConfirmationService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false);
}

public sealed class MessageBoxConfirmationService : IConfirmationService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = false)
    {
        var result = MessageBox.Show(
            message,
            title,
            MessageBoxButton.OKCancel,
            isDestructive ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.Cancel);

        return Task.FromResult(result == MessageBoxResult.OK);
    }
}
