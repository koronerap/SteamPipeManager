using System.Windows;
using SteamPipeManager.Presentation.Services;

namespace SteamPipeManager.App.Services;

/// <summary>WPF onay penceresi.</summary>
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
