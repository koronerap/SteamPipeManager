using System.Globalization;
using System.Text;

// SteamCMD yerine geçen test ikizi.
//
// Gerçek steamcmd'nin ölçülmüş davranışını taklit eder (bkz. docs/M0-FINDINGS.md):
//   * konsol çıktısının tamamını logs/console_log.txt'e zaman damgalarıyla artımlı yazar
//   * stdout'a yazdığını sonuna kadar tamponlar (canlı akış vermez)
//
// Hangi senaryonun oynatılacağı kendi klasöründeki scenario.txt'ten okunur; böylece
// SteamCmdRunner'ın verdiği komut satırına dokunmadan senaryo değiştirilebilir.

var baseDir = AppContext.BaseDirectory;
var scenarioFile = Path.Combine(baseDir, "scenario.txt");
var scenario = File.Exists(scenarioFile) ? File.ReadAllText(scenarioFile).Trim() : "success";

var logsDir = Path.Combine(baseDir, "logs");
Directory.CreateDirectory(logsDir);
var logPath = Path.Combine(logsDir, "console_log.txt");

var buffered = new StringBuilder();

void Emit(string line)
{
    var stamped = $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] {line}";

    // console_log.txt anında boşaltılır — gerçek steamcmd de böyle yapıyor.
    using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
    using (var writer = new StreamWriter(stream))
    {
        writer.WriteLine(stamped);
    }

    // stdout ise süreç bitene kadar tutulur.
    buffered.AppendLine(stamped);
}

void Flush()
{
    Console.Out.Write(buffered.ToString());
    Console.Out.Flush();
}

Emit("Client version: 9999999999");
Emit("Loading Steam API...");
Thread.Sleep(40);
Emit("OK");

switch (scenario)
{
    case "loginfail":
        Emit("Logging in user 'tester' to Steam Public...");
        Thread.Sleep(40);
        Emit("FAILED (Invalid Password)");
        Flush();
        return 5;

    case "ratelimit":
        Emit("Logging in user 'tester' to Steam Public...");
        Thread.Sleep(40);
        Emit("FAILED (Rate Limit Exceeded)");
        Flush();
        return 5;

    case "prompt":
        // Oturum düşmüş: gerçek steamcmd burada girdi bekleyip sonsuza kadar takılır.
        Emit("Logging in user 'tester' to Steam Public...");
        Thread.Sleep(40);
        Emit("password:");
        Thread.Sleep(60_000);
        Flush();
        return 0;

    case "stall":
        Emit("Logging in user 'tester' to Steam Public...");
        Emit("OK");
        Emit("Building depot 1001 ...");
        Thread.Sleep(60_000);
        Flush();
        return 0;

    case "buildfail":
        Emit("Logging in user 'tester' to Steam Public...");
        Emit("OK");
        Emit("Building depot 1001 ...");
        Thread.Sleep(40);
        Emit("ERROR! Failed to upload depot 1001");
        Flush();
        return 8;

    case "restart":
        // İlk kurulumdaki "güncellendim, yeniden başlat" durumu.
        // Senaryo kendini "success"e çevirir ki ikinci çalıştırma zamanlamadan
        // bağımsız olarak başarılı olsun.
        File.WriteAllText(scenarioFile, "success");
        Emit("[----] Update complete, launching...");
        Flush();
        return 7;

    default:
        Emit("Logging in user 'tester' to Steam Public...");
        Thread.Sleep(40);
        Emit("OK");
        Emit("Scanning content");
        Thread.Sleep(40);
        Emit("Building depot 1001 ...");
        Emit("Uploading content...");
        Thread.Sleep(40);
        Emit("Successfully finished appID 1000 build (BuildID 4242)");
        Emit("Unloading Steam API...");
        Flush();
        return 0;
}
