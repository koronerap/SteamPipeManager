using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Presentation.Services;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.SteamCmd;

namespace SteamPipeManager.Presentation.ViewModels;

/// <summary>
/// Uygulama içi Steam girişi. Konsol penceresi açılmaz; şifre stdin üzerinden
/// SteamCMD'ye iletilir ve yalnızca bu işlem süresince bellekte tutulur.
/// </summary>
public sealed partial class LoginViewModel(BuildCoordinator coordinator) : ObservableObject
{
    private TaskCompletionSource<string?>? _guardCodeRequest;
    private CancellationTokenSource? _cancellation;

    /// <summary>Giriş tamamlandığında sonucu döndürmek için.</summary>
    private TaskCompletionSource<LoginResult>? _completion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private UserProfile? _profile;

    public bool IsOpen => Profile is not null;

    public string Username => Profile?.SteamUsername ?? "";

    public string DisplayName => Profile?.DisplayName ?? "";

    /// <summary>PasswordBox bağlanamadığı için değer code-behind'dan set edilir.</summary>
    public string Password { get; set; } = "";

    [ObservableProperty]
    private string _guardCode = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(ShowPasswordStep))]
    [NotifyPropertyChangedFor(nameof(ShowGuardStep))]
    [NotifyPropertyChangedFor(nameof(ShowMobileStep))]
    private LoginStage _stage = LoginStage.Starting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(ShowPasswordStep))]
    [NotifyPropertyChangedFor(nameof(ShowGuardStep))]
    [NotifyPropertyChangedFor(nameof(ShowMobileStep))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsIdle => !IsBusy;

    /// <summary>Şifre adımı: henüz giriş başlatılmadıysa.</summary>
    public bool ShowPasswordStep => !IsBusy;

    public bool ShowGuardStep => IsBusy && Stage == LoginStage.AwaitingGuardCode;

    public bool ShowMobileStep => IsBusy && Stage == LoginStage.AwaitingMobileConfirmation;

    /// <summary>Pencereyi açar ve giriş bitene kadar bekleyen bir görev döndürür.</summary>
    public Task<LoginResult> BeginAsync(UserProfile profile)
    {
        Profile = profile;
        Password = "";
        GuardCode = "";
        ErrorMessage = null;
        StatusMessage = "";
        Stage = LoginStage.Starting;
        IsBusy = false;

        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(DisplayName));

        _completion = new TaskCompletionSource<LoginResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        return _completion.Task;
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (Profile is not { } profile || IsBusy)
        {
            return;
        }

        if (Password.Length == 0)
        {
            ErrorMessage = AppLocalizer.Instance.Get("Login.Error.EmptyPassword");
            return;
        }

        ErrorMessage = null;
        IsBusy = true;
        _cancellation = new CancellationTokenSource();

        try
        {
            var progress = new Progress<LoginProgress>(p =>
            {
                Stage = p.Stage;
                StatusMessage = p.Message;
            });

            var result = await coordinator.LoginAsync(
                profile, Password, RequestGuardCodeAsync, progress, _cancellation.Token);

            if (result.Succeeded)
            {
                Finish(result);
                return;
            }

            // Başarısızsa pencere açık kalır; kullanıcı şifreyi düzeltip tekrar deneyebilir.
            Stage = LoginStage.Failed;
            ErrorMessage = Describe(result);
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            Stage = LoginStage.Failed;
            ErrorMessage = ex.Message;
        }
        finally
        {
            // Şifre bellekte bırakılmaz.
            Password = "";
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private static string Describe(LoginResult result) => result.Reason switch
    {
        LoginFailureReason.InvalidPassword => AppLocalizer.Instance.Get("Login.Error.InvalidPassword"),
        LoginFailureReason.RateLimitExceeded => AppLocalizer.Instance.Get("Login.Error.RateLimit"),
        LoginFailureReason.TwoFactorMismatch => AppLocalizer.Instance.Get("Login.Error.GuardMismatch"),
        LoginFailureReason.AccountLoginDeniedNeedTwoFactor => AppLocalizer.Instance.Get("Login.Error.GuardRequired"),
        _ => result.Message,
    };

    /// <summary>Steam Guard kodu istendiğinde çağrılır; kullanıcı girene kadar bekler.</summary>
    private Task<string?> RequestGuardCodeAsync(CancellationToken ct)
    {
        GuardCode = "";
        _guardCodeRequest = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ct.Register(() => _guardCodeRequest?.TrySetResult(null));

        return _guardCodeRequest.Task;
    }

    [RelayCommand]
    private void SubmitGuardCode()
    {
        if (GuardCode.Trim() is { Length: > 0 } code)
        {
            _guardCodeRequest?.TrySetResult(code);
            StatusMessage = AppLocalizer.Instance.Get("Login.CodeSent");
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _guardCodeRequest?.TrySetResult(null);
        _cancellation?.Cancel();

        Finish(new LoginResult(LoginStage.Cancelled, AppLocalizer.Instance.Get("Login.Cancelled")));
    }

    private void Finish(LoginResult result)
    {
        Password = "";
        GuardCode = "";
        Profile = null;
        IsBusy = false;

        _completion?.TrySetResult(result);
        _completion = null;
    }
}
