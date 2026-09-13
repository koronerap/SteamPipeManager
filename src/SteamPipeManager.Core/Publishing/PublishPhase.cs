namespace SteamPipeManager.Core.Publishing;

/// <summary>
/// Bir yayınlama işinin geçtiği aşamalar. Sağlayıcıdan bağımsız.
///
/// Ölçümle doğrulandı: SteamCMD ve BuildPatchTool aynı akışı izliyor — giriş/kimlik,
/// hazırlık, içeriğin taranması (BPT'de chunk üretimi), ağa yükleme, sonuç.
/// Arayüz bu modele bağlı olduğu için build paneli iki sağlayıcıyı da değişmeden
/// gösterebiliyor.
///
/// Aşama arayüzün değil, işin bir özelliği; bu yüzden ViewModel'de değil burada duruyor.
/// </summary>
public enum PublishPhase
{
    Idle,

    /// <summary>Steam: SteamCMD girişi · Epic: client kimlik doğrulaması.</summary>
    LoggingIn,

    Preparing,

    /// <summary>Steam: içerik taraması · Epic: chunk üretimi.</summary>
    Scanning,

    Uploading,
    Succeeded,
    Failed,
}

/// <summary>
/// Aşama geçişleri. Aşama <b>yalnızca ileri gider</b>.
///
/// Sebebi ölçülmüş bir hata: ilerleme satırları hem tarama hem yükleme sırasında
/// geliyor ("668.1MB (93%)"), doğrudan eşlendiğinde "Uploading content" göründükten
/// sonra başlık tekrar "İçerik taranıyor"a düşüyordu. BPT'de de aynı risk var —
/// <c>LogDataScanner</c> satırları yükleme başladıktan sonra da gelebiliyor.
/// </summary>
public static class PublishPhases
{
    /// <summary>Sıralamadaki yeri; geri düşmeyi engellemek için kullanılıyor.</summary>
    public static int Rank(this PublishPhase phase) => phase switch
    {
        PublishPhase.Idle => 0,
        PublishPhase.LoggingIn => 1,
        PublishPhase.Preparing => 2,
        PublishPhase.Scanning => 3,
        PublishPhase.Uploading => 4,
        _ => 5,
    };

    /// <summary>
    /// <paramref name="candidate"/> yalnızca mevcut aşamadan ileriyse geçerli olur.
    /// </summary>
    public static PublishPhase Advance(PublishPhase current, PublishPhase candidate) =>
        candidate.Rank() > current.Rank() ? candidate : current;

    public static bool IsFinished(this PublishPhase phase) =>
        phase is PublishPhase.Succeeded or PublishPhase.Failed;
}
