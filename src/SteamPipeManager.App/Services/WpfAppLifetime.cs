using System.Windows;
using SteamPipeManager.Presentation.Services;

namespace SteamPipeManager.App.Services;

public sealed class WpfAppLifetime : IAppLifetime
{
    public void Shutdown() => Application.Current.Shutdown();
}
