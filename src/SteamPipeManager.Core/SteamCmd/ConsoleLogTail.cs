using System.Runtime.CompilerServices;
using System.Text;

namespace SteamPipeManager.Core.SteamCmd;

/// <summary>
/// SteamCMD'nin <c>logs/console_log.txt</c> dosyasını canlı takip eder.
///
/// M0'da ölçüldü: stdout pipe'a bağlıyken bloklar halinde tamponlanıyor ama bu dosya
/// iş sürerken artımlı olarak yazılıyor. Canlı ilerleme buradan okunur.
///
/// Dosya her SteamCMD başlangıcında sıfırlandığı için küçülme algılanır ve baştan okunur.
/// </summary>
public sealed class ConsoleLogTail(string filePath, TimeSpan? pollInterval = null)
{
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromMilliseconds(250);

    public string FilePath { get; } = filePath;

    /// <summary>En son okunan baytın konumu; donma dedektörü bunun artışını izler.</summary>
    public long Position { get; private set; }

    /// <summary>Dosyaya en son yeni içerik geldiği an.</summary>
    public DateTimeOffset LastGrowthAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// <paramref name="stopWhen"/> true dönene kadar (yani süreç bitene kadar) satır üretir;
    /// süreç bittikten sonra kalan içerik son bir kez okunur.
    /// </summary>
    public async IAsyncEnumerable<string> ReadLinesAsync(
        Func<bool> stopWhen,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var carry = new StringBuilder();
        var finished = false;

        while (!ct.IsCancellationRequested)
        {
            // Süreç bitmişse bir tur daha okunur; son yazılanlar kaçmasın.
            var lastPass = finished;

            foreach (var line in ReadNewLines(carry))
            {
                yield return line;
            }

            if (lastPass)
            {
                break;
            }

            if (stopWhen())
            {
                finished = true;
                continue;
            }

            try
            {
                await Task.Delay(_poll, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (carry.Length > 0)
        {
            yield return carry.ToString();
        }
    }

    private IEnumerable<string> ReadNewLines(StringBuilder carry)
    {
        if (!File.Exists(FilePath))
        {
            yield break;
        }

        FileStream stream;

        try
        {
            // SteamCMD yazmaya devam ederken kilitlememek için tam paylaşım.
            stream = new FileStream(
                FilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException)
        {
            // Anlık kilit çakışması; bir sonraki turda tekrar denenir.
            yield break;
        }

        using (stream)
        {
            if (stream.Length < Position)
            {
                // Dosya sıfırlanmış (yeni SteamCMD çalıştırması) — baştan oku.
                Position = 0;
                carry.Clear();
            }

            if (stream.Length == Position)
            {
                yield break;
            }

            stream.Seek(Position, SeekOrigin.Begin);

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            var chunk = reader.ReadToEnd();

            Position = stream.Length;
            LastGrowthAt = DateTimeOffset.UtcNow;

            carry.Append(chunk);
            var buffered = carry.ToString();
            carry.Clear();

            var lastBreak = buffered.LastIndexOf('\n');

            if (lastBreak < 0)
            {
                // Henüz tam satır yok; tamamlanmasını bekle.
                carry.Append(buffered);
                yield break;
            }

            // Son satır sonrası kalan parça yarım olabilir, sonraki tura devredilir.
            carry.Append(buffered[(lastBreak + 1)..]);

            foreach (var line in buffered[..lastBreak].Split('\n'))
            {
                yield return line.TrimEnd('\r');
            }
        }
    }
}
