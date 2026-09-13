using System.Collections.ObjectModel;

namespace SteamPipeManager.Core.Models;

/// <summary>
/// Bir Epic profilinin hesap düzeyindeki ayarları.
///
/// Yalnızca <see cref="PublishProviderId.Epic"/> profillerinde dolu; Steam profillerinde
/// null ve <c>profiles.json</c>'da hiç görünmüyor.
///
/// <b>Client secret burada yok</b> ve olmayacak: <c>profiles.json</c>'ın yedeklenebilir
/// ve paylaşılabilir kalması Steam tarafında verdiğimiz bir sözdü, Epic'te de bozulmuyor.
/// Secret şifreli ayrı bir depoda tutuluyor.
/// </summary>
public sealed class EpicProfileSettings : ObservableModel
{
    private string _organizationId = "";
    private string _clientId = "";

    /// <summary>Dev Portal'daki organizasyonun kimliği.</summary>
    public string OrganizationId
    {
        get => _organizationId;
        set => Set(ref _organizationId, value);
    }

    /// <summary>
    /// BPT Credentials sekmesinden alınan client id. Oyunun EOS SDK kimliğinden ayrı —
    /// SDK Credentials altındakiler BuildPatchTool ile çalışmıyor.
    /// </summary>
    public string ClientId
    {
        get => _clientId;
        set => Set(ref _clientId, value);
    }

    public bool IsComplete =>
        OrganizationId.Length > 0 && ClientId.Length > 0;
}

/// <summary>
/// Bir oyunun Epic tarafındaki karşılığı: bir <b>product</b> ve altındaki artifact'ler.
/// Steam'deki <see cref="SteamApp.SubApps"/> listesinin karşılığı burada
/// <see cref="Artifacts"/>.
/// </summary>
public sealed class EpicGameSettings : ObservableModel
{
    private string _productId = "";

    public string ProductId
    {
        get => _productId;
        set => Set(ref _productId, value);
    }

    public ObservableCollection<EpicArtifact> Artifacts { get; set; } = [];
}

/// <summary>
/// Epic'teki tek bir build hedefi (artifact).
///
/// Steam'deki <see cref="SubApp"/>'in karşılığı, ama depot kavramı yok: bir artifact'in
/// tek bir <see cref="BuildRoot"/>'u var. Platform ayrımı depot'larla değil, ayrı
/// artifact'lerle yapılıyor.
/// </summary>
public sealed class EpicArtifact : ObservableModel
{
    private string _title = "";
    private SubAppKind _kind = SubAppKind.Main;
    private string _artifactId = "";
    private string _buildRoot = "";
    private string _appLaunch = "";
    private string _appArgs = "";
    private string? _cloudDir;
    private string _label = "";
    private string _platform = "Windows";
    private string? _sandboxId;
    private string _buildVersionTemplate = "{app}-{date}-{n}";
    private bool _preview;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    /// <summary>Ana oyun, demo, playtest… Steam ile ortak; arayüz aynı ikonları kullanıyor.</summary>
    public SubAppKind Kind
    {
        get => _kind;
        set => Set(ref _kind, value);
    }

    /// <summary>Dev Portal'daki artifact kimliği.</summary>
    public string ArtifactId
    {
        get => _artifactId;
        set => Set(ref _artifactId, value);
    }

    /// <summary>Yüklenecek içeriğin klasörü. Depot yok, tek kök var.</summary>
    public string BuildRoot
    {
        get => _buildRoot;
        set => Set(ref _buildRoot, value);
    }

    /// <summary><see cref="BuildRoot"/> içine göreli, oyunun çalıştırılabilir dosyası.</summary>
    public string AppLaunch
    {
        get => _appLaunch;
        set => Set(ref _appLaunch, value);
    }

    public string AppArgs
    {
        get => _appArgs;
        set => Set(ref _appArgs, value);
    }

    /// <summary>
    /// Chunk verisinin saklandığı klasör. İsteğe bağlı — verilmezse araç veriyi bellekte
    /// tutuyor. Tutmanın faydası: sonraki yüklemelerde var olan veriyi tanıyıp daha
    /// küçük patch üretiyor.
    /// </summary>
    public string? CloudDir
    {
        get => _cloudDir;
        set => Set(ref _cloudDir, value);
    }

    /// <summary>
    /// Yükleme sonrası uygulanacak etiket (<c>Live</c> gibi). Boş = etiketleme yapma.
    /// Steam'deki <see cref="SubApp.SetLiveBranch"/>'in karşılığı, ama etiketleme
    /// <b>ayrı bir çağrı</b> (<c>LabelBinary</c>).
    /// </summary>
    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    /// <summary>Etiketin bağlandığı platform adı; <c>LabelBinary</c> bunu istiyor.</summary>
    public string Platform
    {
        get => _platform;
        set => Set(ref _platform, value);
    }

    /// <summary>Hedef sandbox (Dev / Stage / Live). Boşsa aracın varsayılanı geçerli.</summary>
    public string? SandboxId
    {
        get => _sandboxId;
        set => Set(ref _sandboxId, value);
    }

    /// <summary>
    /// Sürüm dizesi şablonu. Steam'de BuildID'yi Steam döndürdüğü için böyle bir şey
    /// yoktu; Epic'te sürümü biz üretiyoruz ve tekrarlarsak çakışıyor.
    /// Yer tutucular: <c>{app} {target} {kind} {artifact} {date} {time} {n}</c>
    /// </summary>
    public string BuildVersionTemplate
    {
        get => _buildVersionTemplate;
        set => Set(ref _buildVersionTemplate, value);
    }

    /// <summary>
    /// Kuru çalıştırma: <c>-DryRun</c> ile argümanlar, kimlik bilgileri ve backend
    /// durumu doğrulanır, hiçbir şey yüklenmez. Steam'deki preview build'in karşılığı.
    /// </summary>
    public bool Preview
    {
        get => _preview;
        set => Set(ref _preview, value);
    }
}
