using System.Diagnostics;
using System.Text;

namespace SteamCmdProbe;

/// <summary>
/// Ölçtüğü soru: SteamCMD, şifre istemini beklemeden stdin'e yazılan satırı okur mu?
///
/// M0'da çıktının tamponlandığını biliyoruz — istem bize zamanında ulaşmıyor. Ama girdi
/// yönü ayrı bir kanal; şifreyi önden yazarsak SteamCMD ihtiyaç duyduğunda onu okuyabilir.
/// Doğrulama için geçerli bir şifreye gerek yok: süreç "FAILED (Invalid Password)" ile
/// biterse girdiyi tüketmiş demektir. Donarsa yöntem çalışmıyor demektir.
/// </summary>
internal static class StdinLoginProbe
{
    public static async Task<int> RunAsync(string exe, string user, string password)
    {
        Console.WriteLine($"--- stdin login testi: {user} ---\n");

        var psi = new ProcessStartInfo(exe, $"+login {user} +quit")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi)!;
        var stopwatch = Stopwatch.StartNew();

        // İstem beklenmeden yazılıyor: SteamCMD hazır olduğunda okuyacak.
        await process.StandardInput.WriteLineAsync(password);
        await process.StandardInput.FlushAsync();
        Console.WriteLine($"[{stopwatch.ElapsedMilliseconds,6}ms] şifre stdin'e yazıldı");

        // Steam Guard istenirse ikinci satır da hazır beklesin.
        await process.StandardInput.WriteLineAsync("00000");
        await process.StandardInput.FlushAsync();
        Console.WriteLine($"[{stopwatch.ElapsedMilliseconds,6}ms] sahte guard kodu yazıldı");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var exited = await WaitAsync(process, TimeSpan.FromSeconds(60));

        if (!exited)
        {
            Console.WriteLine($"\n[{stopwatch.ElapsedMilliseconds,6}ms] DONDU — stdin okunmadı");
            process.Kill(entireProcessTree: true);
        }

        var output = await outputTask;
        Console.WriteLine($"\n--- çıktı ---\n{output}");
        Console.WriteLine($"--- exit: {(exited ? process.ExitCode.ToString() : "yok")} , süre {stopwatch.Elapsed.TotalSeconds:F1}s ---");
        Console.WriteLine($"--- SONUÇ: stdin {(exited ? "OKUNDU (yöntem çalışıyor)" : "OKUNMADI (yöntem çalışmıyor)")} ---");

        return exited ? 0 : 2;
    }

    private static async Task<bool> WaitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
