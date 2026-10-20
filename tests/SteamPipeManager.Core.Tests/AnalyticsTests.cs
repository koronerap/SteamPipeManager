using System.Net;
using System.Text;
using System.Web;
using SteamPipeManager.Core.Analytics;
using SteamPipeManager.Core.Epic;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Analitik: anlık oyuncu sayısı (anahtarsız), wishlist ve satışlar (finans anahtarı).
/// Steam'in yerine sahte bir sunucu; yanıt biçimleri Valve'ın belgelerindeki örneklerden.
/// </summary>
public sealed class AnalyticsTests : IDisposable
{
    private const string Key = "FINANCIAL-KEY-123";
    private const uint Game = 4000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spm_analytics_{Guid.NewGuid():N}");
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeSteamApi _steam = new() { ValidKey = Key };

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private DateOnly Today => DateOnly.FromDateTime(_clock.Now.UtcDateTime);

    /// <summary>Tek oyunlu kurulum: <paramref name="key"/> o oyunun profilinin anahtarı.</summary>
    private Harness Service(string? key = Key, int budget = 1000)
    {
        var http = new HttpClient(_steam);

        var service = new AnalyticsService(
            new AnalyticsStore(_dir),
            new SteamPlayerCountClient(http),
            new SteamFinancialClient(http),
            _clock)
        {
            FinancialRequestBudget = budget,
            FinancialRequestSpacing = TimeSpan.Zero,
        };

        return new Harness(service, new AnalyticsScope([Game], key is null ? [] : [new FinancialAccount(key, [Game])]));
    }

    /// <summary>Servis ve kapsamı birlikte; testler oyunun hangi hesapta olduğunu tekrar yazmasın.</summary>
    private sealed class Harness(AnalyticsService service, AnalyticsScope scope)
    {
        public Task<RefreshResult> RefreshAsync(bool force = false) => service.RefreshAsync(scope, force);

        public Task<AppAnalytics> GetAsync(uint appId) =>
            service.GetAsync(appId, scope.Accounts.FirstOrDefault(a => a.AppIds.Contains(appId))?.Id);

        public Task<SalesStats?> GetPartnerSalesAsync() => service.GetPartnerSalesAsync(scope.Accounts[0].Id);

        public Task<AnalyticsState> GetStateAsync() => service.GetStateAsync();
    }

    // ---------------------------------------------------------------- oyuncu sayısı

    [Fact]
    public async Task Current_players_are_read_and_an_app_without_data_has_none()
    {
        _steam.Players[730] = 923774;
        var client = new SteamPlayerCountClient(new HttpClient(_steam));

        Assert.Equal(923774, await client.GetCurrentPlayersAsync(730));
        Assert.Null(await client.GetCurrentPlayersAsync(999999990));
    }

    [Fact]
    public async Task Players_are_sampled_once_per_interval_and_the_peak_comes_from_the_samples()
    {
        var service = Service(key: null);

        foreach (var count in new[] { 100, 250, 180 })
        {
            _steam.Players[Game] = count;
            await service.RefreshAsync();

            // Aralık dolmadan yeniden sorulmuyor.
            await service.RefreshAsync();
            _clock.Advance(TimeSpan.FromMinutes(5));
        }

        var players = (await service.GetAsync(Game)).Players;

        Assert.Equal(3, _steam.Calls("GetNumberOfCurrentPlayers"));
        Assert.Equal(180, players.Current);
        Assert.Equal(250, players.Peak24Hours);
        Assert.Equal(3, players.Last24Hours.Count);
    }

    [Fact]
    public async Task Player_samples_older_than_thirty_days_become_hourly_summaries()
    {
        var service = Service(key: null);
        _steam.Players[Game] = 10;

        await service.RefreshAsync();
        _clock.Advance(TimeSpan.FromDays(31));
        await service.RefreshAsync();

        var history = await new AnalyticsStore(_dir).LoadPlayersAsync(Game);

        Assert.Single(history.Samples);
        Assert.Equal(new PlayerHour(_clock.Now.AddDays(-31).ToUnixTimeSeconds() / 3600 * 3600, 10, 10, 1), Assert.Single(history.Hours));
    }

    /// <summary>
    /// Saatin ortası sınıra düşebiliyor: o saatin ilk yarısı bir turda, kalanı sonrakinde
    /// özetleniyor; ikisi tek satırda, ağırlıklı ortalamayla birleşiyor.
    /// </summary>
    [Fact]
    public void A_partly_summarised_hour_is_merged_and_very_old_hours_are_dropped()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var hour = now.AddDays(-31).ToUnixTimeSeconds() / 3600 * 3600;

        var history = new PlayerHistory
        {
            Hours = [new PlayerHour(hour, 50, 40, 2), new PlayerHour(now.AddDays(-800).ToUnixTimeSeconds(), 1, 1, 1)],
            Samples = [new PlayerSample(hour + 1800, 70), new PlayerSample(hour + 2400, 10), new PlayerSample(now.ToUnixTimeSeconds(), 5)],
        };

        AnalyticsService.RollUp(history, now);

        // (40 × 2 + 70 + 10) / 4
        Assert.Equal(new PlayerHour(hour, 70, 40, 4), Assert.Single(history.Hours));
        Assert.Equal(5, Assert.Single(history.Samples).C);
    }

    [Fact]
    public async Task An_unreleased_app_has_no_current_players_but_is_not_an_error()
    {
        var service = Service(key: null);

        await service.RefreshAsync();
        var players = (await service.GetAsync(Game)).Players;

        Assert.False(players.Available);
        Assert.Null(players.Current);
    }

    // ---------------------------------------------------------------- wishlist

    /// <summary>On günlük wishlist geçmişi: her gün 10 ekleme, 2 silme, 1 satın alma, 1 hediye.</summary>
    private void TenDaysOfWishlists()
    {
        _steam.AppMinDate[Game] = Today.AddDays(-9);

        for (var i = 0; i < 10; i++)
        {
            _steam.Wishlists[(Game, Today.AddDays(-i))] = (10, 2, 1, 1);
        }
    }

    [Fact]
    public async Task The_wishlist_total_is_summed_from_the_first_day()
    {
        TenDaysOfWishlists();

        var result = await Service().RefreshAsync();
        var wishlist = (await Service().GetAsync(Game)).Wishlist!;

        Assert.Equal(RefreshResult.Done, result);
        Assert.True(wishlist.IsComplete);
        Assert.Equal(10 * (10 - 2 - 1 - 1), wishlist.Total);
        Assert.Equal(new WishlistCounts(10, 2, 1, 1), wishlist.Today);
        Assert.Equal(30, wishlist.Last30Days.Count);
        Assert.Equal(Today.AddDays(-9), wishlist.Since);
    }

    /// <summary>
    /// İlk indirme her yenilemede sınırlı sayıda istekle ilerliyor; bitene kadar "devam"
    /// diyor, sonunda toplam yine doğru ve hiçbir gün iki kez indirilmemiş.
    /// </summary>
    [Fact]
    public async Task The_first_download_is_spread_over_refreshes_and_resumes()
    {
        TenDaysOfWishlists();
        var results = new List<RefreshResult>();

        do
        {
            // Satış için bir de değişen günler isteği var.
            results.Add(await Service(budget: 4).RefreshAsync());
        }
        while (results[^1] != RefreshResult.Done && results.Count < 10);

        Assert.Equal(RefreshResult.MorePending, results[0]);
        Assert.Equal(RefreshResult.Done, results[^1]);
        Assert.Equal(10 * (10 - 2 - 1 - 1), (await Service().GetAsync(Game)).Wishlist!.Total);

        // Bugün + dün bir kez, kalan sekiz gün birer kez.
        Assert.Equal(10, _steam.Calls("GetAppWishlistReporting"));
    }

    /// <summary>Gün bitmeden okunan satır eksik; günler geçince bir kez daha indiriliyor.</summary>
    [Fact]
    public async Task A_day_read_while_in_progress_is_downloaded_again_later()
    {
        _steam.AppMinDate[Game] = Today;
        _steam.Wishlists[(Game, Today)] = (5, 0, 0, 0);
        var day = Today;

        await Service().RefreshAsync();

        // Gün bitti; o günün son hali daha yüksek.
        _steam.Wishlists[(Game, day)] = (12, 1, 0, 0);
        _clock.Advance(TimeSpan.FromDays(2));
        await Service().RefreshAsync();

        Assert.Equal(11, (await Service().GetAsync(Game)).Wishlist!.Total);
    }

    // ---------------------------------------------------------------- satışlar

    private DateOnly PacificToday => AnalyticsService.PacificDay(_clock.Now);

    private void SomeSales()
    {
        var today = PacificToday;
        var yesterday = today.AddDays(-1);

        _steam.Changes.Add((100, yesterday));
        _steam.Changes.Add((101, today));

        // Paket satırı ana oyuna (primary_appid), oyun içi satış kendi appid'sine;
        // yalnızca etkinleştirme olan satır satış sayılmıyor. Sayfa boyu 2: üç sayfa.
        _steam.Sales[yesterday] =
        [
            """{"line_item_type":"Package","packageid":1,"primary_appid":4000,"gross_units_sold":10,"gross_units_returned":-2,"net_units_sold":8,"gross_sales_usd":"100.0000","net_sales_usd":"72.5000"}""",
            """{"line_item_type":"MicroTxn","appid":4001,"primary_appid":0,"gross_units_sold":3,"net_units_sold":3,"gross_sales_usd":"6.0000","net_sales_usd":"5.0000"}""",
            """{"line_item_type":"Package","packageid":2,"primary_appid":4000,"gross_units_activated":50,"gross_sales_usd":"0.0000","net_sales_usd":"0.0000"}""",
        ];

        _steam.Sales[today] =
        [
            """{"line_item_type":"Package","packageid":1,"primary_appid":"4000","gross_units_sold":"2","net_units_sold":"2","gross_sales_usd":"20.0000","net_sales_usd":"14.2500"}""",
        ];
    }

    [Fact]
    public async Task Sales_are_totalled_per_app_across_pages()
    {
        SomeSales();

        await Service().RefreshAsync();
        var sales = (await Service().GetAsync(Game)).Sales!;

        Assert.Equal(new SalesTotals(2, 14.25m, 20m), sales.Today);
        Assert.Equal(new SalesTotals(8, 72.5m, 100m), sales.Yesterday);
        Assert.Equal(new SalesTotals(10, 86.75m, 120m), sales.Lifetime);
        Assert.True(sales.IsComplete);

        // Ortak toplam oyun içi satışı da içeriyor.
        Assert.Equal(new SalesTotals(13, 91.75m, 126m), (await Service().GetPartnerSalesAsync())!.Lifetime);
    }

    /// <summary>
    /// Değiştiği bildirilen gün baştan okunup yerine yazılıyor (Valve: "re-query the
    /// entirety of that date"); üstüne eklenmiyor. Sonraki sorgu saklanan sınırla yapılıyor.
    /// </summary>
    [Fact]
    public async Task A_changed_day_replaces_its_sales_and_the_highwatermark_is_kept()
    {
        SomeSales();
        await Service().RefreshAsync();

        var yesterday = PacificToday.AddDays(-1);
        _steam.Sales[yesterday] =
        [
            """{"line_item_type":"Package","packageid":1,"primary_appid":4000,"gross_units_sold":10,"gross_units_returned":-3,"net_units_sold":7,"gross_sales_usd":"100.0000","net_sales_usd":"63.0000"}""",
        ];
        _steam.Changes.Add((102, yesterday));

        _clock.Advance(TimeSpan.FromHours(3));
        await Service().RefreshAsync();

        Assert.Equal(new SalesTotals(7, 63m, 100m), (await Service().GetAsync(Game)).Sales!.Yesterday);
        Assert.Equal(["0", "101"], _steam.HighwatermarksSent);
    }

    [Fact]
    public async Task Without_a_key_there_is_no_financial_data_and_no_financial_request()
    {
        SomeSales();
        TenDaysOfWishlists();

        await Service(key: null).RefreshAsync();
        var analytics = await Service(key: null).GetAsync(Game);

        Assert.Null(analytics.Wishlist);
        Assert.Null(analytics.Sales);
        Assert.Equal(0, _steam.Calls("IPartnerFinancialsService"));
    }

    /// <summary>
    /// Reddedilen anahtar durum olarak saklanıyor ve oyuncu sayısını engellemiyor. Hata
    /// metninde anahtar yok: adreste gidiyor ve hatalar arayüzde görünüyor.
    /// </summary>
    [Fact]
    public async Task A_rejected_key_is_reported_without_leaking_it_and_players_still_update()
    {
        _steam.Players[Game] = 42;
        var service = Service(key: "WRONG-KEY-999");

        await service.RefreshAsync();

        var state = await service.GetStateAsync();
        Assert.Equal(AnalyticsFailure.InvalidKey, Assert.Single(state.Accounts).Value.Failure);
        Assert.Equal(42, (await service.GetAsync(Game)).Players.Current);

        var ex = await Assert.ThrowsAsync<AnalyticsException>(() =>
            new SteamFinancialClient(new HttpClient(_steam)).GetChangedDatesAsync("WRONG-KEY-999", 0));
        Assert.DoesNotContain("WRONG-KEY-999", ex.Message);
        Assert.DoesNotContain("WRONG-KEY-999", ex.ToString());
    }

    /// <summary>Ana uygulama ve widget aynı anda yenilemiyor; ikincisi önbelleği okuyor.</summary>
    [Fact]
    public async Task Only_one_process_refreshes_at_a_time()
    {
        using var held = new AnalyticsStore(_dir).TryLock();

        Assert.NotNull(held);
        Assert.Equal(RefreshResult.Busy, await Service().RefreshAsync());
        Assert.Equal(0, _steam.Calls(""));
    }

    // ---------------------------------------------------------------- anahtar deposu

    [Fact]
    public void Each_profile_has_its_own_key_stored_trimmed()
    {
        var keys = new SteamFinancialKeyStore("unused", new MemoryBackend());
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());

        keys.Write(first, "  ABC123\n");
        keys.Write(second, "XYZ");

        Assert.Equal("ABC123", keys.Read(first));
        Assert.Equal("XYZ", keys.Read(second));

        keys.Write(first, "");
        Assert.False(keys.Has(first));
        Assert.True(keys.Has(second));
    }

    /// <summary>İlk sürümün profile bağlı olmayan anahtarı, sahibi olan profile taşınabiliyor.</summary>
    [Fact]
    public void The_key_from_the_first_version_moves_to_the_profile_it_belongs_to()
    {
        var backend = new MemoryBackend();
        backend.Write("steam-financial-api-key", "OLD-KEY");
        var keys = new SteamFinancialKeyStore("unused", backend);
        var profile = Guid.NewGuid();

        Assert.True(keys.HasLegacy);
        Assert.False(keys.Has(profile));

        keys.MoveLegacyTo(profile);

        Assert.Equal("OLD-KEY", keys.Read(profile));
        Assert.False(keys.HasLegacy);
    }

    /// <summary>
    /// İki şirketin anahtarı: biri reddedilince yalnızca o hesap hatalı, diğerinin satışları
    /// yine geliyor. Tek anahtarlı sürümde reddedilen anahtar her şeyi durduruyordu.
    /// </summary>
    [Fact]
    public async Task A_rejected_key_only_affects_its_own_profiles_games()
    {
        SomeSales();
        var http = new HttpClient(_steam);
        var service = new AnalyticsService(new AnalyticsStore(_dir), new SteamPlayerCountClient(http), new SteamFinancialClient(http), _clock)
        {
            FinancialRequestSpacing = TimeSpan.Zero,
        };

        var good = new FinancialAccount(Key, [Game]);
        var bad = new FinancialAccount("OTHER-COMPANY-KEY", [5000]);

        await service.RefreshAsync(new AnalyticsScope([Game, 5000], [bad, good]));

        var state = await service.GetStateAsync();
        Assert.Equal(AnalyticsFailure.InvalidKey, state.Accounts[bad.Id].Failure);
        Assert.Null(state.Accounts[good.Id].Failure);
        Assert.Equal(new SalesTotals(2, 14.25m, 20m), (await service.GetAsync(Game, good.Id)).Sales!.Today);

        // Satışlar anahtar başına ayrı dosyada; anahtarın kendisi dosya adında yok.
        Assert.True(File.Exists(Path.Combine(_dir, "sales", $"{good.Id}.json")));
        Assert.DoesNotContain(Key, string.Join(" ", Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)));
    }

    /// <summary>Anahtar Zinciri'nde Epic secret'larından ayrı, kendi adıyla duruyor.</summary>
    [Fact]
    public void In_the_keychain_the_key_has_its_own_name_apart_from_epic_secrets()
    {
        var runner = new CommandLog();
        new MacKeychainBackend(runner, SteamFinancialKeyStore.Scope).Write("steam-financial-api-key", "k");

        var (_, arguments) = Assert.Single(runner.Calls);
        Assert.Contains("SteamPipeManager.Steam.FinancialApiKey", arguments);
        Assert.DoesNotContain(MacKeychainBackend.Service, arguments);
        Assert.Contains("Steam Pipe Manager — Steam Financial API", arguments);
    }

    [Fact]
    public void On_linux_the_key_is_stored_under_its_own_attribute()
    {
        var runner = new CommandLog();
        new LinuxSecretServiceBackend(runner, SteamFinancialKeyStore.Scope).Write("steam-financial-api-key", "k");

        var (_, arguments) = Assert.Single(runner.Calls);
        Assert.Contains("financial", arguments);
        Assert.DoesNotContain("profile", arguments);
    }
}

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;

    public void Advance(TimeSpan by) => Now += by;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class MemoryBackend : ISecretBackend
{
    private readonly Dictionary<string, string> _items = [];

    public SecretStorageKind Kind => SecretStorageKind.WindowsDpapi;

    public bool Has(string key) => _items.ContainsKey(key);

    public string? Read(string key) => _items.GetValueOrDefault(key);

    public void Write(string key, string secret) => _items[key] = secret;

    public void Delete(string key) => _items.Remove(key);
}

file sealed class CommandLog : ICommandRunner
{
    public List<(string Program, IReadOnlyList<string> Arguments)> Calls { get; } = [];

    public CommandResult Run(string program, IReadOnlyList<string> arguments, string? standardInput = null)
    {
        Calls.Add((program, arguments));
        return new CommandResult(0, "");
    }
}

/// <summary>
/// Steam'in oyuncu sayısı ve finans uçlarının taklidi. Finans uçları yanlış anahtarda
/// gerçeği gibi HTTP 403 ve HTML döndürüyor.
/// </summary>
internal sealed class FakeSteamApi : HttpMessageHandler
{
    public required string ValidKey { get; init; }

    public Dictionary<uint, int> Players { get; } = [];

    public Dictionary<uint, DateOnly> AppMinDate { get; } = [];

    public Dictionary<(uint App, DateOnly Date), (int Adds, int Deletes, int Purchases, int Gifts)> Wishlists { get; } = [];

    /// <summary>Değişiklik günlüğü: sınır değeri ve değişen gün.</summary>
    public List<(ulong Mark, DateOnly Date)> Changes { get; } = [];

    public Dictionary<DateOnly, string[]> Sales { get; } = [];

    public int PageSize { get; init; } = 2;

    public List<string> Requests { get; } = [];

    public List<string> HighwatermarksSent { get; } = [];

    public int Calls(string fragment) => Requests.Count(r => r.Contains(fragment, StringComparison.Ordinal));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        Requests.Add(uri.AbsolutePath);
        var query = HttpUtility.ParseQueryString(uri.Query);

        if (uri.AbsolutePath.Contains("GetNumberOfCurrentPlayers"))
        {
            return Task.FromResult(Players.TryGetValue(uint.Parse(query["appid"]!), out var count)
                ? Json($$$"""{"response":{"player_count":{{{count}}},"result":1}}""")
                : Json("""{"response":{"result":42}}""", HttpStatusCode.NotFound));
        }

        if (query["key"] != ValidKey)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html><body>Access is denied. Please verify your <pre>key=</pre> parameter.</body></html>"),
            });
        }

        if (uri.AbsolutePath.Contains("GetAppWishlistReporting"))
        {
            var app = uint.Parse(query["appid"]!);
            var date = DateOnly.Parse(query["date"]!);
            var (a, d, p, g) = Wishlists.GetValueOrDefault((app, date));
            var min = AppMinDate.TryGetValue(app, out var m) ? $"\"{m:yyyy-MM-dd}\"" : "null";

            return Task.FromResult(Json(
                $$$"""{"response":{"appid":{{{app}}},"date":"{{{date:yyyy-MM-dd}}}","wishlist_summary":{"wishlist_adds":{{{a}}},"wishlist_deletes":{{{d}}},"wishlist_purchases":{{{p}}},"wishlist_gifts":{{{g}}}},"app_min_date":{{{min}}}}}"""));
        }

        if (uri.AbsolutePath.Contains("GetChangedDatesForPartner"))
        {
            var since = ulong.Parse(query["highwatermark"]!);
            HighwatermarksSent.Add(query["highwatermark"]!);

            var changed = Changes.Where(c => c.Mark > since).ToList();
            var dates = string.Join(",", changed.Select(c => $"\"{c.Date:yyyy/MM/dd}\"").Distinct());
            var next = changed.Count > 0 ? changed.Max(c => c.Mark) : since;

            return Task.FromResult(Json($$$"""{"response":{"dates":[{{{dates}}}],"result_highwatermark":"{{{next}}}"}}"""));
        }

        if (uri.AbsolutePath.Contains("GetDetailedSales"))
        {
            var date = DateOnly.Parse(query["date"]!);
            var after = ulong.Parse(query["highwatermark_id"]!);
            var rows = Sales.GetValueOrDefault(date) ?? [];

            // Satır kimlikleri 1'den başlıyor; sınırdan sonrakiler, sayfa boyu kadar.
            var page = rows.Select((row, i) => (Id: (ulong)i + 1, Row: row))
                .Where(r => r.Id > after)
                .Take(PageSize)
                .ToList();

            var maxId = page.Count > 0 ? page[^1].Id : after;
            var results = string.Join(",", page.Select(r => r.Row));

            return Task.FromResult(Json(
                $$$"""{"response":{"results":[{{{results}}}],"app_info":[{"appid":4000,"app_name":"Test Game"}],"max_id":"{{{maxId}}}"}}"""));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
