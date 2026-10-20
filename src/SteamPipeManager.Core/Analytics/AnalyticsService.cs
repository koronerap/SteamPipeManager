namespace SteamPipeManager.Core.Analytics;

public sealed record PlayerStats(
    int? Current,
    DateTimeOffset? At,
    int? Peak24Hours,
    IReadOnlyList<PlayerSample> Last24Hours,
    bool Available);

public sealed record WishlistCounts(int Adds, int Deletes, int Purchases, int Gifts)
{
    public static readonly WishlistCounts Zero = new(0, 0, 0, 0);

    public int Net => Adds - Deletes - Purchases - Gifts;
}

public sealed record DailyValue(DateOnly Date, decimal Value);

/// <param name="Total">
/// Wishlist'te duran toplam: ilk günden bu yana eklenen, eksi silinen, satın alınan ve
/// hediye edilen. Steam bu sayıyı vermiyor, günlük hareketlerden toplanıyor.
/// </param>
/// <param name="IsComplete">Bütün günler indirildi mi; değilse toplam henüz eksik.</param>
public sealed record WishlistStats(
    int Total,
    WishlistCounts Today,
    WishlistCounts Yesterday,
    IReadOnlyList<DailyValue> Last30Days,
    bool IsComplete,
    DateOnly? Since);

/// <summary>Satışlar Pasifik günlerine göre; gelir Steam payı düşülmeden.</summary>
public sealed record SalesStats(
    SalesTotals Today,
    SalesTotals Yesterday,
    SalesTotals Last7Days,
    SalesTotals Last30Days,
    SalesTotals Lifetime,
    IReadOnlyList<DailyValue> Last30DaysNetUsd,
    bool IsComplete);

public sealed record AppAnalytics(uint AppId, string? Name, PlayerStats Players, WishlistStats? Wishlist, SalesStats? Sales);

public enum RefreshResult
{
    /// <summary>Gereken her şey yenilendi.</summary>
    Done,

    /// <summary>Geçmiş verinin bir kısmı kaldı; kısa süre sonra yeniden çağrılmalı.</summary>
    MorePending,

    /// <summary>Başka bir süreç (ana uygulama ya da widget) yeniliyor; önbellek okunabilir.</summary>
    Busy,
}

/// <summary>
/// Analitik verisini yeniler ve önbellekten özetler. Ana uygulama ve widget aynı
/// sınıfı kullanıyor; önbellek ortak, aynı anda yalnızca biri yeniliyor.
///
/// Sıklıklar: oyuncu sayısı 5 dakikada bir, wishlist ve satış 3 saatte bir (Valve
/// finans verisini günde en az bir kez tazeliyor). Finans geçmişinin ilk indirmesi
/// (ilk günden bugüne) her yenilemede sınırlı sayıda istekle, yavaşça ilerliyor.
/// </summary>
public sealed class AnalyticsService(
    AnalyticsStore store,
    SteamPlayerCountClient players,
    SteamFinancialClient financial,
    Func<string?> readKey,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static readonly TimeSpan PlayerRetention = TimeSpan.FromDays(30);

    public TimeSpan PlayersInterval { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan FinancialInterval { get; init; } = TimeSpan.FromHours(3);

    /// <summary>Bir yenilemede finans servisine en fazla bu kadar istek.</summary>
    public int FinancialRequestBudget { get; init; } = 120;

    /// <summary>Finans istekleri arasındaki en kısa süre; Valve sınır yayınlamıyor, temkinli.</summary>
    public TimeSpan FinancialRequestSpacing { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Kaydedilen her 25 günde bir önbelleğe yazılıyor; yarıda kalan ilerleme kaybolmasın.</summary>
    private const int SaveEvery = 25;

    public async Task<RefreshResult> RefreshAsync(
        IReadOnlyCollection<uint> appIds, bool force = false, CancellationToken ct = default)
    {
        using var refreshLock = store.TryLock();

        if (refreshLock is null)
        {
            return RefreshResult.Busy;
        }

        var now = _time.GetUtcNow();

        foreach (var appId in appIds.Distinct())
        {
            await RefreshPlayersAsync(appId, now, force, ct).ConfigureAwait(false);
        }

        var state = await store.LoadStateAsync(ct).ConfigureAwait(false);
        state.LastRefreshAt = now;

        var pending = false;

        if (readKey() is { Length: > 0 } key)
        {
            var budget = new RequestBudget(FinancialRequestBudget, FinancialRequestSpacing, _time);
            financial.BeforeRequest = budget.TakeAsync;

            try
            {
                // Önce güncel veri (bugün, dün, değişen satış günleri), sonra geçmiş: ilk
                // açılışta da ekrandaki sayılar hemen dolsun.
                foreach (var appId in appIds.Distinct())
                {
                    await RefreshRecentWishlistAsync(key, appId, now, force, ct).ConfigureAwait(false);
                }

                var sales = await RefreshChangedSalesAsync(key, now, force, ct).ConfigureAwait(false);

                // Bütçe biterse BudgetExhaustedException ile çıkılıyor: iş kaldı demek.
                await DownloadPendingSalesAsync(key, sales, ct).ConfigureAwait(false);

                foreach (var appId in appIds.Distinct())
                {
                    await BackfillWishlistAsync(key, appId, now, ct).ConfigureAwait(false);
                }

                state.FinancialFailure = null;
                state.FinancialFailureAt = null;
            }
            catch (BudgetExhaustedException)
            {
                pending = true;
                state.FinancialFailure = null;
            }
            catch (AnalyticsException ex)
            {
                state.FinancialFailure = ex.Failure;
                state.FinancialFailureAt = now;
            }
            finally
            {
                financial.BeforeRequest = null;
            }
        }
        else
        {
            state.FinancialFailure = null;
        }

        state.FinancialBackfillPending = pending;
        await store.SaveStateAsync(state, ct).ConfigureAwait(false);

        return pending ? RefreshResult.MorePending : RefreshResult.Done;
    }

    private async Task RefreshPlayersAsync(uint appId, DateTimeOffset now, bool force, CancellationToken ct)
    {
        var history = await store.LoadPlayersAsync(appId, ct).ConfigureAwait(false);

        if (!force && history.LastCheckedAt is { } last && now - last < PlayersInterval)
        {
            return;
        }

        int? count;

        try
        {
            count = await players.GetCurrentPlayersAsync(appId, ct).ConfigureAwait(false);
        }
        catch (AnalyticsException)
        {
            // Ağ yok: sorgu zamanı güncellenmiyor, bir sonraki turda yeniden deneniyor.
            return;
        }

        history.LastCheckedAt = now;
        history.Available = count is not null;

        if (count is { } value)
        {
            history.Samples.Add(new PlayerSample(now.ToUnixTimeSeconds(), value));
        }

        var oldest = (now - PlayerRetention).ToUnixTimeSeconds();
        history.Samples.RemoveAll(s => s.T < oldest);

        await store.SavePlayersAsync(appId, history, ct).ConfigureAwait(false);
    }

    private async Task RefreshRecentWishlistAsync(string key, uint appId, DateTimeOffset now, bool force, CancellationToken ct)
    {
        var ledger = await store.LoadWishlistAsync(appId, ct).ConfigureAwait(false);

        if (!force && ledger.LastCheckedAt is { } last && now - last < FinancialInterval)
        {
            return;
        }

        var today = UtcDay(now);

        try
        {
            foreach (var date in new[] { today, today.AddDays(-1) })
            {
                Record(ledger, await financial.GetWishlistDayAsync(key, appId, date, ct).ConfigureAwait(false), today);
            }

            ledger.LastCheckedAt = now;
        }
        finally
        {
            await store.SaveWishlistAsync(appId, ledger, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Eksik ya da gün bitmeden okunmuş günleri en yeniden geriye doğru indirir. Bütçe
    /// biterse <see cref="BudgetExhaustedException"/> yukarı çıkıyor; inenler kaydedilmiş.
    /// </summary>
    private async Task BackfillWishlistAsync(string key, uint appId, DateTimeOffset now, CancellationToken ct)
    {
        var ledger = await store.LoadWishlistAsync(appId, ct).ConfigureAwait(false);

        if (ledger.AppMinDate is not { } first)
        {
            return;
        }

        var today = UtcDay(now);
        var missing = MissingWishlistDays(ledger, first, today).ToList();

        if (missing.Count == 0)
        {
            return;
        }

        var written = 0;

        try
        {
            foreach (var date in missing)
            {
                Record(ledger, await financial.GetWishlistDayAsync(key, appId, date, ct).ConfigureAwait(false), today);

                if (++written % SaveEvery == 0)
                {
                    await store.SaveWishlistAsync(appId, ledger, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await store.SaveWishlistAsync(appId, ledger, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Yeniden istenmesi gereken günler, en yeniden: hiç indirilmemiş ya da bittikten
    /// sonra okunmamış olanlar. Bugün ve dün zaten her turda yenileniyor.
    /// </summary>
    private static IEnumerable<DateOnly> MissingWishlistDays(WishlistLedger ledger, DateOnly first, DateOnly today)
    {
        for (var date = today.AddDays(-2); date >= first; date = date.AddDays(-1))
        {
            if (!ledger.Days.TryGetValue(SteamWebApi.DateText(date), out var row) || !IsFinal(row, date))
            {
                yield return date;
            }
        }
    }

    /// <summary>Valve günü ertesi gün içinde tamamlıyor; iki gün sonra okunan satır kesin.</summary>
    private static bool IsFinal(WishlistRow row, DateOnly date) => row.F is { } fetched && fetched >= date.AddDays(2);

    private static void Record(WishlistLedger ledger, WishlistDay day, DateOnly today)
    {
        ledger.Days[SteamWebApi.DateText(day.Date)] = new WishlistRow(day.Adds, day.Deletes, day.Purchases, day.Gifts, today);

        if (day.AppMinDate is { } min)
        {
            ledger.AppMinDate = min;
        }
    }

    private async Task<SalesLedger> RefreshChangedSalesAsync(string key, DateTimeOffset now, bool force, CancellationToken ct)
    {
        var ledger = await store.LoadSalesAsync(ct).ConfigureAwait(false);

        if (!force && ledger.LastCheckedAt is { } last && now - last < FinancialInterval)
        {
            return ledger;
        }

        var changed = await financial.GetChangedDatesAsync(key, ledger.Highwatermark, ct).ConfigureAwait(false);

        foreach (var date in changed.Dates.Select(SteamWebApi.DateText))
        {
            if (!ledger.PendingDates.Contains(date))
            {
                ledger.PendingDates.Add(date);
            }
        }

        // Sınır, bekleyen günlerle aynı dosyada: günler okunmadan kapanırsa bir sonraki
        // açılışta bekleyenler listesinden devam ediliyor.
        ledger.Highwatermark = changed.Highwatermark;
        ledger.LastCheckedAt = now;
        await store.SaveSalesAsync(ledger, ct).ConfigureAwait(false);

        return ledger;
    }

    private async Task DownloadPendingSalesAsync(string key, SalesLedger ledger, CancellationToken ct)
    {
        if (ledger.PendingDates.Count == 0)
        {
            return;
        }

        var written = 0;

        try
        {
            // En yeni günler önce: bugün ve dün hemen görünsün.
            foreach (var text in ledger.PendingDates.OrderByDescending(d => d, StringComparer.Ordinal).ToList())
            {
                if (SteamWebApi.Date(text) is not { } date)
                {
                    ledger.PendingDates.Remove(text);
                    continue;
                }

                var day = await financial.GetSalesDayAsync(key, date, ct).ConfigureAwait(false);

                // Değişen gün baştan okunuyor ve yerine yazılıyor (Valve: "re-query the
                // entirety of that date"); üstüne eklenmiyor.
                ledger.Days[text] = day.ByApp.ToDictionary(p => p.Key, p => new SalesRow(p.Value.Units, p.Value.NetUsd, p.Value.GrossUsd));

                foreach (var (appId, name) in day.AppNames)
                {
                    ledger.AppNames[appId] = name;
                }

                ledger.PendingDates.Remove(text);

                if (++written % SaveEvery == 0)
                {
                    await store.SaveSalesAsync(ledger, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await store.SaveSalesAsync(ledger, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Bir app'in önbellekteki özeti; ağa çıkmıyor.</summary>
    public async Task<AppAnalytics> GetAsync(uint appId, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var history = await store.LoadPlayersAsync(appId, ct).ConfigureAwait(false);
        var wishlist = await store.LoadWishlistAsync(appId, ct).ConfigureAwait(false);
        var sales = await store.LoadSalesAsync(ct).ConfigureAwait(false);

        return new AppAnalytics(
            appId,
            sales.AppNames.GetValueOrDefault(appId),
            SummarizePlayers(history, now),
            SummarizeWishlist(wishlist, now),
            SummarizeSales(sales, now, appId));
    }

    /// <summary>Ortağın bütün app'lerinin satış toplamı; finans verisi yoksa null.</summary>
    public async Task<SalesStats?> GetPartnerSalesAsync(CancellationToken ct = default) =>
        SummarizeSales(await store.LoadSalesAsync(ct).ConfigureAwait(false), _time.GetUtcNow(), appId: null);

    public Task<AnalyticsState> GetStateAsync(CancellationToken ct = default) => store.LoadStateAsync(ct);

    private static PlayerStats SummarizePlayers(PlayerHistory history, DateTimeOffset now)
    {
        var since = (now - TimeSpan.FromHours(24)).ToUnixTimeSeconds();
        var day = history.Samples.Where(s => s.T >= since).OrderBy(s => s.T).ToList();
        var latest = history.Samples.MaxBy(s => s.T);

        return new PlayerStats(
            history.Available ? latest?.C : null,
            latest?.At,
            day.Count > 0 ? day.Max(s => s.C) : null,
            day,
            history.Available);
    }

    private static WishlistStats? SummarizeWishlist(WishlistLedger ledger, DateTimeOffset now)
    {
        if (ledger.Days.Count == 0)
        {
            return null;
        }

        var today = UtcDay(now);

        WishlistCounts On(DateOnly date) =>
            ledger.Days.TryGetValue(SteamWebApi.DateText(date), out var row)
                ? new WishlistCounts(row.A, row.D, row.P, row.G)
                : WishlistCounts.Zero;

        var last30 = Enumerable.Range(0, 30)
            .Select(i => today.AddDays(i - 29))
            .Select(date => new DailyValue(date, On(date).Net))
            .ToList();

        var complete = ledger.AppMinDate is { } first && !MissingWishlistDays(ledger, first, today).Any();

        return new WishlistStats(
            ledger.Days.Values.Sum(r => r.Net),
            On(today),
            On(today.AddDays(-1)),
            last30,
            complete,
            ledger.AppMinDate);
    }

    private static SalesStats? SummarizeSales(SalesLedger ledger, DateTimeOffset now, uint? appId)
    {
        if (ledger.LastCheckedAt is null)
        {
            return null;
        }

        var today = PacificDay(now);

        SalesTotals On(string date) =>
            ledger.Days.TryGetValue(date, out var apps)
                ? appId is { } id
                    ? apps.TryGetValue(id, out var row) ? row.Totals : SalesTotals.Zero
                    : apps.Values.Aggregate(SalesTotals.Zero, (sum, row) => sum.Add(row.Totals))
                : SalesTotals.Zero;

        SalesTotals Range(int days) => Enumerable.Range(0, days)
            .Select(i => On(SteamWebApi.DateText(today.AddDays(-i))))
            .Aggregate(SalesTotals.Zero, (sum, t) => sum.Add(t));

        var lifetime = ledger.Days.Keys.Select(On).Aggregate(SalesTotals.Zero, (sum, t) => sum.Add(t));

        var last30 = Enumerable.Range(0, 30)
            .Select(i => today.AddDays(i - 29))
            .Select(date => new DailyValue(date, On(SteamWebApi.DateText(date)).NetUsd))
            .ToList();

        return new SalesStats(
            On(SteamWebApi.DateText(today)),
            On(SteamWebApi.DateText(today.AddDays(-1))),
            Range(7),
            Range(30),
            lifetime,
            last30,
            ledger.PendingDates.Count == 0);
    }

    /// <summary>Wishlist günleri GMT.</summary>
    private static DateOnly UtcDay(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

    /// <summary>Satış günleri Pasifik saatiyle (Valve'ın verdiği ve beklediği tarih).</summary>
    internal static DateOnly PacificDay(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Pacific).DateTime);

    private static readonly TimeZoneInfo Pacific = FindPacific();

    private static TimeZoneInfo FindPacific()
    {
        foreach (var id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        // Saat dilimi verisi olmayan kurulumlarda yaz saatsiz yaklaşık değer.
        return TimeZoneInfo.CreateCustomTimeZone("Pacific", TimeSpan.FromHours(-8), "Pacific", "Pacific");
    }

    private sealed class BudgetExhaustedException() : Exception("Request budget used up for this refresh.");

    /// <summary>Bir yenilemedeki finans istekleri: sayı sınırı ve aralarında bekleme.</summary>
    private sealed class RequestBudget(int limit, TimeSpan spacing, TimeProvider time)
    {
        private int _used;
        private DateTimeOffset? _last;

        public async Task TakeAsync(CancellationToken ct)
        {
            if (_used >= limit)
            {
                throw new BudgetExhaustedException();
            }

            if (_last is { } last && spacing > TimeSpan.Zero)
            {
                var wait = spacing - (time.GetUtcNow() - last);

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, ct).ConfigureAwait(false);
                }
            }

            _used++;
            _last = time.GetUtcNow();
        }
    }
}
