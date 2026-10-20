using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.Core.Analytics;
using SteamPipeManager.Core.SteamCmd;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Presentation.Services;

namespace SteamPipeManager.Presentation.ViewModels;

/// <summary>Analitik sayılarının biçimi; sayfa ve widget aynı biçimi kullanıyor.</summary>
public static class AnalyticsFormat
{
    public static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Count(int? value) => value is { } v ? Count(v) : "—";

    /// <summary>Değişim: "+12", "−3", "0".</summary>
    public static string Signed(int value) =>
        value > 0 ? "+" + Count(value) : value < 0 ? "−" + Count(-value) : "0";

    /// <summary>Kısa sayı (widget ve dar alanlar için): 950, 12,3K, 1,2M.</summary>
    public static string Compact(int value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000d).ToString("0.#", CultureInfo.CurrentCulture) + "M",
        >= 10_000 => (value / 1_000d).ToString("0", CultureInfo.CurrentCulture) + "K",
        >= 1_000 => (value / 1_000d).ToString("0.#", CultureInfo.CurrentCulture) + "K",
        _ => value.ToString(CultureInfo.CurrentCulture),
    };

    /// <summary>Steam'in verdiği para birimi dolar; yerel biçimle ama dolar işaretiyle.</summary>
    public static string Usd(decimal value) => "$" + value.ToString("N2", CultureInfo.CurrentCulture);

    public static string Sales(SalesTotals totals) =>
        $"{Usd(totals.NetUsd)} · {AppLocalizer.Instance.Format("Analytics.Units", Count(totals.Units))}";

    /// <summary>
    /// Küçük çubuk grafik için 0..1 yükseklikler. <paramref name="fromZero"/>: en büyüğe
    /// oranla (satış, oyuncu); değilse en küçükten en büyüğe (birikimli wishlist, yoksa
    /// bütün çubuklar neredeyse aynı boyda görünürdü). Hiç değer yoksa boş.
    /// </summary>
    public static IReadOnlyList<double> Bars(IEnumerable<double> values, bool fromZero)
    {
        var list = values.ToList();

        if (list.Count == 0 || list.All(v => v == 0))
        {
            return [];
        }

        var max = list.Max();
        var min = fromZero ? 0 : list.Min();

        // Sıfır olmayan değer en az ince bir çizgi olarak görünsün.
        return max - min < double.Epsilon
            ? [.. list.Select(v => v == 0 ? 0 : 1.0)]
            : [.. list.Select(v => v == 0 && fromZero ? 0 : Math.Max(0.06, (v - min) / (max - min)))];
    }

    /// <summary>Son 24 saatin örnekleri yarım saatlik dilimlerde, dilimin en yükseği; boş dilim sıfır.</summary>
    public static IEnumerable<double> HalfHours(IEnumerable<PlayerSample> samples, DateTimeOffset now)
    {
        var start = now - TimeSpan.FromHours(24);
        var slots = new double[48];

        foreach (var sample in samples)
        {
            var index = (int)((sample.At - start).TotalMinutes / 30);

            if (index is >= 0 and < 48)
            {
                slots[index] = Math.Max(slots[index], sample.C);
            }
        }

        return slots;
    }
}

/// <summary>Analitik sayfasında bir oyunun kartı.</summary>
public sealed partial class AnalyticsCard(AnalyticsTarget target) : ObservableObject
{
    public AnalyticsTarget Target { get; } = target;

    public uint AppId => Target.AppId;

    public string Title => Target.Title;

    public string Subtitle => $"{Target.Kind} · {Target.AppId}";

    public string Initial => Title is { Length: > 0 } t ? t[..1].ToUpperInvariant() : "?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCapsule))]
    private string? _capsulePath;

    public bool HasCapsule => CapsulePath is { Length: > 0 };

    [ObservableProperty]
    private string _playersText = "—";

    [ObservableProperty]
    private string _playersDetail = "";

    [ObservableProperty]
    private IReadOnlyList<double> _playersSeries = [];

    /// <summary>Wishlist ya da satış verisi yoksa nedeni: anahtar yok ya da henüz gelmedi.</summary>
    [ObservableProperty]
    private string _financialPlaceholder = "";

    [ObservableProperty]
    private bool _hasWishlist;

    [ObservableProperty]
    private string _wishlistText = "";

    [ObservableProperty]
    private string _wishlistDetail = "";

    [ObservableProperty]
    private IReadOnlyList<double> _wishlistSeries = [];

    [ObservableProperty]
    private bool _hasSales;

    [ObservableProperty]
    private IReadOnlyList<SummaryRow> _salesRows = [];

    [ObservableProperty]
    private string _salesTodayText = "";

    [ObservableProperty]
    private string _salesTodayDetail = "";

    [ObservableProperty]
    private IReadOnlyList<double> _salesSeries = [];

    public void Apply(AppAnalytics data, bool hasKey)
    {
        var players = data.Players;

        FinancialPlaceholder = AppLocalizer.Instance.Get(hasKey ? "Analytics.NoDataYet" : "Analytics.NeedsKey");

        PlayersText = AnalyticsFormat.Count(players.Current);
        PlayersDetail = players switch
        {
            { Available: false } => AppLocalizer.Instance.Get("Analytics.NoPlayers"),
            { Peak24Hours: { } peak } => AppLocalizer.Instance.Format("Analytics.Peak", AnalyticsFormat.Count(peak)),
            _ => "",
        };
        // Tek örnekle grafik bir şey anlatmıyor; ikinci örnekten sonra çiziliyor.
        PlayersSeries = players.Last24Hours.Count >= 2
            ? AnalyticsFormat.Bars(AnalyticsFormat.HalfHours(players.Last24Hours, DateTimeOffset.UtcNow), fromZero: true)
            : [];

        HasWishlist = data.Wishlist is not null;

        if (data.Wishlist is { } wishlist)
        {
            WishlistText = AnalyticsFormat.Count(wishlist.Total);
            WishlistDetail = wishlist.IsComplete
                ? AppLocalizer.Instance.Format(
                    "Analytics.WishlistToday",
                    AnalyticsFormat.Signed(wishlist.Today.Net),
                    AnalyticsFormat.Signed(wishlist.Yesterday.Net))
                : AppLocalizer.Instance.Get("Analytics.WishlistIncomplete");
            WishlistSeries = AnalyticsFormat.Bars(Cumulative(wishlist), fromZero: false);
        }

        HasSales = data.Sales is not null;

        if (data.Sales is { } sales)
        {
            // Bugün büyük rakamla gösteriliyor; listede tekrar edilmiyor.
            SalesTodayText = AnalyticsFormat.Usd(sales.Today.NetUsd);
            SalesTodayDetail = AppLocalizer.Instance.Format("Analytics.Units", AnalyticsFormat.Count(sales.Today.Units));
            SalesRows =
            [
                new(AppLocalizer.Instance.Get("Analytics.Sales.Yesterday"), AnalyticsFormat.Sales(sales.Yesterday)),
                new(AppLocalizer.Instance.Get("Analytics.Sales.Week"), AnalyticsFormat.Sales(sales.Last7Days)),
                new(AppLocalizer.Instance.Get("Analytics.Sales.Month"), AnalyticsFormat.Sales(sales.Last30Days)),
                new(AppLocalizer.Instance.Get("Analytics.Sales.Lifetime"), AnalyticsFormat.Sales(sales.Lifetime)),
            ];
            SalesSeries = AnalyticsFormat.Bars(sales.Last30DaysNetUsd.Select(d => (double)d.Value), fromZero: true);
        }
    }

    /// <summary>Son 30 günün wishlist toplamı, gün gün: günlük farklardan daha okunur bir çizgi.</summary>
    private static IEnumerable<double> Cumulative(WishlistStats wishlist)
    {
        var running = wishlist.Total - (double)wishlist.Last30Days.Sum(d => d.Value);

        foreach (var day in wishlist.Last30Days)
        {
            running += (double)day.Value;
            yield return running;
        }
    }

    public override string ToString() => Title;
}

/// <summary>
/// Analitik sayfası: Steam profillerindeki her oyunun anlık oyuncu sayısı, wishlist ve
/// satışları. Veri arka plandaki <see cref="AnalyticsRefresher"/>'dan; bu sayfa yalnızca
/// önbelleği okuyor.
/// </summary>
public sealed partial class AnalyticsViewModel : ObservableObject
{
    private readonly AnalyticsService _service;
    private readonly AnalyticsRefresher _refresher;
    private readonly ProfileRepository _profiles;
    private readonly SteamImageService _images;
    private readonly SteamFinancialKeyStore _keys;

    public AnalyticsViewModel(
        AnalyticsService service,
        AnalyticsRefresher refresher,
        ProfileRepository profiles,
        SteamImageService images,
        SteamFinancialKeyStore keys)
    {
        _service = service;
        _refresher = refresher;
        _profiles = profiles;
        _images = images;
        _keys = keys;

        refresher.Updated += async (_, _) =>
        {
            if (IsActive)
            {
                await ReloadAsync();
            }
        };
    }

    public ObservableCollection<AnalyticsCard> Cards { get; } = [];

    public bool HasCards => Cards.Count > 0;

    /// <summary>Sayfa görünürken önbellek her turdan sonra yeniden okunuyor.</summary>
    public bool IsActive { get; set; }

    [ObservableProperty]
    private bool _hasKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    [ObservableProperty]
    private StatusTone _noticeTone;

    public bool HasNotice => Notice is { Length: > 0 };

    [ObservableProperty]
    private string _updatedText = "";

    public async Task LoadAsync()
    {
        Cards.Clear();

        foreach (var target in AnalyticsTargets.From(_profiles.Profiles))
        {
            Cards.Add(new AnalyticsCard(target) { CapsulePath = _images.GetCachedAppLibraryCapsule(target.AppId) });
        }

        OnPropertyChanged(nameof(HasCards));

        _ = RefreshCapsulesAsync();

        await ReloadAsync();
        _refresher.RefreshSoon();
    }

    /// <summary>Görseller: önce önbellek, sonra açılış başına bir kez Steam'e soruluyor.</summary>
    private async Task RefreshCapsulesAsync()
    {
        await Task.WhenAll(Cards.ToList().Select(async card =>
        {
            try
            {
                if (await _images.GetAppLibraryCapsuleAsync(card.AppId) is { } path)
                {
                    card.CapsulePath = path;
                }
            }
            catch
            {
                // Görsel olmadan da çalışılır.
            }
        }));
    }

    private async Task ReloadAsync()
    {
        HasKey = _keys.HasKey;

        foreach (var card in Cards.ToList())
        {
            card.Apply(await _service.GetAsync(card.AppId), HasKey);
        }

        var state = await _service.GetStateAsync();

        (Notice, NoticeTone) = (HasKey, state.FinancialFailure) switch
        {
            (false, _) => (AppLocalizer.Instance.Get("Analytics.NoKey"), StatusTone.Info),
            (true, AnalyticsFailure.InvalidKey) => (AppLocalizer.Instance.Get("Analytics.KeyRejected"), StatusTone.Danger),
            (true, AnalyticsFailure.RateLimited) => (AppLocalizer.Instance.Get("Analytics.RateLimited"), StatusTone.Warning),
            (true, { }) => (AppLocalizer.Instance.Get("Analytics.Offline"), StatusTone.Warning),
            (true, null) when state.FinancialBackfillPending => (AppLocalizer.Instance.Get("Analytics.Backfilling"), StatusTone.Info),
            _ => (null, StatusTone.Neutral),
        };

        UpdatedText = state.LastRefreshAt is { } at
            ? AppLocalizer.Instance.Format("Analytics.Updated", at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
            : "";
    }

    [RelayCommand]
    private void Refresh() => _refresher.RefreshSoon(force: true);
}
