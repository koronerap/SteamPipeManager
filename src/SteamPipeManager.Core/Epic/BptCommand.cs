namespace SteamPipeManager.Core.Epic;

/// <summary>
/// BuildPatchTool'a verilecek tek bir çağrı.
///
/// Client secret bilerek argümanların dışında tutuluyor: aracın kendi log'u secret'ı
/// maskeliyor (ölçüldü), ama komut satırı <b>işletim sistemi süreç listesinde</b>
/// görünüyor. BPT'nin <c>-ClientSecretEnvVar</c> parametresi tam bu yüzden var ve
/// secret çocuk sürece yalnızca ortam değişkeni olarak geçiriliyor.
/// </summary>
public sealed class BptCommand
{
    /// <summary>Secret'ın taşındığı ortam değişkeni. Yalnızca çocuk sürecin ortamında var.</summary>
    public const string SecretEnvironmentVariable = "SPM_BPT_CLIENT_SECRET";

    private readonly List<string> _arguments = [];

    public BptCommand(string mode)
    {
        Mode = mode;
        _arguments.Add($"-mode={mode}");
    }

    public string Mode { get; }

    /// <summary>Çocuk sürece verilecek secret; null ise ortam değişkeni hiç kurulmuyor.</summary>
    public string? ClientSecret { get; private set; }

    public BptCommand Add(string name, string? value)
    {
        if (value is not null)
        {
            _arguments.Add($"-{name}={value}");
        }

        return this;
    }

    /// <summary>Değer almayan bayraklar (<c>-DryRun</c> gibi).</summary>
    public BptCommand AddFlag(string name)
    {
        _arguments.Add($"-{name}");
        return this;
    }

    /// <summary>
    /// Kimlik bilgilerini ekler. Secret argüman listesine <b>girmiyor</b>; yerine
    /// <c>-ClientSecretEnvVar</c> adı yazılıyor ve değer ortamdan okunuyor.
    /// </summary>
    public BptCommand WithCredentials(
        string organizationId,
        string productId,
        string artifactId,
        string clientId,
        string clientSecret)
    {
        Add("OrganizationId", organizationId);
        Add("ProductId", productId);
        Add("ArtifactId", artifactId);
        Add("ClientId", clientId);
        Add("ClientSecretEnvVar", SecretEnvironmentVariable);

        ClientSecret = clientSecret;

        return this;
    }

    /// <summary>
    /// Log'un yazılacağı dosya. BPT bir Unreal programı olduğu için <c>-abslog</c>
    /// destekliyor (ölçüldü) — bu sayede her çalıştırma kendi log dosyasını alıyor.
    ///
    /// Varsayılan davranışta bütün çalıştırmalar ortak
    /// <c>%LocalAppData%\BuildPatchTool\Saved\Logs\BuildPatchTool.log</c> dosyasını
    /// paylaşıyor; Steam tarafında tam olarak bu paylaşım yüzünden bir çalıştırmanın
    /// satırları başka bir hesaba mal edilmişti. Kendi dosyamızı vererek o hata
    /// sınıfını tümden kapatıyoruz.
    /// </summary>
    public BptCommand WithLogFile(string path) => Add("abslog", path);

    public IReadOnlyList<string> Arguments => _arguments;

    /// <summary>
    /// Kullanıcıya gösterilebilecek komut satırı. Secret zaten listede olmadığı için
    /// maskelenecek bir şey yok; yine de ne çalıştırılacağı birebir görünüyor.
    /// </summary>
    public string ToDisplayString(string executablePath) =>
        $"\"{executablePath}\" {string.Join(" ", _arguments.Select(Quote))}";

    private static string Quote(string argument) =>
        argument.Contains(' ') && !argument.EndsWith('"')
            ? QuoteValue(argument)
            : argument;

    /// <summary>Yalnızca değer kısmı tırnaklanır; <c>-Key="a b"</c> biçimi korunur.</summary>
    private static string QuoteValue(string argument)
    {
        var separator = argument.IndexOf('=');

        return separator < 0
            ? $"\"{argument}\""
            : $"{argument[..separator]}=\"{argument[(separator + 1)..]}\"";
    }
}
