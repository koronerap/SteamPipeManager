using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.Core.Storage;

public interface IProfileStore
{
    Task<ProfileDatabase> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(ProfileDatabase database, CancellationToken ct = default);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}

/// <summary>
/// JSON dosya deposu. Yazma önce geçici dosyaya yapılır, sonra yerine taşınır: yazma
/// sırasında uygulama çökerse mevcut dosya bozulmadan kalır.
///
/// Buradaki beklemeler arayüz iş parçacığına geri dönmüyor (<c>ConfigureAwait(false)</c>).
/// Saf dosya işi; arayüze dönmesi gereken bir şey yok, ve dönmeye çalışması arayüz
/// iş parçacığı bir kaydı beklerken kilitlenmeye yol açıyordu.
/// </summary>
public sealed class JsonFileStore<T>(string filePath, Func<T> createDefault)
    where T : class
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Windows yolları ve Türkçe karakterler dosyada okunabilir kalsın.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FilePath { get; } = filePath;

    public async Task<T> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath))
        {
            return createDefault();
        }

        // Eşzamanlı Dispose: 'await using' arayüz bağlamına geri dönmeye çalışırdı.
        using var stream = File.OpenRead(FilePath);

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct).ConfigureAwait(false)
                   ?? createDefault();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(FilePath)} could not be read: {ex.Message}", ex);
        }
    }

    public async Task SaveAsync(T value, CancellationToken ct = default)
    {
        // JSON çağıranın iş parçacığında, tek seferde üretiliyor. Model arayüzden
        // düzenleniyor; serileştirme yazma ile iç içe ilerleseydi arka planda
        // okunurken arayüz aynı listeyi değiştirebilirdi.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // Her yazma kendi geçici dosyasını kullanıyor. Sabit bir ad, yarıda kalmış
        // (ya da hâlâ açık tutulan) eski bir yazma yüzünden sonraki bütün kayıtların
        // başarısız olmasına yol açıyordu.
        var tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, ct).ConfigureAwait(false);
            File.Move(tempPath, FilePath, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Artık bir geçici dosya kalır; asıl dosyaya zararı yok.
        }
    }
}

public sealed class JsonProfileStore(WorkspaceLayout layout) : IProfileStore
{
    private readonly JsonFileStore<ProfileDatabase> _store = new(layout.ProfilesFile, () => new ProfileDatabase());

    public string FilePath => _store.FilePath;

    /// <summary>
    /// Okurken şema göçü uygulanır; diske ancak bir sonraki kaydetmede yeni sürümle
    /// yazılır. Böylece uygulamayı açıp hiçbir şey yapmamak kimsenin dosyasını değiştirmez.
    /// </summary>
    public async Task<ProfileDatabase> LoadAsync(CancellationToken ct = default) =>
        ProfileSchema.Upgrade(await _store.LoadAsync(ct).ConfigureAwait(false));

    public Task SaveAsync(ProfileDatabase database, CancellationToken ct = default) =>
        _store.SaveAsync(database, ct);
}

public sealed class JsonSettingsStore(WorkspaceLayout layout) : ISettingsStore
{
    private readonly JsonFileStore<AppSettings> _store = new(layout.SettingsFile, () => new AppSettings());

    public string FilePath => _store.FilePath;

    public Task<AppSettings> LoadAsync(CancellationToken ct = default) => _store.LoadAsync(ct);

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default) =>
        _store.SaveAsync(settings, ct);
}
