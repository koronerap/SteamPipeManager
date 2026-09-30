namespace SteamPipeManager.Core.Storage;

/// <summary>
/// Düzenlemeleri kısa bir gecikmeyle diske yazar; her tuş vuruşunda yazmamak için.
///
/// Kurallar:
/// <list type="bullet">
/// <item>Gecikme içinde gelen düzenlemeler tek kayıtta birleşir.</item>
/// <item>Kayıtlar sırayla yapılır; ikisi aynı anda dosyaya yazmaz.</item>
/// <item>Başarılı bir kayıttan sonra "kaydedilmemiş değişiklik" kalmaz. Eski sürüm bu
///   bayrağı hiç temizlemediği için kapanışta her seferinde gereksiz bir kayıt
///   yapılıyor ve pencere o kaydı beklerken kilitleniyordu.</item>
/// <item>Başarısız kayıt değişikliği "kaydedilmemiş" olarak bırakır; sonraki kayıt ya da
///   <see cref="FlushAsync"/> yeniden dener ve kapanışta kullanıcıya sorulabilir.</item>
/// </list>
///
/// Devam eden bir yazma iptal edilmiyor, yalnızca bekleme iptal ediliyor: yarıda
/// kesilen yazma bir şey kazandırmıyor, ardından gelen kayıt zaten yeniden yazıyor.
/// </summary>
public sealed class DebouncedSaver(Func<Task> save, TimeSpan delay)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    private CancellationTokenSource? _waiting;
    private bool _dirty;

    public TimeSpan Delay { get; set; } = delay;

    /// <summary>Diske henüz yazılmamış bir düzenleme var mı.</summary>
    public bool HasUnsavedChanges
    {
        get
        {
            lock (_gate)
            {
                return _dirty;
            }
        }
    }

    /// <summary>Son kaydın hatası; başarılı kayıtta temizlenir.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>Her kayıt denemesinden sonra: başarılıysa null, değilse hata.</summary>
    public event Action<Exception?>? Completed;

    /// <summary>Bir düzenleme oldu; gecikmeden sonra kaydet.</summary>
    public void Schedule()
    {
        var waiting = new CancellationTokenSource();

        lock (_gate)
        {
            _dirty = true;
            _waiting?.Cancel();
            _waiting = waiting;
        }

        _ = SaveAfterDelayAsync(waiting.Token);
    }

    /// <summary>
    /// Bekleyen düzenlemeyi hemen yazar. Sürmekte olan bir kayıt varsa önce onun
    /// bitmesini bekler. Yazılacak bir şey yoksa diske dokunmaz.
    /// </summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            _waiting?.Cancel();
            _waiting = null;
        }

        return SaveIfDirtyAsync(CancellationToken.None);
    }

    private async Task SaveAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Delay, ct);
        }
        catch (OperationCanceledException)
        {
            // Ardından gelen düzenleme ya da FlushAsync kaydı devraldı.
            return;
        }

        await SaveIfDirtyAsync(ct);
    }

    private async Task SaveIfDirtyAsync(CancellationToken ct)
    {
        await _oneAtATime.WaitAsync();

        try
        {
            lock (_gate)
            {
                // Sırada beklerken yeni bir düzenleme geldiyse kayıt ona kaldı.
                if (ct.IsCancellationRequested || !_dirty)
                {
                    return;
                }

                // Yazmadan önce temizleniyor: yazma sürerken gelen düzenleme bayrağı
                // yeniden kaldırır ve kendi kaydını planlar.
                _dirty = false;
            }

            try
            {
                await save();
                LastError = null;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _dirty = true;
                }

                LastError = ex;
            }

            Completed?.Invoke(LastError);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}
