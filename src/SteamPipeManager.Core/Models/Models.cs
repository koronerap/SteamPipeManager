using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace SteamPipeManager.Core.Models;

/// <summary>Kök depo nesnesi; <c>profiles.json</c> bunun serileştirilmiş halidir.</summary>
public sealed class ProfileDatabase
{
    /// <summary>
    /// Diskteki biçimin sürümü. v1'de sağlayıcı kavramı yoktu ve her profil Steam'di;
    /// v2 <see cref="UserProfile.Provider"/> alanını ekledi. Okuma sırasında eski
    /// dosyalar yükseltilir, bkz. <see cref="ProfileSchema"/>.
    /// </summary>
    public int SchemaVersion { get; set; } = ProfileSchema.Current;

    public List<UserProfile> Profiles { get; set; } = [];
}

/// <summary>
/// <c>profiles.json</c> biçim sürümleri arasındaki geçişler.
///
/// Göç okuma anında yapılıyor ve dosya ancak kullanıcı bir şey değiştirdiğinde yeni
/// sürümle yazılıyor; uygulamayı bir kez açıp kapatmak kimsenin dosyasını dönüştürmüyor.
/// </summary>
public static class ProfileSchema
{
    public const int Current = 3;

    /// <summary>
    /// Eski bir veritabanını güncel şemaya taşır.
    ///
    /// Adımlar <b>sırayla</b> uygulanıyor: v1'den gelen bir dosya v2 adımından geçip
    /// sonra v3 adımına giriyor. Doğrudan güncel sürüme atlamak bugün çalışırdı çünkü
    /// adımların ikisi de veri taşımıyor, ama ileride gerçek iş yapan bir adım
    /// eklendiğinde eski dosyalar onu sessizce atlardı.
    ///
    /// Bilinmeyen (daha yeni) sürümlere dokunulmaz: ileri sürümden dönen bir
    /// kullanıcının verisini bozmaktansa olduğu gibi bırakmak yeğdir.
    /// </summary>
    public static ProfileDatabase Upgrade(ProfileDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        // Sürüm alanı hiç olmayan çok eski bir dosya 0 olarak okunur; o da v1 sayılır.
        if (database.SchemaVersion < 1)
        {
            database.SchemaVersion = 1;
        }

        while (database.SchemaVersion < Current)
        {
            database = database.SchemaVersion switch
            {
                1 => ToVersion2(database),
                2 => ToVersion3(database),

                // Buraya düşmek, Current arttırılıp adımının yazılmadığı anlamına gelir.
                _ => throw new InvalidOperationException(
                    $"profiles.json şema sürümü {database.SchemaVersion} için geçiş adımı tanımlı değil."),
            };
        }

        return database;
    }

    /// <summary>
    /// v1 → v2: sağlayıcı kavramı eklendi. v1'de her profil Steam'di ve enum'un
    /// varsayılanı zaten Steam olduğu için taşınacak veri yok.
    /// </summary>
    private static ProfileDatabase ToVersion2(ProfileDatabase database)
    {
        database.SchemaVersion = 2;
        return database;
    }

    /// <summary>
    /// v2 → v3: Epic alanları eklendi (<see cref="UserProfile.Epic"/>,
    /// <see cref="SteamApp.Epic"/>). Hepsi isteğe bağlı ve null; Steam profilleri
    /// hiç etkilenmiyor, dosyalarında bu alanlar görünmüyor bile.
    /// </summary>
    private static ProfileDatabase ToVersion3(ProfileDatabase database)
    {
        database.SchemaVersion = 3;
        return database;
    }
}

/// <summary>Bir Steam hesabı. SteamCMD oturumu hesap başına cache'lenir.</summary>
public sealed class UserProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Bu profilin build'lerini gönderdiği mağaza. Şema v1'den gelen dosyalarda alan
    /// yok; enum varsayılanı Steam olduğu için eski profiller doğru okunuyor.
    /// </summary>
    public PublishProviderId Provider { get; set; } = PublishProviderId.Steam;

    /// <summary>
    /// Epic profillerinin hesap ayarları; Steam profillerinde null ve dosyada hiç
    /// görünmüyor. Client secret burada değil — şifreli ayrı depoda.
    /// </summary>
    public EpicProfileSettings? Epic { get; set; }

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
public sealed class SteamApp : ObservableModel
{
    private string _title = "";

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string? CoverImagePath { get; set; }
    public string Notes { get; set; } = "";

    /// <summary>Steam build hedefleri. Epic profillerindeki oyunlarda boş kalır.</summary>
    public List<SubApp> SubApps { get; set; } = [];

    /// <summary>
    /// Oyunun Epic tarafı: product kimliği ve artifact'ler. Steam oyunlarında null.
    ///
    /// İki liste yan yana duruyor çünkü bir oyun profilin altında yaşıyor ve profilin
    /// sağlayıcısı belli — yani ikisi aynı anda dolu olmuyor. Ortak bir "hedef"
    /// soyutlaması, iki tarafın da somutlaştığı bu noktada tasarlanacak.
    /// </summary>
    public EpicGameSettings? Epic { get; set; }
}

/// <summary>Build hedefi: ana oyun, demo, playtest veya beta. Bir Steam AppID'ye karşılık gelir.</summary>
public sealed class SubApp : ObservableModel
{
    private string _title = "";
    private SubAppKind _kind = SubAppKind.Main;
    private uint _steamAppId;
    private string? _contentRoot;
    private string _buildDescriptionTemplate = "{app} {kind} — {date} {time}";
    private string _setLiveBranch = "";
    private bool _preview;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public SubAppKind Kind
    {
        get => _kind;
        set => Set(ref _kind, value);
    }

    public uint SteamAppId
    {
        get => _steamAppId;
        set => Set(ref _steamAppId, value);
    }

    /// <summary>
    /// App script'indeki <c>contentroot</c>. Null ise boş string yazılır ve her depot
    /// kendi <see cref="DepotConfig.ContentRoot"/> değerini kullanır — referans
    /// script'lerdeki düzen budur.
    /// </summary>
    public string? ContentRoot
    {
        get => _contentRoot;
        set => Set(ref _contentRoot, value);
    }

    public string BuildDescriptionTemplate
    {
        get => _buildDescriptionTemplate;
        set => Set(ref _buildDescriptionTemplate, value);
    }

    /// <summary>Build'in canlı alınacağı beta branch'i. Boş = hiçbir branch'e alma.</summary>
    public string SetLiveBranch
    {
        get => _setLiveBranch;
        set => Set(ref _setLiveBranch, value);
    }

    /// <summary>Preview build: içerik yüklenmez, sadece ne yükleneceği raporlanır.</summary>
    public bool Preview
    {
        get => _preview;
        set => Set(ref _preview, value);
    }

    /// <summary>
    /// Depot listesi. Arayüzdeki liste doğrudan buna bağlandığı için gözlemlenebilir
    /// olmak zorunda: düz <c>List</c> iken eklenen/silinen depot ekrana yansımıyordu.
    /// </summary>
    public ObservableCollection<DepotConfig> Depots { get; set; } = [];
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
public sealed class DepotConfig : ObservableModel
{
    private uint _depotId;
    private string _label = "";
    private string? _contentRoot;

    public uint DepotId
    {
        get => _depotId;
        set => Set(ref _depotId, value);
    }

    /// <summary>Sadece arayüzde gösterilir (ör. "Windows"), VDF'ye yazılmaz.</summary>
    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    /// <summary>Bu depot'un içerik kökü. Null ise <see cref="SubApp.ContentRoot"/> kullanılır.</summary>
    public string? ContentRoot
    {
        get => _contentRoot;
        set => Set(ref _contentRoot, value);
    }

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

    /// <summary>
    /// Build hedefinin kimliği. Adı Steam'den kalma; Epic kayıtlarında
    /// <see cref="EpicArtifact.Id"/> tutuluyor. Yeniden adlandırmak var olan
    /// <c>history.json</c> kayıtlarındaki alanı kopardığı için adı korunuyor.
    /// </summary>
    public Guid SubAppId { get; set; }

    /// <summary>Kaydın hangi mağazaya ait olduğu. Eski kayıtlarda alan yok, Steam sayılır.</summary>
    public PublishProviderId Provider { get; set; } = PublishProviderId.Steam;

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

    /// <summary>Ham araç çıktısının kaydedildiği dosya.</summary>
    public string? LogFilePath { get; set; }

    /// <summary>Yalnızca Epic: yayınlanan artifact.</summary>
    public string? EpicArtifactId { get; set; }

    /// <summary>
    /// Yalnızca Epic: gönderilen sürüm dizesi. Steam'de karşılığı
    /// <see cref="SteamBuildId"/>, ama onu Steam üretiyor; bunu biz veriyoruz.
    /// </summary>
    public string? EpicBuildVersion { get; set; }

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
