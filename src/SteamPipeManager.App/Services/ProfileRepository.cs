using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Storage;

namespace SteamPipeManager.App.Services;

/// <summary>
/// Profil ağacının uygulama ömrü boyunca tek kopyası. ViewModel'ler doğrudan model
/// nesnelerini düzenler; <see cref="SaveAsync"/> tüm ağacı diske yazar.
/// </summary>
public sealed class ProfileRepository(IProfileStore store)
{
    private ProfileDatabase _database = new();

    public IReadOnlyList<UserProfile> Profiles => _database.Profiles;

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _database = await store.LoadAsync(ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await store.SaveAsync(_database, ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public UserProfile AddProfile(string displayName, string steamUsername)
    {
        var profile = new UserProfile { DisplayName = displayName, SteamUsername = steamUsername };
        _database.Profiles.Add(profile);
        return profile;
    }

    public void RemoveProfile(UserProfile profile) => _database.Profiles.Remove(profile);

    /// <summary>Bir profilin altındaki tüm build hedefleri (kart sayacı ve doğrulama için).</summary>
    public static IEnumerable<SubApp> AllSubApps(UserProfile profile) =>
        profile.Apps.SelectMany(a => a.SubApps);

    public UserProfile? FindOwner(SteamApp app) =>
        _database.Profiles.FirstOrDefault(p => p.Apps.Contains(app));

    public SteamApp? FindOwner(SubApp subApp) =>
        _database.Profiles.SelectMany(p => p.Apps).FirstOrDefault(a => a.SubApps.Contains(subApp));
}
