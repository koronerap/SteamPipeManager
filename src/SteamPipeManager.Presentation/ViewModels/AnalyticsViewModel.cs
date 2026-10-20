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

    /// <summary>
    /// Ekran paylaşırken tutarlar gizli ("$•••"). Tercih (<see cref="AnalyticsPreferences.HideAmounts"/>)
    /// sayfa ve widget her yenilendiğinde buraya yazılıyor; grafikler de bunu okuyor.
    /// </summary>
    public static bool HideMoney { get; set; }

    private const string Hidden = "•••";

    /// <summary>Şerit için kısa tutar: $227, $1.2K, −$40.</summary>
    public static string CompactUsd(decimal value)
    {
        if (HideMoney)
        {
            return "$" + Hidden;
        }

        var rounded = (int)Math.Round(Math.Abs(value));
        return (value < 0 ? "−$" : "$") + (rounded < 1000 ? rounded.ToString(CultureInfo.CurrentCulture) : Compact(rounded));
    }

    /// <summary>Steam'in verdiği para birimi dolar; yerel biçimle ama dolar işaretiyle.</summary>
    public static string Usd(decimal value) =>
        HideMoney ? "$" + Hidden : (value < 0 ? "−$" : "$") + Math.Abs(value).ToString("N2", CultureInfo.CurrentCulture);

    /// <summary>Kullanıcının para birimindeki yaklaşık karşılık: "≈ ₺11,270"; yoksa boş.</summary>
    public static string Local(decimal? amount, string? currency)
    {
        if (amount is not { } value || currency is null)
        {
            return "";
        }

        var symbol = Currencies.SymbolOf(currency);

        if (HideMoney)
        {
            return "≈ " + symbol + Hidden;
        }

        var abs = Math.Abs(value);
        return "≈ " + (value < 0 ? "−" : "") + symbol + abs.ToString(abs >= 100 ? "N0" : "N2", CultureInfo.CurrentCulture);
    }

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

    /// <param name="rates">Kullanıcının para birimi; bugünkü satışın yaklaşık karşılığı için. Null: dolar.</param>
    public void Apply(AppAnalytics data, bool hasKey, ExchangeRates? rates = null)
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

            if (rates?.Latest is { } rate && sales.Today.NetUsd != 0)
            {
                SalesTodayDetail += " · " + AnalyticsFormat.Local(sales.Today.NetUsd * rate, rates.Currency);
            }
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
    private readonly AnalyticsServices _analytics;
    private readonly AnalyticsService _service;
    private readonly AnalyticsRefresher _refresher;
    private readonly ProfileRepository _profiles;
    private readonly SteamImageService _images;
    private readonly SteamFinancialKeyStore _keys;
    private readonly IWidgetLauncher? _widgetLauncher;

    /// <param name="widgetLauncher">Widget'ın içinde gösterilirken null: düğme gizleniyor.</param>
    public AnalyticsViewModel(
        AnalyticsServices analytics,
        AnalyticsRefresher refresher,
        ProfileRepository profiles,
        SteamImageService images,
        IWidgetLauncher? widgetLauncher = null)
    {
        _widgetLauncher = widgetLauncher;
        _analytics = analytics;
        _service = analytics.Service;
        _refresher = refresher;
        _profiles = profiles;
        _images = images;
        _keys = analytics.Keys;

        refresher.Updated += async (_, _) =>
        {
            if (IsActive)
            {
                await ReloadAsync();

                if (Detail is { } detail)
                {
                    await detail.LoadAsync();
                }
            }
        };
    }

    public ObservableCollection<AnalyticsCard> Cards { get; } = [];

    /// <summary>Açık olan oyun ayrıntısı; null ise kartların listesi görünüyor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview))]
    private AnalyticsDetailViewModel? _detail;

    public bool IsOverview => Detail is null;

    private AnalyticsPlan? _plan;

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

    /// <summary>Ekran paylaşırken tutarlar gizli; tercih kalıcı ve widget'la ortak.</summary>
    [ObservableProperty]
    private bool _hideAmounts;

    public async Task LoadAsync()
    {
        Detail = null;
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
        // Anahtarlar profil başına; ayarlarda değişmiş olabilir, her seferinde yeniden.
        var plan = AnalyticsPlan.Build(AnalyticsTargets.From(_profiles.Profiles), _keys.Read);
        _plan = plan;
        HasKey = plan.Scope.Accounts.Count > 0;

        // Tercihler ayarlarda ya da widget'ta değişmiş olabilir.
        var preferences = await _analytics.Preferences.LoadAsync();
        HideAmounts = AnalyticsFormat.HideMoney = preferences.HideAmounts;

        // Kartlarda bugünkü satışın karşılığı en son kurla; kur yoksa yalnızca dolar.
        var rates = HasKey ? await _analytics.Rates.GetAsync(preferences.EffectiveCurrency, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-7)) : null;

        foreach (var card in Cards.ToList())
        {
            var account = plan.AccountOfApp.GetValueOrDefault(card.AppId);
            card.Apply(await _service.GetAsync(card.AppId, account), hasKey: account is not null, rates);
        }

        var state = await _service.GetStateAsync();

        var failures = state.Accounts
            .Where(a => a.Value.Failure is not null && plan.ProfilesOfAccount.ContainsKey(a.Key))
            .ToList();

        var rejected = failures
            .Where(a => a.Value.Failure == AnalyticsFailure.InvalidKey)
            .SelectMany(a => plan.ProfilesOfAccount[a.Key])
            .ToList();

        (Notice, NoticeTone) = true switch
        {
            _ when !HasKey => (AppLocalizer.Instance.Get("Analytics.NoKey"), StatusTone.Info),
            _ when rejected.Count > 0 => (AppLocalizer.Instance.Format("Analytics.KeyRejected", string.Join(", ", rejected)), StatusTone.Danger),
            _ when failures.Any(a => a.Value.Failure == AnalyticsFailure.RateLimited) => (AppLocalizer.Instance.Get("Analytics.RateLimited"), StatusTone.Warning),
            _ when failures.Count > 0 => (AppLocalizer.Instance.Get("Analytics.Offline"), StatusTone.Warning),
            _ when state.FinancialBackfillPending => (AppLocalizer.Instance.Get("Analytics.Backfilling"), StatusTone.Info),
            _ => (null, StatusTone.Neutral),
        };

        UpdatedText = state.LastRefreshAt is { } at
            ? AppLocalizer.Instance.Format("Analytics.Updated", at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
            : "";
    }

    [RelayCommand]
    private void Refresh() => _refresher.RefreshSoon(force: true);

    [RelayCommand]
    private async Task OpenDetailsAsync(AnalyticsCard card)
    {
        var account = _plan?.AccountOfApp.GetValueOrDefault(card.AppId);
        var detail = new AnalyticsDetailViewModel(card, _analytics, account, _analytics.Preferences.Current.EffectiveCurrency, () => Detail = null);
        Detail = detail;
        await detail.LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleHideAmountsAsync()
    {
        _analytics.Preferences.Current.HideAmounts = !HideAmounts;
        await _analytics.Preferences.SaveAsync();
        await ReloadAsync();
        Detail?.Refresh();
    }

    public bool CanOpenWidget => _widgetLauncher is not null;

    [RelayCommand]
    private void OpenWidget()
    {
        if (_widgetLauncher?.Launch() == false)
        {
            (Notice, NoticeTone) = (AppLocalizer.Instance.Get("Analytics.WidgetMissing"), StatusTone.Warning);
        }
    }
}
