using SteamPipeManager.Core.Localization;
using SteamPipeManager.Core.Publishing;

namespace SteamPipeManager.Core.Epic;

/// <summary>
/// Bir Epic profilinin BuildPatchTool kimlik bilgileri.
///
/// <see cref="ClientSecret"/> burada yalnızca çağrı süresince taşınıyor; kalıcı
/// saklama şifreli ayrı bir depoda olacak ve <c>profiles.json</c>'a hiç yazılmayacak.
/// </summary>
public sealed record EpicCredentials(
    string OrganizationId,
    string ProductId,
    string ArtifactId,
    string ClientId,
    string ClientSecret);

/// <summary>
/// Epic kimlik bilgilerinin hâlâ çalışıp çalışmadığını <c>UploadBinary -DryRun</c> ile
/// kontrol eder.
///
/// Ölçüldü (docs/E0-BPT-FINDINGS.md, Bulgu 6): <c>-DryRun</c> argümanları, kimlik
/// bilgilerini ve backend durumunu <b>hiçbir şey yüklemeden</b> doğruluyor ve sonucu
/// tek satırlık <c>[DRYRUN][RESULT] PASS|FAIL</c> kararıyla bildiriyor. Steam'de ayrı
/// bir oturum kontrolü mekanizması yazmak zorunda kalmıştık; burada aracın kendisi
/// bunu sunuyor.
/// </summary>
public sealed class BptSessionService(BptInstallation installation)
{
    public BptInstallation Installation { get; } = installation;

    public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Kimlik bilgilerini doğrular.
    /// </summary>
    /// <param name="credentials">Kontrol edilecek kimlik bilgileri.</param>
    /// <param name="logPath">Bu çalıştırmanın log dosyası.</param>
    /// <param name="buildRoot">
    /// Doğrulanacak içerik klasörü. Verilmezse geçici, tek dosyalık bir klasör
    /// kullanılır: ölçümde görüldü ki BPT argüman doğrulamasını kimlik doğrulamadan
    /// <b>önce</b> yapıyor, yani geçerli görünen bir build kökü olmadan kimlik
    /// bilgilerine hiç sıra gelmiyor. Hiçbir şey yüklenmediği için bu zararsız.
    /// </param>
    public async Task<SessionCheckResult> CheckAsync(
        EpicCredentials credentials,
        string logPath,
        string? buildRoot = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        if (!Installation.Exists)
        {
            return new SessionCheckResult(
                SessionState.CheckFailed,
                Loc.T("Epic.ToolNotFoundAt", Installation.ExecutablePath));
        }

        var scratch = buildRoot is null ? CreateScratchRoot() : null;
        var root = buildRoot ?? scratch!;

        try
        {
            var command = new BptCommand("UploadBinary")
                .WithCredentials(
                    credentials.OrganizationId,
                    credentials.ProductId,
                    credentials.ArtifactId,
                    credentials.ClientId,
                    credentials.ClientSecret)
                .Add("BuildRoot", root)
                .Add("BuildVersion", "0.0.0-check")
                .Add("AppLaunch", ScratchExecutableName)
                .Add("AppArgs", "")
                .AddFlag("DryRun");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CheckTimeout);

            var runner = new BptRunner(Installation) { StallTimeout = CheckTimeout };

            BptRunResult result;

            try
            {
                result = await runner.RunAsync(command, logPath, ct: timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new SessionCheckResult(SessionState.CheckFailed, ex.Message);
            }

            return Interpret(result);
        }
        finally
        {
            if (scratch is not null)
            {
                DeleteQuietly(scratch);
            }
        }
    }

    /// <summary>
    /// Çalıştırma sonucunu oturum durumuna çevirir.
    ///
    /// Ayrım önemli: <b>kimlik reddedildi</b> kullanıcının düzeltebileceği bir şey
    /// (yeni secret girmesi gerekir), <b>kontrol başarısız</b> ise bizim tarafımızda
    /// bir sorun (araç yok, zaman aşımı, beklenmedik çıkış). İkisini karıştırmak
    /// kullanıcıyı boşuna kimlik bilgisi girmeye yönlendirir.
    /// </summary>
    private static SessionCheckResult Interpret(BptRunResult result)
    {
        if (result.TimedOut)
        {
            return new SessionCheckResult(SessionState.CheckFailed, Loc.T("Epic.CheckTimeout"));
        }

        if (result.Cancelled)
        {
            return new SessionCheckResult(SessionState.Unknown, Loc.T("Epic.CheckCancelled"));
        }

        if (result.Events.Any(e => e.Kind == BptEventKind.DryRunPassed))
        {
            return new SessionCheckResult(SessionState.Active, Loc.T("Session.Active"));
        }

        if (result.Events.Any(e => e.Kind == BptEventKind.AuthenticationFailed))
        {
            return new SessionCheckResult(SessionState.LoginRequired, Loc.T("Epic.CredentialsRejected"));
        }

        // Zorunlu bir parametre eksikse sorun kimlik bilgilerinde değil, bizim
        // kurduğumuz çağrıda ya da hedef tanımında.
        if (result.Events.FirstOrDefault(e => e.Kind == BptEventKind.ValidationFailed) is { } invalid)
        {
            return new SessionCheckResult(SessionState.CheckFailed, invalid.Message);
        }

        if (result.Failure is { ExitReason: { Length: > 0 } reason })
        {
            return new SessionCheckResult(
                SessionState.CheckFailed, Loc.T("Epic.ToolExited", reason));
        }

        // Karar satırı hiç görülmediyse emin olamayız; build'i riske atmaktansa
        // kontrol başarısız sayılır.
        return new SessionCheckResult(
            SessionState.CheckFailed, Loc.T("Epic.CheckNotVerified", result.ExitCode));
    }

    private const string ScratchExecutableName = "Game.exe";

    /// <summary>
    /// Yalnızca doğrulama için tek dosyalık geçici bir build kökü. Hiçbir zaman
    /// yüklenmiyor: <c>-DryRun</c> chunk bile üretmiyor.
    ///
    /// Dosyanın <b>boş olmaması şart</b> — ölçüldü: BPT "BuildRoot must not be an empty
    /// directory or a directory tree containing only empty files/directories" diyerek
    /// argüman doğrulamasında duruyor ve kimlik bilgilerine hiç sıra gelmiyordu.
    /// </summary>
    private static string CreateScratchRoot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"spm_epic_check_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, ScratchExecutableName),
            "Steam Pipe Manager credential check placeholder.");

        return directory;
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Geçici klasör temizlenemezse kontrol sonucu etkilenmiyor.
        }
    }
}
