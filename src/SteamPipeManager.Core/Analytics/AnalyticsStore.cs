using SteamPipeManager.Core.Storage;

namespace SteamPipeManager.Core.Analytics;

/// <summary>Bir oyuncu sayısı örneği: Unix saniyesi ve sayı.</summary>
public sealed record PlayerSample(long T, int C)
{
    public DateTimeOffset At => DateTimeOffset.FromUnixTimeSeconds(T);
}

/// <summary>
/// Bir saatin oyuncu özeti: saatin başı (Unix saniyesi), en yüksek, ortalama ve örnek
/// sayısı. Eski örnekler bu hâle getirilip saklanıyor; uzun aralıklı grafikler buradan.
/// </summary>
public sealed record PlayerHour(long T, int Max, int Avg, int N)
{
    public DateTimeOffset At => DateTimeOffset.FromUnixTimeSeconds(T);
}

/// <summary>Bir app'in kaydedilen oyuncu sayıları.</summary>
public sealed class PlayerHistory
{
    /// <summary>Son <see cref="AnalyticsService.PlayerRetention"/> içindeki her örnek.</summary>
    public List<PlayerSample> Samples { get; set; } = [];

    /// <summary>Daha eski örneklerin saatlik özetleri, eskiden yeniye.</summary>
    public List<PlayerHour> Hours { get; set; } = [];

    /// <summary>Son sorgu; Steam'de verisi olmayan app'lerde de dolu, böylece her turda sorulmuyor.</summary>
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>Son sorguda Steam'in verisi var mıydı (yayınlanmamış app'lerde yok).</summary>
    public bool Available { get; set; }
}

/// <summary>
/// Bir günün wishlist satırı; kısa adlar dosya küçük kalsın diye: eklenen, silinen,
/// satın alınan, hediye edilen ve satırın indirildiği gün (<c>F</c>). Gün bitmeden
/// okunan satır sonra yeniden indiriliyor.
/// </summary>
public sealed record WishlistRow(int A, int D, int P, int G, DateOnly? F = null)
{
    public int Net => A - D - P - G;
}

/// <summary>Bir app'in günlük wishlist kayıtları (GMT günleri, <c>yyyy-MM-dd</c>).</summary>
public sealed class WishlistLedger
{
    /// <summary>Steam'in bu app için verisinin başladığı gün; toplam buradan itibaren hesaplanıyor.</summary>
    public DateOnly? AppMinDate { get; set; }

    public Dictionary<string, WishlistRow> Days { get; set; } = [];

    public DateTimeOffset? LastCheckedAt { get; set; }
}

/// <summary>Bir günün bir app'e ait satış satırı.</summary>
public sealed record SalesRow(int Units, decimal NetUsd, decimal GrossUsd)
{
    public SalesTotals Totals => new(Units, NetUsd, GrossUsd);
}

/// <summary>
/// Bir ortağın (anahtarın) bütün satışları, günlere (Pasifik) ve app'lere göre. Anahtar
/// ortağın bütün app'lerini gördüğü için anahtar başına tek dosya.
/// </summary>
public sealed class SalesLedger
{
    /// <summary>Steam'in değişen günler için verdiği sınır; bir sonraki sorguda gönderiliyor.</summary>
    public ulong Highwatermark { get; set; }

    /// <summary>
    /// Değiştiği bildirilip henüz okunmamış günler. Sınırla birlikte kaydediliyor: yarıda
    /// kalan bir yenilemede hiçbir gün kaybolmuyor.
    /// </summary>
    public List<string> PendingDates { get; set; } = [];

    public Dictionary<string, Dictionary<uint, SalesRow>> Days { get; set; } = [];

    public Dictionary<uint, string> AppNames { get; set; } = [];

    public DateTimeOffset? LastCheckedAt { get; set; }
}

/// <summary>Bir finans hesabının (anahtarın) son durumu.</summary>
public sealed class FinancialAccountState
{
    /// <summary>Bu anahtarla veri neden alınamadı; null ise sorun yok.</summary>
    public AnalyticsFailure? Failure { get; set; }

    public DateTimeOffset? FailureAt { get; set; }
}

/// <summary>Son yenilemenin durumu; arayüz ve widget hatayı buradan gösteriyor.</summary>
public sealed class AnalyticsState
{
    /// <summary>Anahtar kimliğine (<see cref="SteamFinancialKeyStore.Fingerprint"/>) göre.</summary>
    public Dictionary<string, FinancialAccountState> Accounts { get; set; } = [];

    /// <summary>Wishlist ya da satış geçmişinin bir kısmı henüz indirilmedi.</summary>
    public bool FinancialBackfillPending { get; set; }

    public DateTimeOffset? LastRefreshAt { get; set; }
}

/// <summary>
/// Analitik önbelleği (<c>analytics/</c> klasörü). Ana uygulama ve widget aynı klasörü
/// kullanıyor; aynı anda yalnızca biri yeniliyor (<see cref="TryLock"/>), diğeri okuyor.
/// </summary>
public sealed class AnalyticsStore(string directory)
{
    public string Directory { get; } = directory;

    private JsonFileStore<PlayerHistory> Players(uint appId) =>
        new(Path.Combine(Directory, "players", $"{appId}.json"), () => new PlayerHistory());

    private JsonFileStore<WishlistLedger> Wishlist(uint appId) =>
        new(Path.Combine(Directory, "wishlist", $"{appId}.json"), () => new WishlistLedger());

    private JsonFileStore<SalesLedger> Sales(string accountId) =>
        new(Path.Combine(Directory, "sales", $"{accountId}.json"), () => new SalesLedger());

    private JsonFileStore<AnalyticsState> State => new(Path.Combine(Directory, "state.json"), () => new AnalyticsState());

    public Task<PlayerHistory> LoadPlayersAsync(uint appId, CancellationToken ct = default) =>
        LoadOrEmptyAsync(Players(appId), () => new PlayerHistory(), ct);

    public Task SavePlayersAsync(uint appId, PlayerHistory history, CancellationToken ct = default) =>
        Players(appId).SaveAsync(history, ct);

    public Task<WishlistLedger> LoadWishlistAsync(uint appId, CancellationToken ct = default) =>
        LoadOrEmptyAsync(Wishlist(appId), () => new WishlistLedger(), ct);

    public Task SaveWishlistAsync(uint appId, WishlistLedger ledger, CancellationToken ct = default) =>
        Wishlist(appId).SaveAsync(ledger, ct);

    public Task<SalesLedger> LoadSalesAsync(string accountId, CancellationToken ct = default) =>
        LoadOrEmptyAsync(Sales(accountId), () => new SalesLedger(), ct);

    public Task SaveSalesAsync(string accountId, SalesLedger ledger, CancellationToken ct = default) =>
        Sales(accountId).SaveAsync(ledger, ct);

    public Task<AnalyticsState> LoadStateAsync(CancellationToken ct = default) =>
        LoadOrEmptyAsync(State, () => new AnalyticsState(), ct);

    public Task SaveStateAsync(AnalyticsState state, CancellationToken ct = default) => State.SaveAsync(state, ct);

    /// <summary>
    /// Bozuk bir önbellek dosyası uygulamayı durdurmamalı: veri Steam'den yeniden
    /// alınabiliyor, dosya boş sayılıyor.
    /// </summary>
    private static async Task<T> LoadOrEmptyAsync<T>(JsonFileStore<T> store, Func<T> empty, CancellationToken ct)
        where T : class
    {
        try
        {
            return await store.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return empty();
        }
    }

    /// <summary>
    /// Yenileme kilidi. Başka bir süreç (ana uygulama ya da widget) yeniliyorsa null:
    /// iki süreç aynı anda Steam'e sorup birbirinin dosyasını ezmesin. Kilit dosyası
    /// süreç kapanınca kendiliğinden bırakılıyor.
    /// </summary>
    public IDisposable? TryLock()
    {
        System.IO.Directory.CreateDirectory(Directory);

        try
        {
            return new FileStream(
                Path.Combine(Directory, ".refresh.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
