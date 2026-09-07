using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.Core.Storage;

public sealed class BuildHistory
{
    public int SchemaVersion { get; set; } = 1;

    public List<BuildRecord> Records { get; set; } = [];
}

/// <summary>
/// Alınan build'lerin kaydı. Profil verisinden ayrı dosyada tutulur: sürekli büyüyen
/// bir liste, elle düzenlenebilir olması istenen <c>profiles.json</c>'ı şişirmesin.
/// </summary>
public sealed class BuildHistoryStore(WorkspaceLayout layout)
{
    /// <summary>Sınırsız büyümeyi önlemek için tutulan en fazla kayıt sayısı.</summary>
    public const int MaxRecords = 500;

    private readonly JsonFileStore<BuildHistory> _store =
        new(Path.Combine(layout.Root, "history.json"), () => new BuildHistory());

    public string FilePath => _store.FilePath;

    public Task<BuildHistory> LoadAsync(CancellationToken ct = default) => _store.LoadAsync(ct);

    /// <summary>Kaydı ekler, en yeniler başta olacak şekilde sıralar ve dosyayı yazar.</summary>
    public async Task AppendAsync(BuildRecord record, CancellationToken ct = default)
    {
        var history = await LoadAsync(ct);

        history.Records.RemoveAll(r => r.Id == record.Id);
        history.Records.Insert(0, record);

        if (history.Records.Count > MaxRecords)
        {
            history.Records.RemoveRange(MaxRecords, history.Records.Count - MaxRecords);
        }

        await _store.SaveAsync(history, ct);
    }

    public async Task<IReadOnlyList<BuildRecord>> ForSubAppAsync(Guid subAppId, CancellationToken ct = default)
    {
        var history = await LoadAsync(ct);

        return [.. history.Records
            .Where(r => r.SubAppId == subAppId)
            .OrderByDescending(r => r.StartedAt)];
    }

    /// <summary>Bir hedefin en son başarılı build'i — "şu an yayında ne var" sorusunun cevabı.</summary>
    public async Task<BuildRecord?> LastSuccessfulAsync(Guid subAppId, CancellationToken ct = default) =>
        (await ForSubAppAsync(subAppId, ct))
            .FirstOrDefault(r => r.Outcome == BuildOutcome.Succeeded);
}
