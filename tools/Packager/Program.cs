using System.Formats.Tar;
using System.IO.Compression;
using System.Security;
using System.Text;

// Kullanım:
//   Packager tar <kaynak klasör> <çıktı.tar.gz> <paketteki kök ad> [çalıştırılabilir göreli yol]...
//   Packager macapp <publish klasörü> <.app klasörü> <ürün adı> <çalıştırılabilir ad> <bundle id> <sürüm> <app.ico>

return args switch
{
    ["tar", var source, var output, var rootName, .. var executables] => Tar(source, output, rootName, executables),
    ["macapp", var publish, var bundle, var name, var executable, var id, var version, var icon] =>
        MacApp(publish, bundle, name, executable, id, version, icon),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: Packager tar <source> <out.tar.gz> <root> [exec...]");
    Console.Error.WriteLine("       Packager macapp <publish> <bundle.app> <name> <exe> <id> <version> <icon.ico>");
    return 2;
}

/// <summary>
/// Klasörü tar.gz'ye yazar. İzinler açıkça veriliyor: Windows'ta dosyaların Unix izni
/// yok; klasörler ve çalıştırılabilirler 0755, diğer dosyalar 0644.
/// </summary>
static int Tar(string source, string output, string rootName, string[] executables)
{
    var executable = new HashSet<string>(executables.Select(e => e.Replace('\\', '/')), StringComparer.Ordinal);
    const UnixFileMode directoryMode = (UnixFileMode)0b111_101_101;
    const UnixFileMode fileMode = (UnixFileMode)0b110_100_100;

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);

    using var file = File.Create(output);
    using var gzip = new GZipStream(file, CompressionLevel.Optimal);
    using var tar = new TarWriter(gzip, TarEntryFormat.Pax);

    var timestamp = DateTimeOffset.UtcNow;

    tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, rootName + "/") { Mode = directoryMode, ModificationTime = timestamp });

    foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var relative = Path.GetRelativePath(source, directory).Replace('\\', '/');
        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"{rootName}/{relative}/") { Mode = directoryMode, ModificationTime = timestamp });
    }

    foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
        var isExecutable = executable.Contains(relative);

        using var data = File.OpenRead(path);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"{rootName}/{relative}")
        {
            Mode = isExecutable ? directoryMode : fileMode,
            ModificationTime = timestamp,
            DataStream = data,
        });
    }

    // İlk çalıştırılabilir uygulamanın kendisi, mutlaka olmalı; diğerleri (ör. createdump)
    // yayın ayarına göre olmayabilir.
    if (executables.Length > 0 && !File.Exists(Path.Combine(source, executables[0])))
    {
        Console.Error.WriteLine($"Executable not found in {source}: {executables[0]}");
        return 1;
    }

    return 0;
}

/// <summary>
/// macOS uygulama paketi: Contents/MacOS'ta publish çıktısı, Contents/Resources'ta simge,
/// Contents/Info.plist. Finder ve Dock adı, simgeyi ve çalıştırılacak dosyayı buradan alıyor.
/// </summary>
static int MacApp(string publish, string bundle, string name, string executable, string id, string version, string icon)
{
    if (Directory.Exists(bundle))
    {
        Directory.Delete(bundle, recursive: true);
    }

    var contents = Path.Combine(bundle, "Contents");
    var macOs = Path.Combine(contents, "MacOS");
    var resources = Path.Combine(contents, "Resources");

    Directory.CreateDirectory(macOs);
    Directory.CreateDirectory(resources);

    foreach (var path in Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(macOs, Path.GetRelativePath(publish, path));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(path, target);
    }

    if (!File.Exists(Path.Combine(macOs, executable)))
    {
        Console.Error.WriteLine($"{executable} is not in {publish}");
        return 1;
    }

    File.WriteAllBytes(Path.Combine(resources, "app.icns"), IcnsFromIco(File.ReadAllBytes(icon)));
    File.WriteAllText(Path.Combine(contents, "Info.plist"), InfoPlist(name, executable, id, version), new UTF8Encoding(false));

    return 0;
}

/// <summary>
/// CFBundleVersion yalnızca noktalı sayılar kabul ediyor; önsürüm eki ("-test.1") ve
/// derleme kimliği ("+abc") atılıyor.
/// </summary>
static string BundleVersion(string version)
{
    var core = version.Split('+')[0].Split('-')[0];
    return core.Length > 0 ? core : "0.0.0";
}

static string InfoPlist(string name, string executable, string id, string version)
{
    static string X(string value) => SecurityElement.Escape(value)!;

    var bundleVersion = BundleVersion(version);

    return $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>CFBundleName</key>
            <string>{X(name)}</string>
            <key>CFBundleDisplayName</key>
            <string>{X(name)}</string>
            <key>CFBundleIdentifier</key>
            <string>{X(id)}</string>
            <key>CFBundleVersion</key>
            <string>{X(bundleVersion)}</string>
            <key>CFBundleShortVersionString</key>
            <string>{X(bundleVersion)}</string>
            <key>CFBundleExecutable</key>
            <string>{X(executable)}</string>
            <key>CFBundleIconFile</key>
            <string>app.icns</string>
            <key>CFBundlePackageType</key>
            <string>APPL</string>
            <key>CFBundleInfoDictionaryVersion</key>
            <string>6.0</string>
            <key>LSMinimumSystemVersion</key>
            <string>12.0</string>
            <key>LSApplicationCategoryType</key>
            <string>public.app-category.developer-tools</string>
            <key>NSHighResolutionCapable</key>
            <true/>
        </dict>
        </plist>

        """;
}

/// <summary>
/// Windows simgesindeki PNG görüntülerinden .icns üretir. icns, PNG'yi doğrudan
/// gömmeye izin veriyor; boyut başına bir tür kodu var.
/// </summary>
static byte[] IcnsFromIco(byte[] ico)
{
    var types = new Dictionary<int, string>
    {
        [16] = "icp4",
        [32] = "icp5",
        [64] = "icp6",
        [128] = "ic07",
        [256] = "ic08",
    };

    var count = BitConverter.ToUInt16(ico, 4);
    var chunks = new List<(string Type, byte[] Data)>();

    for (var i = 0; i < count; i++)
    {
        var entry = 6 + 16 * i;
        var width = ico[entry] == 0 ? 256 : ico[entry];
        var size = BitConverter.ToInt32(ico, entry + 8);
        var offset = BitConverter.ToInt32(ico, entry + 12);
        var data = ico.AsSpan(offset, size).ToArray();

        var isPng = data.Length > 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G';

        if (isPng && types.TryGetValue(width, out var type))
        {
            chunks.Add((type, data));
        }
    }

    if (chunks.Count == 0)
    {
        throw new InvalidDataException("The icon has no PNG images that icns can embed.");
    }

    using var stream = new MemoryStream();
    var total = 8 + chunks.Sum(c => 8 + c.Data.Length);

    stream.Write("icns"u8);
    stream.Write(BigEndian(total));

    foreach (var (type, data) in chunks)
    {
        stream.Write(Encoding.ASCII.GetBytes(type));
        stream.Write(BigEndian(8 + data.Length));
        stream.Write(data);
    }

    return stream.ToArray();
}

static byte[] BigEndian(int value)
{
    var bytes = BitConverter.GetBytes(value);

    if (BitConverter.IsLittleEndian)
    {
        Array.Reverse(bytes);
    }

    return bytes;
}
