using System.Text.Json.Serialization;

namespace SteamPipeManager.Core.Models;

/// <summary>Kök depo nesnesi; <c>profiles.json</c> bunun serileştirilmiş halidir.</summary>
public sealed class ProfileDatabase
{
    public int SchemaVersion { get; set; } = 1;
    public List<UserProfile> Profiles { get; set; } = [];
}

/// <summary>Bir Steam hesabı. SteamCMD oturumu hesap başına cache'lenir.</summary>
public sealed class UserProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = "";
    public string SteamUsername { get; set; } = "";

    /// <summary>v1.0'da tek geçerli değer <see cref="CredentialMode.SteamCmdCache"/>.</summary>
    public CredentialMode CredentialMode { get; set; } = CredentialMode.SteamCmdCache;

    /// <summary>v1.0'da hep null: tüm profiller ortak SteamCMD kurulumunu paylaşır.</summary>
    public string? SteamCmdInstanceDir { get; set; }

    /// <summary>
    /// Giriş sırasında SteamCMD'nin bildirdiği hesap numarasından türetilir.
    /// Profil avatarını çekmek için kullanılır; bilinmiyorsa null.
    /// </summary>
    public ulong? SteamId64 { get; set; }

    public List<SteamApp> Apps { get; set; } = [];
}

public enum CredentialMode
{
    /// <summary>Şifre saklanmaz; SteamCMD kendi oturumunu hatırlar.</summary>
    SteamCmdCache,
    DpapiStored,
    AlwaysPrompt,
}

/// <summary>Bir oyun. Kendisi build almaz; build hedefleri <see cref="SubApps"/> içindedir.</summary>
public sealed class SteamApp
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string? CoverImagePath { get; set; }
    public string Notes { get; set; } = "";
    public List<SubApp> SubApps { get; set; } = [];
}

/// <summary>Build hedefi: ana oyun, demo, playtest veya beta. Bir Steam AppID'ye karşılık gelir.</summary>
public sealed class SubApp
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public SubAppKind Kind { get; set; } = SubAppKind.Main;
    public uint SteamAppId { get; set; }

    /// <summary>
    /// App script'indeki <c>contentroot</c>. Null ise boş string yazılır ve her depot
    /// kendi <see cref="DepotConfig.ContentRoot"/> değerini kullanır — referans
    /// script'lerdeki düzen budur.
    /// </summary>
    public string? ContentRoot { get; set; }

    public string BuildDescriptionTemplate { get; set; } = "{app} {kind} — {date} {time}";

    /// <summary>Build'in canlı alınacağı beta branch'i. Boş = hiçbir branch'e alma.</summary>
    public string SetLiveBranch { get; set; } = "";

    /// <summary>Preview build: içerik yüklenmez, sadece ne yükleneceği raporlanır.</summary>
    public bool Preview { get; set; }

    public List<DepotConfig> Depots { get; set; } = [];
}

public enum SubAppKind
{
    Main,
    Demo,
    Playtest,
    Beta,
    Tool,
    Other,
}

/// <summary>Tek bir depot ve içeriğinin nasıl eşleneceği.</summary>
public sealed class DepotConfig
{
    public uint DepotId { get; set; }

    /// <summary>Sadece arayüzde gösterilir (ör. "Windows"), VDF'ye yazılmaz.</summary>
    public string Label { get; set; } = "";

    /// <summary>Bu depot'un içerik kökü. Null ise <see cref="SubApp.ContentRoot"/> kullanılır.</summary>
    public string? ContentRoot { get; set; }

    public List<FileMapping> FileMappings { get; set; } = [FileMapping.Everything()];
    public List<string> FileExclusions { get; set; } = [];

    /// <summary>v1.1: depot'a gömülecek install script yolu.</summary>
    public string? InstallScript { get; set; }

    /// <summary>v1.1: dosya bazlı öznitelikler (ör. <c>userconfig</c>).</summary>
    public List<FileProperty> FileProperties { get; set; } = [];
}

public sealed class FileMapping
{
    public string LocalPath { get; set; } = "*";
    public string DepotPath { get; set; } = ".";
    public bool Recursive { get; set; } = true;

    public static FileMapping Everything() => new();
}

public sealed class FileProperty
{
    public string LocalPath { get; set; } = "";
    public string Attributes { get; set; } = "";
}

/// <summary>Tamamlanmış (ya da başarısız olmuş) bir build'in kaydı.</summary>
public sealed class BuildRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubAppId { get; set; }
    public uint SteamAppId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public BuildOutcome Outcome { get; set; } = BuildOutcome.Running;
    public bool WasPreview { get; set; }
    public string Description { get; set; } = "";
    public string SetLiveBranch { get; set; } = "";

    /// <summary>SteamCMD'nin bildirdiği BuildID; sadece başarılı build'lerde dolu.</summary>
    public uint? SteamBuildId { get; set; }

    public int? ExitCode { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>Ham SteamCMD çıktısının kaydedildiği dosya.</summary>
    public string? LogFilePath { get; set; }

    [JsonIgnore]
    public TimeSpan? Duration => FinishedAt - StartedAt;

    /// <summary>
    /// Gösterim için hazır süre. <see cref="Duration"/> nullable olduğu için XAML'deki
    /// <c>StringFormat</c> ona uygulanmıyor ve alan boş kalıyordu.
    /// </summary>
    [JsonIgnore]
    public string DurationText => Duration is { } duration
        ? duration.ToString(duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss")
        : "—";

}

public enum BuildOutcome
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
}
