using System.Diagnostics;
using System.Text;
using SteamPipeManager.Core.SteamCmd;

// M0 doğrulama aracı.
//
// Cevaplaması gereken soru: SteamCMD yönlendirilmiş stdout üzerinden prompt'larını gösteriyor
// ve yönlendirilmiş stdin'den okuyor mu? Prompt'lar satır sonu ile bitmediği için ReadLine()
// çalışmaz; bu araç akışı karakter karakter okuyup ne geldiğini olduğu gibi raporlar.
//
// Kullanım:
//   dotnet run --project tools/SteamCmdProbe -- bootstrap
//   dotnet run --project tools/SteamCmdProbe -- login <kullanıcı> [gönderilecek-cevap]

var installDir = Path.Combine(Path.GetTempPath(), "spm-probe-steamcmd");
var mode = args.Length > 0 ? args[0] : "bootstrap";

Console.OutputEncoding = Encoding.UTF8;

var provisioner = new SteamCmdProvisioner();
var progress = new Progress<ProvisionProgress>(p =>
    Console.WriteLine($"  [{p.Stage}]{(p.Fraction is { } f ? $" {f:P0}" : "")}"));

Console.WriteLine($"SteamCMD kurulumu: {installDir}");
var exe = await provisioner.EnsureInstalledAsync(installDir, progress);
Console.WriteLine($"exe: {exe}\n");

switch (mode)
{
    case "bootstrap":
        await RunAsync(exe, "+quit", stdinReply: null);
        break;

    case "login":
        var user = args.Length > 1 ? args[1] : "spm_probe_nonexistent_account";
        var reply = args.Length > 2 ? args[2] : "probe-dummy-password";
        await RunAsync(exe, $"+login {user}", reply);
        break;

    // Sifreyi istem beklemeden stdin'e yazip steamcmd'nin okuyup okumadigini olcer.
    // Cikti tamponlu (M0 Bulgu 2) ama girdi tarafi tamponsuz olabilir; bu test onu belirler.
    case "stdin-login":
        {
            var stdinUser = args.Length > 1 ? args[1] : "spm_probe_no_such_user_9f2a";
            var stdinPass = args.Length > 2 ? args[2] : "definitely-wrong-password";
            return await SteamCmdProbe.StdinLoginProbe.RunAsync(exe, stdinUser, stdinPass);
        }

    case "run":
        await RunAsync(exe, string.Join(' ', args.Skip(1)), stdinReply: null);
        break;


    default:
        Console.WriteLine("Modlar: bootstrap | login | run <args...>");
        return 1;
}

return 0;

static async Task RunAsync(string exe, string arguments, string? stdinReply)
{
    Console.WriteLine($"--- çalıştırılıyor: steamcmd.exe {arguments} ---\n");

    var psi = new ProcessStartInfo(exe, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = Path.GetDirectoryName(exe)!,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    using var process = Process.Start(psi)
        ?? throw new InvalidOperationException("steamcmd başlatılamadı.");

    var stopwatch = Stopwatch.StartNew();
    var pending = new StringBuilder();
    var repliesSent = 0;
    var sawPrompt = false;

    var stderrTask = Task.Run(async () =>
    {
        var text = await process.StandardError.ReadToEndAsync();
        if (text.Length > 0)
        {
            Console.WriteLine($"\n[stderr] {text}");
        }
    });

    // Karakter karakter okuma: satır sonu ile bitmeyen prompt'ları görebilmenin tek yolu.
    var buffer = new char[1];

    while (await process.StandardOutput.ReadAsync(buffer, 0, 1) > 0)
    {
        var c = buffer[0];

        if (c == '\n')
        {
            Console.WriteLine($"[{stopwatch.ElapsedMilliseconds,6}ms] LINE  | {pending}");
            pending.Clear();
            continue;
        }

        if (c == '\r')
        {
            continue;
        }

        pending.Append(c);

        if (LooksLikePrompt(pending.ToString()) is { } promptKind)
        {
            sawPrompt = true;
            Console.WriteLine($"[{stopwatch.ElapsedMilliseconds,6}ms] PROMPT| {pending}   <-- {promptKind}");

            if (stdinReply is not null && repliesSent < 3)
            {
                repliesSent++;
                Console.WriteLine($"[{stopwatch.ElapsedMilliseconds,6}ms] SEND  | (stdin'e cevap #{repliesSent} yazılıyor)");
                await process.StandardInput.WriteLineAsync(stdinReply);
                await process.StandardInput.FlushAsync();
            }

            pending.Clear();
        }
    }

    if (pending.Length > 0)
    {
        Console.WriteLine($"[{stopwatch.ElapsedMilliseconds,6}ms] TAIL  | {pending}");
    }

    await stderrTask;
    await process.WaitForExitAsync();

    Console.WriteLine($"\n--- exit code: {process.ExitCode}, süre: {stopwatch.Elapsed.TotalSeconds:F1}s ---");
    Console.WriteLine($"--- prompt görüldü mü: {(sawPrompt ? "EVET" : "hayır")}, gönderilen cevap: {repliesSent} ---");
}

// SteamCMD'nin prompt'ları satır sonu ile bitmez; kısmi satır bu desenlerden birine
// uyuyorsa girdi bekleniyor demektir.
static string? LooksLikePrompt(string partialLine)
{
    var text = partialLine.TrimEnd();

    if (text.EndsWith("password:", StringComparison.OrdinalIgnoreCase))
    {
        return "PASSWORD";
    }

    if (text.EndsWith("Steam Guard code:", StringComparison.OrdinalIgnoreCase) ||
        text.EndsWith("Two-factor code:", StringComparison.OrdinalIgnoreCase) ||
        text.EndsWith("authenticator app:", StringComparison.OrdinalIgnoreCase))
    {
        return "STEAM_GUARD";
    }

    return null;
}
