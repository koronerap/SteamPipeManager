namespace SteamPipeManager.Presentation.Services;

/// <summary>Uygulamayı kapatma; güncelleyici yeni sürümü başlattıktan sonra kullanıyor.</summary>
public interface IAppLifetime
{
    void Shutdown();
}
