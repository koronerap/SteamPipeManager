using SteamPipeManager.Core.Epic;

namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Client secret'ın şifreli deposu. Steam tarafında "profiles.json paylaşılabilir"
/// sözü verilmişti; Epic'te secret kalıcı olmak zorunda olduğu için o söz ancak
/// secret'ı ayrı ve şifreli tutarak korunabiliyor.
/// </summary>
public sealed class EpicSecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"spm_secret_{Guid.NewGuid():N}");

    private string FilePath => Path.Combine(_dir, "epic-secrets.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [WindowsFact("DPAPI")]
    public void A_secret_round_trips()
    {
        var store = new EpicSecretStore(FilePath);
        var profile = Guid.NewGuid();

        store.Write(profile, "s3cr3t-value");

        Assert.True(store.Has(profile));
        Assert.Equal("s3cr3t-value", store.Read(profile));
    }

    /// <summary>Asıl mesele: düz metin diskte durmamalı.</summary>
    [WindowsFact("DPAPI")]
    public void The_secret_is_not_stored_in_plain_text()
    {
        var store = new EpicSecretStore(FilePath);
        store.Write(Guid.NewGuid(), "super-secret-token");

        var onDisk = File.ReadAllText(FilePath);

        Assert.DoesNotContain("super-secret-token", onDisk, StringComparison.Ordinal);
    }

    [WindowsFact("DPAPI")]
    public void Profiles_do_not_see_each_others_secrets()
    {
        var store = new EpicSecretStore(FilePath);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        store.Write(first, "first-secret");
        store.Write(second, "second-secret");

        Assert.Equal("first-secret", store.Read(first));
        Assert.Equal("second-secret", store.Read(second));
    }

    [WindowsFact("DPAPI")]
    public void An_unknown_profile_has_no_secret()
    {
        var store = new EpicSecretStore(FilePath);

        Assert.False(store.Has(Guid.NewGuid()));
        Assert.Null(store.Read(Guid.NewGuid()));
    }

    [WindowsFact("DPAPI")]
    public void Writing_an_empty_value_removes_the_secret()
    {
        var store = new EpicSecretStore(FilePath);
        var profile = Guid.NewGuid();

        store.Write(profile, "value");
        store.Write(profile, "");

        Assert.False(store.Has(profile));
    }

    [WindowsFact("DPAPI")]
    public void Removing_a_profile_takes_its_secret_with_it()
    {
        var store = new EpicSecretStore(FilePath);
        var profile = Guid.NewGuid();

        store.Write(profile, "value");
        store.Remove(profile);

        Assert.Null(store.Read(profile));
    }

    /// <summary>
    /// Bozuk bir depo secret'ın kaybolması demek — kullanıcı yeniden girer. Uygulamanın
    /// açılışta patlamaması daha önemli.
    /// </summary>
    [WindowsFact("DPAPI")]
    public void A_corrupt_store_is_treated_as_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ bu json değil");

        var store = new EpicSecretStore(FilePath);

        Assert.Null(store.Read(Guid.NewGuid()));
        Assert.False(store.Has(Guid.NewGuid()));
    }

    /// <summary>
    /// Başka bir makineden kopyalanmış bir kayıt çözülemez; yok sayılıp kullanıcıdan
    /// yeniden istenmeli, patlamamalı.
    /// </summary>
    [WindowsFact("DPAPI")]
    public void An_undecryptable_entry_is_ignored()
    {
        var profile = Guid.NewGuid();

        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            FilePath,
            $$"""{ "{{profile:N}}": "bm90LXJlYWxseS1lbmNyeXB0ZWQ=" }""");

        Assert.Null(new EpicSecretStore(FilePath).Read(profile));
    }
}
