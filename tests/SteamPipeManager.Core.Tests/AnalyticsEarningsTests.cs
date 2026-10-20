using System.Globalization;
using System.Net;
using System.Text;
using System.Web;
using SteamPipeManager.Core.Analytics;
using SteamPipeManager.Presentation.Charts;
using SteamPipeManager.Presentation.ViewModels;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Kazanç: dönem toplamları, ay sonu tahmini, kur çevirisi ve tutarların gizlenmesi.
/// Tutar biçimi (<see cref="AnalyticsFormat.HideMoney"/>) ortak durum; bu sınıfın testleri
/// sırayla çalışıyor, başka sınıf tutar metni denetlemiyor.
/// </summary>
public sealed class AnalyticsEarningsTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spm_rates_{Guid.NewGuid():N}");
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public AnalyticsEarningsTests() => CultureInfo.CurrentCulture = new CultureInfo("en-US");

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        AnalyticsFormat.HideMoney = false;

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static SalesHistory Sales(DateOnly today, params (int DaysAgo, SalesTotals Totals)[] days) =>
        new([.. days.OrderByDescending(d => d.DaysAgo).Select(d => new SalesDayTotals(today.AddDays(-d.DaysAgo), d.Totals))], new HashSet<DateOnly>(), today);

    [Fact]
    public void Earnings_cover_today_yesterday_the_month_and_a_month_end_estimate()
    {
        // Ekim 1–19 tamamlandı: her gün net $10 (pay $7); bugün (20'si) $4.
        var days = Enumerable.Range(1, 19)
            .Select(i => (i, new SalesTotals(1, 10m, 12m, 1m, 1m, 7m)))
            .Append((0, new SalesTotals(1, 4m, 5m, 0m, 1m, 2.8m)))
            .ToArray();

        var earnings = AnalyticsEarnings.Build(Sales(Today, days), AnalyticsRange.Month, rates: null);

        Assert.Equal(4m, earnings.Today.NetUsd);
        Assert.Equal(10m, earnings.Yesterday.NetUsd);
        Assert.Equal(194m, earnings.Month.NetUsd);

        // Bugün yarım: tahmin tamamlanmış 19 günden, 31 güne.
        Assert.Equal(310m, Math.Round(earnings.Projection!.NetUsd, 2));
        Assert.Equal(217m, Math.Round(earnings.Projection.ShareUsd, 2));

        Assert.Equal(30, earnings.RangeDays);
        Assert.Equal(19m, earnings.Range.ReturnsUsd);
        Assert.Equal(earnings.Range.NetUsd - earnings.Range.ShareUsd, earnings.Range.SteamCutUsd);
        Assert.Equal(4, earnings.Tiles.Count);
        Assert.Equal(6, earnings.Rows.Count);
        Assert.Equal("−$19.00", earnings.Rows[1].Value);
        Assert.Equal("", earnings.Tiles[0].Local);
    }

    [Fact]
    public void The_first_days_of_a_month_have_no_estimate()
    {
        var earnings = AnalyticsEarnings.Build(
            Sales(new DateOnly(2026, 11, 2), (1, new SalesTotals(1, 10m, 12m))), AnalyticsRange.Week, rates: null);

        Assert.Null(earnings.Projection);
        Assert.Equal("—", earnings.Tiles[3].Net);
    }

    [Fact]
    public void Local_amounts_use_each_days_own_rate()
    {
        var rates = new ExchangeRates("TRY", new Dictionary<DateOnly, decimal> { [Today.AddDays(-1)] = 40m, [Today] = 50m });
        var earnings = AnalyticsEarnings.Build(
            Sales(Today, (1, new SalesTotals(1, 10m, 12m, ShareUsd: 7m)), (0, new SalesTotals(1, 2m, 3m, ShareUsd: 1.4m))),
            AnalyticsRange.Week, rates);

        Assert.Equal("≈ ₺100", earnings.Tiles[0].Local);
        Assert.Equal("≈ ₺400", earnings.Tiles[1].Local);

        // Net 10 × 40 + 2 × 50; ödeme 7 × 40 + 1,4 × 50.
        Assert.Equal("≈ ₺500", earnings.Rows[3].Local);
        Assert.Equal("≈ ₺350", earnings.Rows[5].Local);
    }

    [Fact]
    public void A_missing_rate_takes_the_previous_days_but_not_from_weeks_ago()
    {
        var rates = new ExchangeRates("EUR", new Dictionary<DateOnly, decimal> { [Today.AddDays(-20)] = 0.9m });

        Assert.Equal(0.9m, rates.On(Today.AddDays(-10)));
        Assert.Null(rates.On(Today));
        Assert.Null(rates.Convert([(Today, 5m)]));
        Assert.Equal(0m, rates.Convert([(Today, 0m)]));
    }

    [Fact]
    public void Amounts_can_be_hidden_and_negatives_get_a_real_minus()
    {
        Assert.Equal("−$5.00", AnalyticsFormat.Usd(-5m));
        Assert.Equal("−$1.5K", AnalyticsFormat.CompactUsd(-1500m));
        Assert.Equal("≈ ₺1,235", AnalyticsFormat.Local(1234.6m, "TRY"));
        Assert.Equal("≈ SEK 12.50", AnalyticsFormat.Local(12.5m, "SEK"));

        AnalyticsFormat.HideMoney = true;

        Assert.Equal("$•••", AnalyticsFormat.Usd(5m));
        Assert.Equal("$•••", AnalyticsFormat.CompactUsd(1500m));
        Assert.Equal("≈ ₺•••", AnalyticsFormat.Local(100m, "TRY"));

        // Adetler gizlenmiyor.
        Assert.Equal("1,500", AnalyticsFormat.Count(1500));
    }

    // ---------------------------------------------------------------- kur servisi

    private sealed class FakeRates : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);

            if (Fail)
            {
                throw new HttpRequestException("offline");
            }

            var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
            var from = DateOnly.Parse(query["from"]!, CultureInfo.InvariantCulture);
            var to = DateOnly.Parse(query["to"]!, CultureInfo.InvariantCulture);
            var items = new List<string>();

            for (var d = from; d <= to; d = d.AddDays(1))
            {
                items.Add($$"""{"date":"{{d:yyyy-MM-dd}}","base":"USD","quote":"{{query["quotes"]}}","rate":{{30 + d.Day}}.5}""");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[" + string.Join(",", items) + "]", Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task Rates_are_fetched_once_kept_and_only_the_currency_and_dates_are_sent()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var fake = new FakeRates();
        var service = new ExchangeRateService(new AnalyticsStore(_dir), new ExchangeRateClient(new HttpClient(fake)), clock);

        var first = await service.GetAsync("try", Today.AddDays(-10));
        Assert.Equal(11, first!.Count);
        Assert.Equal(47.5m, first.On(Today.AddDays(-3)));

        // Önbellekte: yeni istek yok. Daha eskisi istenince yalnızca eksik kısım.
        await service.GetAsync("TRY", Today.AddDays(-5));
        Assert.Single(fake.Requests);

        await service.GetAsync("TRY", Today.AddDays(-20));
        Assert.Equal(2, fake.Requests.Count);
        Assert.EndsWith($"from={Today.AddDays(-20):yyyy-MM-dd}&to={Today.AddDays(-11):yyyy-MM-dd}", fake.Requests[1]);

        Assert.All(fake.Requests, r => Assert.Matches(@"^/v2/rates\?base=USD&quotes=TRY&from=\d{4}-\d\d-\d\d&to=\d{4}-\d\d-\d\d$", r));

        // Dolar için kur gerekmiyor.
        Assert.Null(await service.GetAsync("USD", Today));
        Assert.Equal(2, fake.Requests.Count);
    }

    [Fact]
    public async Task Without_a_connection_the_cached_rates_are_used_and_retries_wait()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var fake = new FakeRates();
        var service = new ExchangeRateService(new AnalyticsStore(_dir), new ExchangeRateClient(new HttpClient(fake)), clock);

        await service.GetAsync("EUR", Today.AddDays(-3));

        // Ertesi gün bağlantı yok: eldekiyle devam, hemen yeniden denenmiyor.
        clock.Advance(TimeSpan.FromDays(1));
        fake.Fail = true;

        var offline = await service.GetAsync("EUR", Today.AddDays(-3));
        Assert.Equal(50.5m, offline!.On(Today.AddDays(1)));
        Assert.Equal(2, fake.Requests.Count);

        await service.GetAsync("EUR", Today.AddDays(-3));
        Assert.Equal(2, fake.Requests.Count);

        clock.Advance(TimeSpan.FromMinutes(16));
        fake.Fail = false;
        var back = await service.GetAsync("EUR", Today.AddDays(-3));

        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal(51.5m, back!.On(Today.AddDays(1)));
    }
}
