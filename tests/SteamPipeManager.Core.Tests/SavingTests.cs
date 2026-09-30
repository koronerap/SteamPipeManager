using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Otomatik kayıt. Bu kuralların biri bozulduğunda belirti "kapatıp açınca
/// değişiklik yok" oluyor — ancak iş işten geçtikten sonra fark ediliyor.
/// </summary>
public sealed class DebouncedSaverTests
{
    private sealed class CountingSave
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Func<Task>? Body { get; set; }

        public async Task Invoke()
        {
            Interlocked.Increment(ref _count);

            if (Body is not null)
            {
                await Body();
            }
        }
    }

    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(40);

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Beklenen durum oluşmadı.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Edits_within_the_delay_are_saved_once()
    {
        var save = new CountingSave();
        var saver = new DebouncedSaver(save.Invoke, ShortDelay);

        for (var i = 0; i < 5; i++)
        {
            saver.Schedule();
        }

        await WaitUntil(() => save.Count == 1 && !saver.HasUnsavedChanges);
        await Task.Delay(150);

        Assert.Equal(1, save.Count);
    }

    /// <summary>
    /// Asıl hata buydu: başarılı kayıttan sonra bayrak temizlenmediği için kapanış
    /// her seferinde yeni bir kayıt başlatıyor ve o kaydı beklerken kilitleniyordu.
    /// </summary>
    [Fact]
    public async Task After_a_successful_save_there_is_nothing_left_to_flush()
    {
        var save = new CountingSave();
        var saver = new DebouncedSaver(save.Invoke, ShortDelay);

        saver.Schedule();
        await WaitUntil(() => save.Count == 1);
        await WaitUntil(() => !saver.HasUnsavedChanges);

        await saver.FlushAsync();

        Assert.Equal(1, save.Count);
    }

    [Fact]
    public async Task Flush_writes_a_pending_edit_immediately()
    {
        var save = new CountingSave();
        var saver = new DebouncedSaver(save.Invoke, TimeSpan.FromMinutes(5));

        saver.Schedule();
        Assert.True(saver.HasUnsavedChanges);

        await saver.FlushAsync();

        Assert.Equal(1, save.Count);
        Assert.False(saver.HasUnsavedChanges);
    }

    [Fact]
    public async Task Flush_without_edits_does_not_touch_the_disk()
    {
        var save = new CountingSave();

        await new DebouncedSaver(save.Invoke, ShortDelay).FlushAsync();

        Assert.Equal(0, save.Count);
    }

    [Fact]
    public async Task A_failed_save_keeps_the_change_unsaved_and_is_retried_by_flush()
    {
        var fail = true;
        var save = new CountingSave { Body = () => fail ? throw new IOException("disk full") : Task.CompletedTask };
        var saver = new DebouncedSaver(save.Invoke, ShortDelay);

        Exception? reported = null;
        saver.Completed += error => reported = error;

        saver.Schedule();
        await WaitUntil(() => save.Count == 1 && reported is not null);

        Assert.True(saver.HasUnsavedChanges);
        Assert.Equal("disk full", saver.LastError?.Message);

        fail = false;
        await saver.FlushAsync();

        Assert.Equal(2, save.Count);
        Assert.False(saver.HasUnsavedChanges);
        Assert.Null(saver.LastError);
    }

    [Fact]
    public async Task Flush_waits_for_a_save_in_progress_and_then_writes_the_edit_made_during_it()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = new CountingSave();
        var saver = new DebouncedSaver(save.Invoke, ShortDelay);

        save.Body = () => save.Count == 1 ? release.Task : Task.CompletedTask;

        saver.Schedule();
        await WaitUntil(() => save.Count == 1);

        // Yazma sürerken yeni bir düzenleme.
        saver.Schedule();
        var flush = saver.FlushAsync();

        Assert.False(flush.IsCompleted);

        release.SetResult();
        await flush;

        Assert.Equal(2, save.Count);
        Assert.False(saver.HasUnsavedChanges);
    }

    [Fact]
    public async Task Saves_never_overlap()
    {
        var running = 0;
        var overlapped = false;
        var saver = new DebouncedSaver(async () =>
        {
            if (Interlocked.Increment(ref running) > 1)
            {
                overlapped = true;
            }

            await Task.Delay(20);
            Interlocked.Decrement(ref running);
        }, TimeSpan.FromMilliseconds(1));

        var flushes = new List<Task>();

        for (var i = 0; i < 20; i++)
        {
            saver.Schedule();
            flushes.Add(saver.FlushAsync());
        }

        await Task.WhenAll(flushes);

        Assert.False(overlapped);
        Assert.False(saver.HasUnsavedChanges);
    }
}

public sealed class JsonFileStoreSavingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spm-saving-" + Guid.NewGuid().ToString("N"));

    private WorkspaceLayout Layout => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Kilitlenen eski sürüm <c>profiles.json.tmp</c>'yi açık tutuyordu; sabit adlı geçici
    /// dosya yüzünden yeni açılan uygulamanın kayıtlarının hepsi başarısız oluyordu.
    /// </summary>
    [Fact]
    public async Task A_temp_file_held_open_by_another_process_does_not_block_saving()
    {
        var store = new JsonProfileStore(Layout);
        Directory.CreateDirectory(_root);

        await using (new FileStream(store.FilePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var database = new ProfileDatabase();
            database.Profiles.Add(new UserProfile { DisplayName = "Saved anyway" });

            await store.SaveAsync(database);
        }

        Assert.Equal("Saved anyway", Assert.Single((await store.LoadAsync()).Profiles).DisplayName);
    }

    [Fact]
    public async Task Saving_leaves_no_temp_files()
    {
        var store = new JsonProfileStore(Layout);

        for (var i = 0; i < 3; i++)
        {
            await store.SaveAsync(new ProfileDatabase());
        }

        Assert.Equal(["profiles.json"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    /// <summary>
    /// Kilitlenmenin yeniden üretilmesi: tek iş parçacıklı bir bağlam (WPF'in arayüz
    /// iş parçacığı gibi) kaydı bloklayarak bekliyor. Depo beklemelerini o bağlama geri
    /// göndermeye çalışsaydı bu test asla bitmezdi.
    /// </summary>
    [Fact]
    public async Task Saving_does_not_need_the_calling_thread_to_be_free()
    {
        var store = new JsonProfileStore(Layout);
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new BlockedUiContext());

            try
            {
                // Eski kapanış kodunun yaptığı şey: bloklayarak beklemek.
                store.SaveAsync(new ProfileDatabase()).GetAwaiter().GetResult();
                store.LoadAsync().GetAwaiter().GetResult();
                finished.SetResult(null);
            }
            catch (Exception ex)
            {
                finished.SetResult(ex);
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();

        var completed = await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(completed == finished.Task, "Kayıt, çağıran iş parçacığı bloklanınca kilitlendi.");
        Assert.Null(await finished.Task);
    }

    /// <summary>Kendisine gönderilen işi hiç çalıştırmayan bir arayüz bağlamı.</summary>
    private sealed class BlockedUiContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            // Arayüz iş parçacığı bloklandığı için kuyruğa giren iş asla çalışmaz.
        }

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new InvalidOperationException("UI thread is blocked.");
    }
}
