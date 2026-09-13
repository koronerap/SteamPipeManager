using System.Text;
using System.Text.RegularExpressions;

namespace BptProbe;

/// <summary>
/// Raporu göndermeden önce kimliğe dair ne varsa maskeler.
///
/// Probe'u çalıştıran kişi kendi makinesinde çalıştırıyor; raporda kullanıcı adı,
/// klasör yolları ve (yanlışlıkla gerçek kimlik bilgisiyle çalıştırılırsa) client
/// secret görünebilir. Hiçbiri bize lazım değil.
/// </summary>
public static partial class Redactor
{
    /// <summary>Yolun yalnızca son iki parçası bırakılır; gerisi maskelenir.</summary>
    public static string Path(string value)
    {
        var parts = value.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length <= 2
            ? value
            : @"…\" + string.Join('\\', parts[^2..]);
    }

    public static string Text(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var result = value;

        // Kimlik bilgisi taşıyan parametreler: adı kalsın, değeri gitsin. Parametrenin
        // başındaki tire de korunuyor, yoksa rapordaki komut satırı yanlış görünüyor.
        result = SecretParameter().Replace(result, m => $"-{m.Groups["key"].Value}=<maskelendi>");

        // Kullanıcı profili yolları: C:\Users\jdoe\... → C:\Users\<kullanıcı>\...
        result = UserProfilePath().Replace(result, @"$1\<kullanıcı>\");

        // Makinedeki kullanıcı adı metnin başka yerinde de geçebilir.
        var userName = Environment.UserName;

        if (userName.Length > 2)
        {
            result = result.Replace(userName, "<kullanıcı>", StringComparison.OrdinalIgnoreCase);
        }

        // Uzun onaltılık diziler (token, id) kısaltılır.
        result = LongHex().Replace(result, m => $"<{m.Value.Length}-karakter-id>");

        return result;
    }

    /// <summary>Değeri gizlenecek parametreler; ad eşleşmesi büyük/küçük harf duyarsız.</summary>
    [GeneratedRegex(
        @"-(?<key>ClientSecret|ClientId|OrganizationId|ProductId|ArtifactId|SandboxId|DeploymentId)=\S+",
        RegexOptions.IgnoreCase)]
    private static partial Regex SecretParameter();

    [GeneratedRegex(@"([A-Za-z]:\\Users)\\[^\\\s]+\\", RegexOptions.IgnoreCase)]
    private static partial Regex UserProfilePath();

    [GeneratedRegex(@"\b[0-9a-f]{24,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex LongHex();
}

/// <summary>Rapor metnini biriktirir.</summary>
public sealed class Report
{
    private readonly StringBuilder _text = new();

    public void Section(string title)
    {
        _text.AppendLine();
        _text.AppendLine(new string('=', 78));
        _text.AppendLine(title);
        _text.AppendLine(new string('=', 78));
    }

    public void Line(string line) => _text.AppendLine(line);

    public override string ToString() => _text.ToString();
}

/// <summary>
/// Klasörün dosya listesi ve boyutları. BPT çalıştıktan sonra yanına log dosyası
/// bırakıp bırakmadığını anlamak için önce ve sonra karşılaştırılır.
/// </summary>
public sealed class DirectorySnapshot
{
    private readonly Dictionary<string, long> _files = [];

    /// <summary>
    /// Fotoğrafa alınacak en fazla dosya. BPT'nin kendi klasörü küçük; araç yanlışlıkla
    /// devasa bir ağacın (ör. Program Files) içine yönlendirilirse probe kilitlenmesin.
    /// </summary>
    private const int FileLimit = 5000;

    public static DirectorySnapshot Take(string directory)
    {
        var snapshot = new DirectorySnapshot();

        if (!Directory.Exists(directory))
        {
            return snapshot;
        }

        foreach (var file in Directory
                     .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Take(FileLimit))
        {
            try
            {
                snapshot._files[file] = new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // Okunamayan dosya atlanır; fotoğrafın eksik olması sorun değil.
            }
        }

        return snapshot;
    }

    public IReadOnlyList<string> DifferenceFrom(DirectorySnapshot earlier)
    {
        var changes = new List<string>();

        foreach (var (path, size) in _files)
        {
            if (!earlier._files.TryGetValue(path, out var previous))
            {
                changes.Add($"YENİ     {Redactor.Path(path)}  ({size:N0} bayt)");
            }
            else if (previous != size)
            {
                changes.Add($"DEĞİŞTİ  {Redactor.Path(path)}  ({previous:N0} → {size:N0} bayt)");
            }
        }

        changes.Sort(StringComparer.Ordinal);
        return changes;
    }
}
