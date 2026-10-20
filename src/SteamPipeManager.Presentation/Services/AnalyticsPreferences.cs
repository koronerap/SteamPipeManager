using System.Text.Json.Serialization;
using SteamPipeManager.Core.Analytics;
using SteamPipeManager.Core.Storage;

namespace SteamPipeManager.Presentation.Services;

/// <summary>Analitik tercihleri (<c>analytics-settings.json</c>); ana uygulama ve widget ortak.</summary>
public sealed class AnalyticsPreferences
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Tutarların yanında gösterilen para birimi. Null: kullanıcının bulunduğu ülkeninki;
    /// "USD": yalnızca dolar, çeviri yok.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>Ekran paylaşırken: bütün tutarlar "•••".</summary>
    public bool HideAmounts { get; set; }

    /// <summary>Kullanılan para birimi; dosyaya yazılmıyor, her açılışta bulunduğu ülkeden.</summary>
    [JsonIgnore]
    public string EffectiveCurrency => Currency ?? Currencies.OfHomeRegion() ?? "USD";
}

/// <summary>Tercihlerin dosyası. Okunamazsa varsayılanlar; yazılamazsa sonraki değişiklikte yeniden.</summary>
public sealed class AnalyticsPreferencesStore(string filePath)
{
    private readonly JsonFileStore<AnalyticsPreferences> _store = new(filePath, () => new AnalyticsPreferences());

    public AnalyticsPreferences Current { get; private set; } = new();

    /// <summary>Diskten yeniden okur: öteki süreç (widget ya da ana uygulama) değiştirmiş olabilir.</summary>
    public async Task<AnalyticsPreferences> LoadAsync()
    {
        try
        {
            Current = await _store.LoadAsync();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Current = new AnalyticsPreferences();
        }

        return Current;
    }

    public async Task SaveAsync()
    {
        try
        {
            await _store.SaveAsync(Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tercih bu oturumda geçerli; kalıcı olamadı.
        }
    }
}
