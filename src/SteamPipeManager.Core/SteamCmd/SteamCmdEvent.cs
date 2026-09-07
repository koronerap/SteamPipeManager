namespace SteamPipeManager.Core.SteamCmd;

public enum SteamCmdEventKind
{
    /// <summary>Sınıflandırılmamış satır; log panelinde düz metin olarak gösterilir.</summary>
    Info,

    LoginStarted,
    LoginSucceeded,
    LoginFailed,

    /// <summary>SteamCMD şifre bekliyor.</summary>
    NeedsInteraction,

    /// <summary>Steam Guard kodu isteniyor (e-posta ya da authenticator kodu).</summary>
    NeedsGuardCode,

    /// <summary>Telefondaki Steam uygulamasından onay bekleniyor; yazılacak kod yok.</summary>
    NeedsMobileConfirmation,

    BuildStarted,
    ScanningContent,
    UploadingContent,
    DepotProgress,

    BuildSucceeded,
    Error,
}

/// <summary>
/// <c>console_log.txt</c>'ten ayrıştırılmış tek bir olay.
/// </summary>
public sealed record SteamCmdEvent(
    SteamCmdEventKind Kind,
    string Message,
    string RawLine,
    DateTimeOffset? Timestamp = null)
{
    /// <summary>Yalnızca <see cref="SteamCmdEventKind.BuildSucceeded"/> olaylarında dolu.</summary>
    public uint? BuildId { get; init; }

    /// <summary>Depot'a özgü olaylarda dolu.</summary>
    public uint? DepotId { get; init; }

    /// <summary>0–100 arası ilerleme; bilinmiyorsa null.</summary>
    public double? Percent { get; init; }

    /// <summary>Login hatasının makine tarafından okunabilir sebebi.</summary>
    public LoginFailureReason? FailureReason { get; init; }

    /// <summary>
    /// Giriş satırındaki hesap numarası (<c>[U:1:000000000]</c>). Profil avatarını
    /// çekebilmek için SteamID64'e dönüştürülür.
    /// </summary>
    public uint? SteamAccountId { get; init; }

    public bool IsFailure => Kind is SteamCmdEventKind.Error or SteamCmdEventKind.LoginFailed;
}

public enum LoginFailureReason
{
    Unknown,
    InvalidPassword,
    RateLimitExceeded,
    TwoFactorMismatch,
    AccountLoginDeniedNeedTwoFactor,
}
