using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SteamPipeManager.Core.Publishing;

/// <summary>
/// Bir sürecin stdout'unu geldikçe satır satır okur.
///
/// Linux'ta SteamCMD'nin canlı kaynağı bu: <c>console_log.txt</c> hiç yazılmıyor, stdout
/// ise tamponlanmadan geliyor (WSL'de Ubuntu 24.04 üzerinde ölçüldü). Windows'ta
/// tersi geçerli; orada <see cref="LogTail"/> kullanılıyor.
///
/// Girdi istemleri (<c>Steam Guard code:</c>) satır sonu olmadan basılıyor; satır
/// tamamlanmayı beklerse istem hiç görülmez ve süreç sonsuza kadar bekler. Bu yüzden
/// tamamlanmamış bir parça bilinen bir isteme benziyorsa hemen satır olarak veriliyor.
/// </summary>
public sealed partial class StreamLineReader(TextReader reader)
{
    private readonly StringBuilder _captured = new();

    /// <summary>En son yeni çıktı geldiği an; takılma bekçisi buna bakıyor.</summary>
    public DateTimeOffset LastGrowthAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Okunan çıktının tamamı (arşiv için).</summary>
    public string Captured
    {
        get
        {
            lock (_captured)
            {
                return _captured.ToString();
            }
        }
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new char[4096];
        var carry = new StringBuilder();

        while (true)
        {
            int read;

            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            LastGrowthAt = DateTimeOffset.UtcNow;

            lock (_captured)
            {
                _captured.Append(buffer, 0, read);
            }

            carry.Append(buffer, 0, read);

            var text = carry.ToString();
            var lastBreak = text.LastIndexOf('\n');

            if (lastBreak >= 0)
            {
                carry.Clear().Append(text, lastBreak + 1, text.Length - lastBreak - 1);

                foreach (var line in text[..lastBreak].Split('\n'))
                {
                    yield return line.TrimEnd('\r');
                }
            }

            if (carry.Length > 0 && IsPrompt(carry.ToString()))
            {
                var prompt = carry.ToString();
                carry.Clear();
                yield return prompt;
            }
        }

        if (carry.Length > 0)
        {
            yield return carry.ToString();
        }
    }

    /// <summary>
    /// Yanıt bekleyen bilinen istemler. Yalnızca bunlar erken veriliyor: sıradan bir satır
    /// parça parça gelirse (ör. "progress:" ile biten bir ara parça) bölünmesin.
    /// </summary>
    internal static bool IsPrompt(string partial) => PromptPattern().IsMatch(AnsiPattern().Replace(partial, ""));

    [GeneratedRegex(@"(password|steam guard|two-factor|authenticator|code)[^\n]*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PromptPattern();

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiPattern();
}
