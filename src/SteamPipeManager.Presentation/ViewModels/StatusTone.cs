namespace SteamPipeManager.Presentation.ViewModels;

/// <summary>
/// Durum rengi, arayüzden bağımsız. ViewModel "başarılı / uyarı / hata" der; hangi
/// fırçayla çizileceğine her arayüz kendi temasıyla karar verir.
/// </summary>
public enum StatusTone
{
    Neutral,
    Muted,
    Info,
    Success,
    Warning,
    Danger,
}
