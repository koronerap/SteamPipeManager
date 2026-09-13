namespace SteamPipeManager.Core.Epic;

/// <summary>
/// BuildPatchTool log satırından çıkarılan olay türü.
///
/// Steam'deki karşılığından ayrı duruyor çünkü araçların kelime dağarcığı farklı;
/// ortak olan <see cref="Publishing.PublishPhase"/> tarafında birleşiyorlar.
/// </summary>
public enum BptEventKind
{
    /// <summary>Sınıflandırılmamış satır; log panelinde düz metin olarak gösterilir.</summary>
    Info,

    /// <summary>Aracın çalıştırıldığı yapılandırma bloğu.</summary>
    Configuration,

    /// <summary>Chunk üretimi / yükleme başladı.</summary>
    Started,

    /// <summary>Build kökündeki dosyalar sayıldı.</summary>
    FilesEnumerated,

    /// <summary>İçerik tarama ilerlemesi (taranan bayt konumu).</summary>
    ScanProgress,

    /// <summary>Ağa yükleme ilerlemesi.</summary>
    UploadProgress,

    /// <summary>Manifest diske yazıldı.</summary>
    ManifestSaved,

    /// <summary>İş başarıyla tamamlandı.</summary>
    Succeeded,

    /// <summary>
    /// <c>-DryRun</c> doğrulaması geçti: argümanlar, kimlik bilgileri ve backend
    /// durumu tamam. Hiçbir şey yüklenmedi.
    /// </summary>
    DryRunPassed,

    /// <summary><c>-DryRun</c> doğrulaması kaldı; sebep ayrı satırlarda.</summary>
    DryRunFailed,

    /// <summary>Kimlik doğrulama başarısız.</summary>
    AuthenticationFailed,

    /// <summary>Zorunlu bir parametre eksik ya da geçersiz.</summary>
    ValidationFailed,

    /// <summary>Araç hata koduyla çıktı.</summary>
    Failed,
}

/// <summary>
/// BuildPatchTool log dosyasından ayrıştırılmış tek bir olay.
/// </summary>
/// <param name="Kind">Olayın türü.</param>
/// <param name="Message">Kullanıcıya gösterilebilecek metin.</param>
/// <param name="RawLine">Ham satır; log panelinde ve arşivde bu tutuluyor.</param>
/// <param name="Timestamp">Satırın kendi zaman damgası (varsa).</param>
public sealed record BptEvent(
    BptEventKind Kind,
    string Message,
    string RawLine,
    DateTimeOffset? Timestamp = null)
{
    /// <summary>Unreal log kategorisi (<c>LogBuildPatchTool</c>, <c>LogDataScanner</c>…).</summary>
    public string? Category { get; init; }

    /// <summary>
    /// Tarama ilerlemesindeki bayt konumu. Toplam boyut bilindiğinde yüzdeye çevrilir;
    /// aracın kendisi yüzde bildirmiyor.
    /// </summary>
    public long? ByteOffset { get; init; }

    /// <summary>0–100 arası ilerleme; hesaplanabildiğinde dolu.</summary>
    public double? Percent { get; init; }

    /// <summary>Sayılan dosya adedi.</summary>
    public int? FileCount { get; init; }

    /// <summary>
    /// Aracın çıkış sebebinin sembolik adı (<c>MissingCredentials</c>,
    /// <c>ArgumentProcessingError</c>). Sayıdan çok daha okunur.
    /// </summary>
    public string? ExitReason { get; init; }

    /// <summary>Çıkış kodu, sembolik adın yanındaki sayı.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Yayınlanan sürüm etiketi (<c>BuildVersion</c>).</summary>
    public string? BuildVersion { get; init; }

    /// <summary>Hedef artifact.</summary>
    public string? ArtifactId { get; init; }

    public bool IsFailure =>
        Kind is BptEventKind.Failed or BptEventKind.AuthenticationFailed
            or BptEventKind.ValidationFailed;
}
