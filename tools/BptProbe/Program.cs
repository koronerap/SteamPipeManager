using System.Diagnostics;
using System.Text;
using BptProbe;

// E0 ölçüm aracı — BuildPatchTool'un gerçekte nasıl davrandığını ölçer.
//
// Steam tarafında M0'da öğrendiğimiz ders: aracın çıktısını *varsaymak* mimariyi yanlış
// kurdurur. SteamCMD'nin stdout'unun bloklu tamponlandığını, buna karşılık console_log.txt'nin
// canlı yazıldığını ancak ölçerek bulmuştuk ve bütün canlı ilerleme tasarımı ona dayandı.
// Bu araç aynı soruları BPT için sorar.
//
// Kimlik bilgisi gerekmez, hesaba giriş yapılmaz, hiçbir ürüne dokunulmaz: yalnızca yardım
// metinleri ve kasten geçersiz kimlikle bir çağrı çalıştırılır.
//
// Kullanım:
//   dotnet run --project tools/BptProbe -- "C:\BuildPatchTool_1.6.0\Engine\Binaries\Win64\BuildPatchTool.exe"
//   dotnet run --project tools/BptProbe -- <exe> --out rapor.txt

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Kullanım: BptProbe <BuildPatchTool.exe yolu> [--out <rapor.txt>]");
    return 1;
}

var exe = args[0];

if (!File.Exists(exe))
{
    Console.Error.WriteLine($"Bulunamadı: {exe}");
    return 1;
}

var outIndex = Array.IndexOf(args, "--out");
var reportPath = outIndex >= 0 && outIndex + 1 < args.Length
    ? args[outIndex + 1]
    : Path.Combine(Directory.GetCurrentDirectory(), "bpt-probe-report.txt");

var report = new Report();
var runner = new ProbeRunner(exe, report);

report.Section("Ortam");
report.Line($"exe          : {Redactor.Path(exe)}");
report.Line($"dosya boyutu : {new FileInfo(exe).Length:N0} bayt");
report.Line($"değiştirilme : {File.GetLastWriteTimeUtc(exe):yyyy-MM-dd HH:mm:ss}Z");
report.Line($"işletim sist.: {Environment.OSVersion}");
report.Line($"probe zamanı : {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z");

// BPT'nin yanına ne bıraktığını görebilmek için önce klasörün fotoğrafı çekilir.
var toolDir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
var before = DirectorySnapshot.Take(toolDir);

// --- 1) Argümansız çalıştırma: kullanım metni ve varsayılan davranış ---
await runner.RunAsync("argümansız", []);

// --- 2) Genel yardım: mod listesi buradan çıkmalı ---
var generalHelp = await runner.RunAsync("genel yardım", ["-help"]);

// --- 3) Her modun kendi yardımı: parametrelerin tam listesi ---
// Modlar tahmin edilmiyor, aracın kendi çıktısından okunuyor. Dokümantasyondaki
// isimler sürümler arasında değişmiş (1.6'daki LabelBuild, 1.8.8'de LabelBinary);
// listeyi araca sordurmak bu sınıf hataları tümden ortadan kaldırıyor.
var modes = ModeList.Parse(generalHelp.Stdout);

report.Section("Keşfedilen modlar");
report.Line(modes.Count == 0
    ? "(mod listesi ayrıştırılamadı — genel yardım çıktısına bakılmalı)"
    : string.Join(Environment.NewLine, modes.Select(m => "  " + m)));

foreach (var mode in modes)
{
    await runner.RunAsync($"mod yardımı: {mode}", [$"-mode={mode}", "-help"]);
}

// --- 4) Geçersiz kimlikle gerçek bir çağrı ---
// Amaç: kimlik doğrulama hatasının biçimi, exit code'u ve stdout'un yönlendirildiğinde
// tamponlanıp tamponlanmadığı. Kimlik bilgileri kasten uydurma; hiçbir hesaba erişilmez.
var fakeBuildRoot = Path.Combine(Path.GetTempPath(), $"bpt_probe_root_{Guid.NewGuid():N}");
var fakeCloudDir = Path.Combine(Path.GetTempPath(), $"bpt_probe_cloud_{Guid.NewGuid():N}");
Directory.CreateDirectory(fakeBuildRoot);
Directory.CreateDirectory(fakeCloudDir);
await File.WriteAllTextAsync(Path.Combine(fakeBuildRoot, "Game.exe"), "not a real binary");

await runner.RunAsync("geçersiz kimlik (UploadBinary)",
[
    "-mode=UploadBinary",
    "-OrganizationId=probe-org",
    "-ProductId=probe-product",
    "-ArtifactId=probe-artifact",
    "-ClientId=probe-client",
    "-ClientSecret=probe-secret-not-real",
    $"-BuildRoot={fakeBuildRoot}",
    $"-CloudDir={fakeCloudDir}",
    "-BuildVersion=0.0.0-probe",
    "-AppLaunch=Game.exe",
    "-AppArgs=",
]);

// --- 5) Eksik zorunlu parametre: doğrulama hatalarının biçimi ---
await runner.RunAsync("eksik parametre", ["-mode=UploadBinary", "-OrganizationId=probe-org"]);

// --- 6) Offline chunk çalıştırması: canlı ilerleme nereden okunuyor? ---
// İsteğe bağlı çünkü birkaç yüz MB geçici veri üretip birkaç dakika sürebiliyor.
// Ama asıl mimari sorunun cevabı burada: stdout mu, log dosyası mı?
var offlineIndex = Array.IndexOf(args, "--offline");

if (offlineIndex >= 0)
{
    var sizeMb = offlineIndex + 1 < args.Length && int.TryParse(args[offlineIndex + 1], out var parsed)
        ? parsed
        : 256;

    await OfflineRun.RunAsync(exe, sizeMb, report);
}
else
{
    report.Section("Offline çalıştırma");
    report.Line("Atlandı. Canlı ilerleme ölçümü için: --offline [MB]");
}

// --- 6) Çalıştırmalardan sonra klasöre ne düştü? ---
// SteamCMD'de canlı ilerlemenin kaynağı stdout değil, yanına yazdığı log dosyasıydı.
// BPT'nin böyle bir dosyası varsa aynı yol burada da açılır.
report.Section("Araç klasöründe oluşan/değişen dosyalar");

var after = DirectorySnapshot.Take(toolDir);
var changes = after.DifferenceFrom(before);

if (changes.Count == 0)
{
    report.Line("(değişiklik yok — BPT yanına dosya yazmıyor gibi görünüyor)");
}
else
{
    foreach (var change in changes)
    {
        report.Line("  " + change);
    }
}

// Bilinen diğer aday konumlar da kontrol edilir.
report.Section("Diğer aday log konumları");

foreach (var candidate in new[]
         {
             Path.Combine(toolDir, "Saved", "Logs"),
             Path.Combine(toolDir, "..", "..", "..", "Saved", "Logs"),
             Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BuildPatchTool"),
             Path.Combine(Path.GetTempPath(), "BuildPatchTool"),
         })
{
    var full = Path.GetFullPath(candidate);
    report.Line($"{(Directory.Exists(full) ? "VAR " : "yok ")} {Redactor.Path(full)}");

    if (!Directory.Exists(full))
    {
        continue;
    }

    foreach (var file in Directory.EnumerateFiles(full).Take(10))
    {
        report.Line($"       {Path.GetFileName(file)}  ({new FileInfo(file).Length:N0} bayt)");
    }
}

try
{
    Directory.Delete(fakeBuildRoot, recursive: true);
    Directory.Delete(fakeCloudDir, recursive: true);
}
catch (IOException)
{
    // Temizlik başarısız olsa da rapor geçerli.
}

report.Section("Özet");
report.Line(runner.Summary());

await File.WriteAllTextAsync(reportPath, report.ToString());

Console.WriteLine();
Console.WriteLine($"Rapor yazıldı: {reportPath}");
Console.WriteLine();
Console.WriteLine("Rapordaki kimlik/yol bilgileri maskelendi, yine de göndermeden önce bir göz at.");

return 0;
