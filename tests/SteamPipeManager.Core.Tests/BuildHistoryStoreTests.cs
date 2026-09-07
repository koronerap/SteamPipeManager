using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Storage;
using SteamPipeManager.Core.Workspace;

namespace SteamPipeManager.Core.Tests;

public sealed class BuildHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"spm_hist_{Guid.NewGuid():N}");

    private BuildHistoryStore Store => new(new WorkspaceLayout(_root));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static BuildRecord Record(Guid subAppId, BuildOutcome outcome, DateTimeOffset started) => new()
    {
        SubAppId = subAppId,
        SteamAppId = 1000,
        StartedAt = started,
        FinishedAt = started.AddMinutes(2),
        Outcome = outcome,
        SteamBuildId = outcome == BuildOutcome.Succeeded ? 4242u : null,
    };

    [Fact]
    public async Task Missing_file_yields_empty_history()
    {
        Assert.Empty((await Store.LoadAsync()).Records);
    }

    [Fact]
    public async Task Appends_and_reads_back()
    {
        var store = Store;
        var subApp = Guid.NewGuid();

        await store.AppendAsync(Record(subApp, BuildOutcome.Succeeded, DateTimeOffset.Now));

        var record = Assert.Single(await store.ForSubAppAsync(subApp));
        Assert.Equal(BuildOutcome.Succeeded, record.Outcome);
        Assert.Equal(4242u, record.SteamBuildId);
        Assert.Equal(TimeSpan.FromMinutes(2), record.Duration);
    }

    [Fact]
    public async Task Filters_by_sub_app()
    {
        var store = Store;
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();

        await store.AppendAsync(Record(mine, BuildOutcome.Succeeded, DateTimeOffset.Now));
        await store.AppendAsync(Record(other, BuildOutcome.Failed, DateTimeOffset.Now));

        Assert.Single(await store.ForSubAppAsync(mine));
        Assert.Single(await store.ForSubAppAsync(other));
    }

    [Fact]
    public async Task Newest_record_comes_first()
    {
        var store = Store;
        var subApp = Guid.NewGuid();
        var now = DateTimeOffset.Now;

        await store.AppendAsync(Record(subApp, BuildOutcome.Failed, now.AddHours(-2)));
        await store.AppendAsync(Record(subApp, BuildOutcome.Succeeded, now));
        await store.AppendAsync(Record(subApp, BuildOutcome.Failed, now.AddHours(-1)));

        var records = await store.ForSubAppAsync(subApp);

        Assert.Equal(
            [now, now.AddHours(-1), now.AddHours(-2)],
            records.Select(r => r.StartedAt));
    }

    [Fact]
    public async Task Last_successful_skips_failures()
    {
        var store = Store;
        var subApp = Guid.NewGuid();
        var now = DateTimeOffset.Now;

        await store.AppendAsync(Record(subApp, BuildOutcome.Succeeded, now.AddHours(-3)));
        await store.AppendAsync(Record(subApp, BuildOutcome.Failed, now));

        var last = await store.LastSuccessfulAsync(subApp);

        Assert.NotNull(last);
        Assert.Equal(BuildOutcome.Succeeded, last!.Outcome);
        Assert.Equal(now.AddHours(-3), last.StartedAt);
    }

    [Fact]
    public async Task Returns_null_when_no_successful_build_exists()
    {
        var store = Store;
        var subApp = Guid.NewGuid();

        await store.AppendAsync(Record(subApp, BuildOutcome.Failed, DateTimeOffset.Now));

        Assert.Null(await store.LastSuccessfulAsync(subApp));
    }

    [Fact]
    public async Task Re_appending_the_same_record_does_not_duplicate_it()
    {
        var store = Store;
        var subApp = Guid.NewGuid();
        var record = Record(subApp, BuildOutcome.Running, DateTimeOffset.Now);

        await store.AppendAsync(record);

        record.Outcome = BuildOutcome.Succeeded;
        await store.AppendAsync(record);

        var stored = Assert.Single(await store.ForSubAppAsync(subApp));
        Assert.Equal(BuildOutcome.Succeeded, stored.Outcome);
    }

    /// <summary>Geçmiş sınırsız büyümemeli; en eskiler düşer.</summary>
    [Fact]
    public async Task Trims_to_the_maximum_record_count()
    {
        var store = Store;
        var subApp = Guid.NewGuid();
        var now = DateTimeOffset.Now;

        for (var i = 0; i < BuildHistoryStore.MaxRecords + 25; i++)
        {
            await store.AppendAsync(Record(subApp, BuildOutcome.Succeeded, now.AddSeconds(i)));
        }

        Assert.Equal(BuildHistoryStore.MaxRecords, (await store.LoadAsync()).Records.Count);
    }

    [Fact]
    public async Task History_lives_outside_profiles_json()
    {
        var layout = new WorkspaceLayout(_root);
        var store = new BuildHistoryStore(layout);

        await store.AppendAsync(Record(Guid.NewGuid(), BuildOutcome.Succeeded, DateTimeOffset.Now));

        Assert.EndsWith("history.json", store.FilePath);
        Assert.False(File.Exists(layout.ProfilesFile));
    }
}
