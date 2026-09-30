using System.Text;

namespace SteamPipeManager.Core.Vdf;

/// <summary>
/// KeyValues ağacını ContentBuilder script'lerinin formatında yazar.
///
/// Format gerçek ContentBuilder kurulumlarındaki çalışan dosyalardan çıkarıldı: CRLF satır sonu,
/// tab girinti, anahtar ile değer arasında tek boşluk, BOM yok, son <c>}</c> sonrasında
/// satır sonu yok.
/// </summary>
public static class VdfWriter
{
    private const string LineEnding = "\r\n";

    /// <summary>BOM'suz UTF-8: referans dosyalarda BOM yok.</summary>
    public static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string Write(VdfNode root)
    {
        var sb = new StringBuilder();
        WriteNode(sb, root, indent: 0);
        return sb.ToString();
    }

    public static async Task WriteFileAsync(string path, VdfNode root, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, Write(root), FileEncoding, ct);
    }

    private static void WriteNode(StringBuilder sb, VdfNode node, int indent)
    {
        var pad = new string('\t', indent);

        if (!node.IsBlock)
        {
            sb.Append(pad).Append(Quote(node.Key)).Append(' ').Append(Quote(node.Value ?? ""));
            return;
        }

        sb.Append(pad).Append(Quote(node.Key)).Append(LineEnding);
        sb.Append(pad).Append('{');

        foreach (var child in node.Children)
        {
            sb.Append(LineEnding);
            WriteNode(sb, child, indent + 1);
        }

        sb.Append(LineEnding).Append(pad).Append('}');
    }

    /// <summary>
    /// Değerler olduğu gibi yazılır — ters bölü kaçışı <b>yapılmaz</b>, çünkü ayrıştırıcı da
    /// kaçış çözmez ve Windows yolları script'lerde ham haliyle duruyor.
    /// Çift tırnak değeri bozacağı için erken hata verilir.
    /// </summary>
    private static string Quote(string value)
    {
        if (value.Contains('"'))
        {
            throw new ArgumentException(
                $"A VDF value cannot contain a double quote: {value}", nameof(value));
        }

        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException(
                $"A VDF value cannot contain a line break: {value}", nameof(value));
        }

        return $"\"{value}\"";
    }
}
