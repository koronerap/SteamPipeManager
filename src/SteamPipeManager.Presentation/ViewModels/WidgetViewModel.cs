using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using SteamPipeManager.Core.Analytics;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Presentation.Services;

namespace SteamPipeManager.Presentation.ViewModels;

/// <summary>Widget'ın ayarları (<c>widget.json</c>).</summary>
public sealed class WidgetSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Şeritte gösterilen oyunlar. Null: ana oyunlardan ilk üçü.</summary>
    public List<uint>? PinnedAppIds { get; set; }

    public bool ShowPlayers { get; set; } = true;

    public bool ShowWishlists { get; set; } = true;

    public bool ShowSales { get; set; } = true;

    public bool StripVisible { get; set; } = true;

    /// <summary>
    /// Şeridin sağ alt köşesi (fiziksel piksel); yoksa görev çubuğunun üstü, sağda. Köşe
    /// tutuluyor, sol üst değil: içerik uzayınca şerit sola doğru büyüsün.
    /// </summary>
    public int? StripRight { get; set; }

    public int? StripBottom { get; set; }
}

public enum WidgetMetric
{
    Players,
    Wishlists,
    Sales,
}

/// <summary>Şeritte bir oyun: kısa ad ve seçilen sayılar.</summary>
public sealed partial class WidgetItem(AnalyticsTarget target) : ObservableObject
{
    public AnalyticsTarget Target { get; } = target;

    public uint AppId => Target.AppId;

    /// <summary>Şeride sığsın diye kısaltılmış ad; tam adı ipucunda.</summary>
    public string ShortTitle => Target.Title.Length <= 18 ? Target.Title : Target.Title[..17].TrimEnd() + "…";

    [ObservableProperty]
    private bool _showPlayers;

    [ObservableProperty]
    private string _playersText = "—";

    [ObservableProperty]
    private bool _showWishlist;

    [ObservableProperty]
    private string _wishlistText = "";

    /// <summary>Bugünkü wishlist değişimi; değişim yoksa boş.</summary>
    [ObservableProperty]
    private string _wishlistDelta = "";

    [ObservableProperty]
    private StatusTone _wishlistDeltaTone = StatusTone.Muted;

    [ObservableProperty]
    private bool _showSales;

    [ObservableProperty]
    private string _salesText = "";

    /// <summary>İpucunda ve tepsi simgesinde kullanılan tek satırlık özet.</summary>
    [ObservableProperty]
    private string _summary = "";

    public void Apply(AppAnalytics data, WidgetSettings settings)
    {
        ShowPlayers = settings.ShowPlayers;
        PlayersText = data.Players.Current is { } players ? AnalyticsFormat.Compact(players) : "—";

        ShowWishlist = settings.ShowWishlists && data.Wishlist is not null;

        if (data.Wishlist is { } wishlist)
        {
            WishlistText = AnalyticsFormat.Compact(wishlist.Total);
            WishlistDelta = wishlist.Today.Net == 0 ? "" : AnalyticsFormat.Signed(wishlist.Today.Net);
            WishlistDeltaTone = wishlist.Today.Net >= 0 ? StatusTone.Success : StatusTone.Danger;
        }

        ShowSales = settings.ShowSales && data.Sales is not null;

        if (data.Sales is { } sales)
        {
            SalesText = AnalyticsFormat.CompactUsd(sales.Today.NetUsd);
        }

        var parts = new List<string>();

        if (ShowPlayers)
        {
            parts.Add(AppLocalizer.Instance.Format("Widget.Summary.Players", PlayersText));
        }

        if (ShowWishlist)
        {
            parts.Add(AppLocalizer.Instance.Format("Widget.Summary.Wishlists", WishlistText) +
                      (WishlistDelta.Length > 0 ? $" ({WishlistDelta})" : ""));
        }

        if (ShowSales)
        {
            parts.Add(AppLocalizer.Instance.Format("Widget.Summary.Sales", SalesText));
        }

        Summary = $"{Target.Title}: {string.Join(" · ", parts)}";
    }

    public override string ToString() => Target.Title;
}

/// <summary>
/// Widget: her zaman üstte duran şerit, tepsi simgesi ve ayrıntı penceresi. Veri ana
/// uygulamayla ortak önbellekten; ikisi aynı anda açıksa yalnızca biri Steam'e soruyor.
///
/// Profiller diskten okunuyor ve her turda yeniden okunuyor: oyunlar ana uygulamada
/// eklenip çıkarılıyor, widget bunu açık kalırken de görmeli.
/// </summary>
public sealed partial class WidgetViewModel(
    AnalyticsService service,
    ProfileRepository profiles,
    SteamFinancialKeyStore keys,
    WorkspaceLayout layout)
{
    /// <summary>Şeritte en fazla bu kadar oyun varsayılan olarak gösteriliyor.</summary>
    public const int DefaultPinnedCount = 3;

    private readonly JsonFileStore<WidgetSettings> _store = new(layout.WidgetSettingsFile, () => new WidgetSettings());

    public ObservableCollection<WidgetItem> Items { get; } = [];

    public IReadOnlyList<AnalyticsTarget> Targets { get; private set; } = [];

    /// <summary>Yenileme planı: hangi anahtar hangi oyunları sorguluyor (profil başına anahtar).</summary>
    public AnalyticsPlan Plan { get; private set; } = AnalyticsPlan.Build([], _ => null);

    public WidgetSettings Settings { get; private set; } = new();

    public string Tooltip { get; private set; } = "";

    /// <summary>Şerit, öğeler değişince boyutunu yeniden hesaplamalı.</summary>
    public event EventHandler? Changed;

    public bool HasItems => Items.Count > 0;

    public async Task LoadAsync()
    {
        try
        {
            Settings = await _store.LoadAsync();
        }
        catch (InvalidDataException)
        {
            Settings = new WidgetSettings();
        }

        await ReloadAsync();
    }

    /// <summary>Profilleri ve önbelleği yeniden okur; arka plandaki her yenileme turundan sonra.</summary>
    public async Task ReloadAsync()
    {
        try
        {
            await profiles.LoadAsync();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // Ana uygulama tam o sırada yazıyor olabilir; eldekiyle devam.
        }

        Targets = AnalyticsTargets.From(profiles.Profiles);
        Plan = AnalyticsPlan.Build(Targets, keys.Read);

        var pinned = PinnedTargets().ToList();

        if (!pinned.Select(t => t.AppId).SequenceEqual(Items.Select(i => i.AppId)))
        {
            Items.Clear();

            foreach (var target in pinned)
            {
                Items.Add(new WidgetItem(target));
            }
        }

        foreach (var item in Items)
        {
            item.Apply(await service.GetAsync(item.AppId, Plan.AccountOfApp.GetValueOrDefault(item.AppId)), Settings);
        }

        var tooltip = new StringBuilder("Pipe Manager Pulse");

        foreach (var item in Items)
        {
            tooltip.Append('\n').Append(item.Summary);
        }

        Tooltip = tooltip.ToString();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Seçilen oyunlar; seçim yapılmadıysa ana oyunlardan ilk üçü.</summary>
    public IEnumerable<AnalyticsTarget> PinnedTargets() =>
        Settings.PinnedAppIds is { } ids
            ? Targets.Where(t => ids.Contains(t.AppId))
            : Targets.Where(t => t.Kind == SubAppKind.Main).Take(DefaultPinnedCount);

    public bool IsPinned(uint appId) => PinnedTargets().Any(t => t.AppId == appId);

    public async Task TogglePinnedAsync(uint appId)
    {
        var pinned = PinnedTargets().Select(t => t.AppId).ToList();

        if (!pinned.Remove(appId))
        {
            pinned.Add(appId);
        }

        Settings.PinnedAppIds = pinned;
        await SaveAsync();
        await ReloadAsync();
    }

    public bool IsShown(WidgetMetric metric) => metric switch
    {
        WidgetMetric.Players => Settings.ShowPlayers,
        WidgetMetric.Wishlists => Settings.ShowWishlists,
        _ => Settings.ShowSales,
    };

    public async Task ToggleMetricAsync(WidgetMetric metric)
    {
        switch (metric)
        {
            case WidgetMetric.Players:
                Settings.ShowPlayers = !Settings.ShowPlayers;
                break;
            case WidgetMetric.Wishlists:
                Settings.ShowWishlists = !Settings.ShowWishlists;
                break;
            default:
                Settings.ShowSales = !Settings.ShowSales;
                break;
        }

        await SaveAsync();
        await ReloadAsync();
    }

    public async Task SaveAsync()
    {
        try
        {
            await _store.SaveAsync(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ayar yazılamazsa widget yine çalışıyor; bir sonraki değişiklikte yeniden.
        }
    }
}

/// <summary>Ana uygulamadan widget'ı başlatma; platforma göre farklı çalıştırılabilir.</summary>
public interface IWidgetLauncher
{
    /// <summary>Widget'ı başlatır; bulunamazsa false. Zaten açıksa yenisi kendini kapatıyor.</summary>
    bool Launch();
}
