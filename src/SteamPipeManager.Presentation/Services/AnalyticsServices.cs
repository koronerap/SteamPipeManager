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
        Service = new AnalyticsService(Store, new SteamPlayerCountClient(http), Financial, Keys.Read);

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

/// <summary>Analitiği gösterilen bir app.</summary>
/// <param name="Title">Ana oyunda oyunun adı; diğerlerinde "Oyun — Demo" gibi.</param>
public sealed record AnalyticsTarget(uint AppId, string Title, string GameTitle, SubAppKind Kind);

public static class AnalyticsTargets
{
    /// <summary>
    /// Steam profillerindeki bütün build hedefleri, AppID'ye göre tekilleştirilmiş: aynı
    /// oyun iki profilde olabilir. Ana oyunlar önce.
    /// </summary>
    public static IReadOnlyList<AnalyticsTarget> From(IEnumerable<UserProfile> profiles) =>
        profiles
            .Where(p => p.Provider == PublishProviderId.Steam)
            .SelectMany(p => p.Apps.SelectMany(app => app.SubApps.Select(sub => (App: app, Sub: sub))))
            .Where(x => x.Sub.SteamAppId != 0)
            .GroupBy(x => x.Sub.SteamAppId)
            .Select(g => g.First())
            .OrderBy(x => x.Sub.Kind == SubAppKind.Main ? 0 : 1)
            .ThenBy(x => x.App.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Sub.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => new AnalyticsTarget(
                x.Sub.SteamAppId,
                x.Sub.Kind == SubAppKind.Main || x.Sub.Title == x.App.Title ? x.App.Title : $"{x.App.Title} — {x.Sub.Title}",
                x.App.Title,
                x.Sub.Kind))
            .ToList();
}

/// <summary>
/// Arka planda analitik yenileme döngüsü; uygulama açık olduğu sürece çalışıyor. Steam
/// oyuncu geçmişi vermediği için zirve ve grafik bu döngünün aldığı örneklerden çıkıyor.
///
/// Döngü arayüz iş parçacığında başlatılıyor: <see cref="Updated"/> da orada geliyor.
/// Widget da çalışıyorsa ikisinden yalnızca biri yeniliyor (önbellek kilidi), diğeri okuyor.
/// </summary>
public sealed class AnalyticsRefresher(AnalyticsService service, Func<IReadOnlyCollection<uint>> appIds) : IDisposable
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
                if (appIds() is { Count: > 0 } ids)
                {
                    result = await service.RefreshAsync(ids, force, ct);
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
