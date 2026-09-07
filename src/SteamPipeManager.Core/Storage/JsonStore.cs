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

        await using var stream = File.OpenRead(FilePath);

        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct) ?? createDefault();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(FilePath)} could not be read: {ex.Message}", ex);
        }
    }

    public async Task SaveAsync(T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        var tempPath = FilePath + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, Options, ct);
        }

        File.Move(tempPath, FilePath, overwrite: true);
    }
}

public sealed class JsonProfileStore(WorkspaceLayout layout) : IProfileStore
{
    private readonly JsonFileStore<ProfileDatabase> _store = new(layout.ProfilesFile, () => new ProfileDatabase());

    public string FilePath => _store.FilePath;

    public Task<ProfileDatabase> LoadAsync(CancellationToken ct = default) => _store.LoadAsync(ct);

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
