using SteamPipeManager.Core.Analytics;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.Presentation.Services;

/// <summary>
/// Analitik parçalarının bir arada kurulumu. Ana uygulamanın iki arayüzü (WPF, Avalonia)
/// ve widget aynı şekilde kuruyor; kopyalanıp ayrışmasın diye tek yerde.
/// </summary>
public sealed class AnalyticsServices
{
    private AnalyticsServices(WorkspaceLayout layout)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        Keys = new SteamFinancialKeyStore(layout.SteamFinancialKeyFile);
        Financial = new SteamFinancialClient(http);
        Store = new AnalyticsStore(layout.AnalyticsDirectory);
        Service = new AnalyticsService(Store, new SteamPlayerCountClient(http), Financial);

        // Anahtarı denemek için ayrı istemci: yenileme döngüsü kendi istemcisine istek
        // sınırı koyuyor, ayarlardaki deneme ona takılmasın.
        KeyTester = new SteamFinancialClient(http);
    }

    public static AnalyticsServices Create(WorkspaceLayout layout) => new(layout);

    public SteamFinancialKeyStore Keys { get; }

    public SteamFinancialClient Financial { get; }

    public SteamFinancialClient KeyTester { get; }

    public AnalyticsStore Store { get; }

    public AnalyticsService Service { get; }
}

/// <summary>Bir app'in bulunduğu profil; finans anahtarı profil başına.</summary>
public sealed record AnalyticsOwner(Guid ProfileId, string ProfileName);

/// <summary>Analitiği gösterilen bir app.</summary>
/// <param name="Title">Ana oyunda oyunun adı; diğerlerinde "Oyun — Demo" gibi.</param>
/// <param name="Owners">App'in bulunduğu profiller; aynı oyun iki profilde olabiliyor.</param>
public sealed record AnalyticsTarget(uint AppId, string Title, string GameTitle, SubAppKind Kind, IReadOnlyList<AnalyticsOwner> Owners);

/// <summary>
/// Profillerin anahtarlarından çıkan yenileme planı: hangi anahtar hangi oyunları
/// sorguluyor, her oyunun satışları hangi hesapta, hangi hesap hangi profillerin.
/// </summary>
public sealed record AnalyticsPlan(
    AnalyticsScope Scope,
    IReadOnlyDictionary<uint, string> AccountOfApp,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ProfilesOfAccount)
{
    /// <summary>
    /// Her oyun, anahtarı olan ilk profilinin anahtarıyla sorgulanıyor. Aynı anahtar iki
    /// profilde (aynı şirket) tek hesap sayılıyor; satışlar bir kez indiriliyor.
    /// </summary>
    public static AnalyticsPlan Build(IReadOnlyList<AnalyticsTarget> targets, Func<Guid, string?> keyOf)
    {
        var keys = new Dictionary<Guid, string?>();
        string? KeyOf(Guid profileId) => keys.TryGetValue(profileId, out var key) ? key : keys[profileId] = keyOf(profileId);

        var accountOfApp = new Dictionary<uint, string>();
        var appsOfKey = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        var profilesOfAccount = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var target in targets)
        {
            if (target.Owners.FirstOrDefault(o => KeyOf(o.ProfileId) is { Length: > 0 }) is not { } owner)
            {
                continue;
            }

            var key = KeyOf(owner.ProfileId)!;
            var account = SteamFinancialKeyStore.Fingerprint(key);

            if (!appsOfKey.TryGetValue(key, out var apps))
            {
                appsOfKey[key] = apps = [];
            }

            apps.Add(target.AppId);
            accountOfApp[target.AppId] = account;

            if (!profilesOfAccount.TryGetValue(account, out var names))
            {
                profilesOfAccount[account] = names = [];
            }

            if (!names.Contains(owner.ProfileName))
            {
                names.Add(owner.ProfileName);
            }
        }

        return new AnalyticsPlan(
            new AnalyticsScope(
                [.. targets.Select(t => t.AppId)],
                [.. appsOfKey.Select(p => new FinancialAccount(p.Key, p.Value))]),
            accountOfApp,
            profilesOfAccount.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value));
    }
}

public static class AnalyticsTargets
{
    /// <summary>
    /// Steam profillerindeki bütün build hedefleri, AppID'ye göre tekilleştirilmiş: aynı
    /// oyun iki profilde olabilir. Ana oyunlar önce.
    /// </summary>
    public static IReadOnlyList<AnalyticsTarget> From(IEnumerable<UserProfile> profiles) =>
        profiles
            .Where(p => p.Provider == PublishProviderId.Steam)
            .SelectMany(p => p.Apps.SelectMany(app => app.SubApps.Select(sub => (Profile: p, App: app, Sub: sub))))
            .Where(x => x.Sub.SteamAppId != 0)
            .GroupBy(x => x.Sub.SteamAppId)
            .Select(g => (First: g.First(), Owners: g.Select(x => new AnalyticsOwner(x.Profile.Id, x.Profile.DisplayName)).DistinctBy(o => o.ProfileId).ToList()))
            .OrderBy(x => x.First.Sub.Kind == SubAppKind.Main ? 0 : 1)
            .ThenBy(x => x.First.App.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.First.Sub.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => new AnalyticsTarget(
                x.First.Sub.SteamAppId,
                x.First.Sub.Kind == SubAppKind.Main || x.First.Sub.Title == x.First.App.Title
                    ? x.First.App.Title
                    : $"{x.First.App.Title} — {x.First.Sub.Title}",
                x.First.App.Title,
                x.First.Sub.Kind,
                x.Owners))
            .ToList();
}

/// <summary>
/// Arka planda analitik yenileme döngüsü; uygulama açık olduğu sürece çalışıyor. Steam
/// oyuncu geçmişi vermediği için zirve ve grafik bu döngünün aldığı örneklerden çıkıyor.
///
/// Döngü arayüz iş parçacığında başlatılıyor: <see cref="Updated"/> da orada geliyor.
/// Widget da çalışıyorsa ikisinden yalnızca biri yeniliyor (önbellek kilidi), diğeri okuyor.
/// </summary>
public sealed class AnalyticsRefresher(AnalyticsService service, Func<AnalyticsScope> scope) : IDisposable
{
    private readonly SemaphoreSlim _wake = new(0);
    private CancellationTokenSource? _loop;
    private bool _force;

    /// <summary>Bir yenileme turu bitti; önbellek yeniden okunabilir.</summary>
    public event EventHandler? Updated;

    /// <summary>Geçmiş verinin bir kısmı henüz inmedi.</summary>
    public bool IsCatchingUp { get; private set; }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = new CancellationTokenSource();
        _ = RunAsync(_loop.Token);
    }

    /// <summary>Beklemeden bir tur; <paramref name="force"/> aralıkları yok sayar.</summary>
    public void RefreshSoon(bool force = false)
    {
        _force |= force;
        _wake.Release();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = RefreshResult.Done;
            var force = _force;
            _force = false;

            try
            {
                if (scope() is { AppIds.Count: > 0 } current)
                {
                    result = await service.RefreshAsync(current, force, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Önbellek ya da anahtar deposu okunamadı: döngü durmasın, sonra yeniden.
            }

            IsCatchingUp = result == RefreshResult.MorePending;
            Updated?.Invoke(this, EventArgs.Empty);

            var delay = result switch
            {
                RefreshResult.MorePending => TimeSpan.FromSeconds(2),
                RefreshResult.Busy => TimeSpan.FromSeconds(30),
                _ => TimeSpan.FromMinutes(1),
            };

            try
            {
                await _wake.WaitAsync(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
    }
}
