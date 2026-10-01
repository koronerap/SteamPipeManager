using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamPipeManager.Presentation.Localization;
using SteamPipeManager.Presentation.Services;
using SteamPipeManager.Core.Epic;
using SteamPipeManager.Core.Models;
using SteamPipeManager.Core.Publishing;
using SteamPipeManager.Core.SteamCmd;

namespace SteamPipeManager.Presentation.ViewModels;

/// <summary>
/// Profil kartı: hesabın özeti ve SteamCMD oturum durumu.
/// Oturum durumu build ekranından buraya taşındı — giriş hesap düzeyinde bir kavram,
/// build hedefine değil profile ait.
/// </summary>
public sealed partial class ProfileCard(UserProfile profile) : ObservableObject
{
    /// <summary>Liste öğesinin erişilebilir adı (ekran okuyucu, otomasyon).</summary>
    public override string ToString() => DisplayName;

    public UserProfile Profile { get; } = profile;

    public string DisplayName => Profile.DisplayName;

    public string SteamUsername => Profile.SteamUsername;

    public PublishProviderId Provider => Profile.Provider;

    /// <summary>Epic profilleri kartta ayırt edilsin; destek henüz deneysel.</summary>
    public bool IsEpic => Profile.Provider == PublishProviderId.Epic;

    /// <summary>Kartın üzerindeki küçük mağaza etiketi. Yalnızca Hub'da gösteriliyor.</summary>
    public string ProviderName => IsEpic ? "Epic" : "Steam";


    /// <summary>
    /// Kartta hesabın altında gösterilen satır. Steam'de kullanıcı adı, Epic'te
    /// organizasyon kimliği — ikisi de "bu profil hangi hesap" sorusunun cevabı.
    /// </summary>
    public string AccountLine => IsEpic
        ? Profile.Epic?.OrganizationId ?? ""
        : Profile.SteamUsername;

    /// <summary>
    /// Steam avatarı (varsa). Yoksa baş harf rozetine düşülür — hesabın Steam profili
    /// gizli veya yoksa görsel çekilemez.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar))]
    private string? _avatarPath;

    public bool HasAvatar => AvatarPath is { Length: > 0 };

    /// <summary>Kart üzerindeki yuvarlak rozet için baş harf.</summary>
    public string Initial => Profile.DisplayName is { Length: > 0 } name
        ? name[..1].ToUpperInvariant()
        : "?";

    public int AppCount => Profile.Apps.Count;

    public int SubAppCount => ProfileRepository.AllSubApps(Profile).Count();

    public string Summary => AppCount == 0
        ? AppLocalizer.Instance.Get("Profile.NoGames")
        : AppLocalizer.Instance.Format("Profile.Summary", AppCount, SubAppCount);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionText))]
    [NotifyPropertyChangedFor(nameof(SessionTone))]
    [NotifyPropertyChangedFor(nameof(NeedsLogin))]
    private SessionState _session = SessionState.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionText))]
    private string _sessionDetail = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionText))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isChecking;

    public bool IsIdle => !IsChecking;

    public bool NeedsLogin => Session is SessionState.LoginRequired or SessionState.Unknown;

    public string SessionText => (IsChecking, Session) switch
    {
        (true, _) => AppLocalizer.Instance.Get("Session.Checking"),
        (_, SessionState.Active) => AppLocalizer.Instance.Get("Session.Active"),
        (_, SessionState.LoginRequired) => AppLocalizer.Instance.Get("Session.LoginRequired"),
        (_, SessionState.CheckFailed) => AppLocalizer.Instance.Format("Session.CheckFailed", SessionDetail),
        _ => AppLocalizer.Instance.Get("Session.Unknown"),
    };

    public StatusTone SessionTone => Session switch
    {
        SessionState.Active => StatusTone.Success,
        SessionState.LoginRequired => StatusTone.Warning,
        SessionState.CheckFailed => StatusTone.Danger,
        _ => StatusTone.Muted,
    };
}

public sealed partial class ProfilePickerViewModel(
    ProfileRepository repository,
    NavigationState navigation,
    IConfirmationService confirmation,
    BuildCoordinator coordinator,
    SteamImageService images,
    LoginViewModel login,
    EpicSecretStore secrets,
    ProductProfile product) : ObservableObject
{
    /// <summary>Uygulama ömrü boyunca bir kez otomatik kontrol yapılır.</summary>
    private bool _autoCheckDone;

    /// <summary>Avatarlar da açılışta bir kez tazelenir.</summary>
    private bool _avatarsRefreshed;

    public ObservableCollection<ProfileCard> Cards { get; } = [];

    [ObservableProperty]
    private bool _isAddingProfile;

    [ObservableProperty]
    private string _newProfileName = "";

    [ObservableProperty]
    private string _newSteamUsername = "";

    /// <summary>Yeni profilin sağlayıcısı. Varsayılan Steam; Epic deneysel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewProfileEpic))]
    private PublishProviderId _newProvider = PublishProviderId.Steam;

    public bool IsNewProfileEpic => NewProvider == PublishProviderId.Epic;

    [ObservableProperty]
    private string _newOrganizationId = "";

    [ObservableProperty]
    private string _newClientId = "";

    [ObservableProperty]
    private string _newClientSecret = "";

    /// <summary>
    /// Sağlayıcı seçicideki liste — ürüne göre. Steam Pipe Manager'da tek eleman
    /// olduğu için seçici hiç gösterilmiyor; kullanıcı olmayan bir seçenekle
    /// karşılaşmıyor.
    /// </summary>
    public IReadOnlyList<PublishProviderId> Providers => product.Providers;

    /// <summary>Birden çok sağlayıcı varsa seçici gösterilir.</summary>
    public bool ShowProviderChoice => product.HasProviderChoice;

    public string ProductName => product.Name;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Düzenlenen profil; null ise düzenleme kapalı.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsEditingEpic), nameof(IsEditingSteam),
        nameof(EditHasStoredSecret))]
    private ProfileCard? _editing;

    [ObservableProperty]
    private string _editName = "";

    [ObservableProperty]
    private string _editUsername = "";

    [ObservableProperty]
    private string _editOrganizationId = "";

    [ObservableProperty]
    private string _editClientId = "";

    /// <summary>
    /// Düzenleme formuna secret hiç yüklenmiyor; boş bırakılırsa kayıtlı secret
    /// olduğu gibi kalıyor. Var olan bir sırrı ekranda göstermenin faydası yok.
    /// </summary>
    [ObservableProperty]
    private string _editClientSecret = "";

    public bool IsEditing => Editing is not null;

    /// <summary>Düzenlenen profil Epic mi — form hangi alanları göstereceğini buna bakıyor.</summary>
    public bool IsEditingEpic => Editing?.IsEpic ?? false;

    public bool IsEditingSteam => Editing is { IsEpic: false };

    /// <summary>
    /// "Secret nerede duruyor" açıklamasının dil anahtarı. Platforma göre değişiyor:
    /// Windows'ta DPAPI, macOS'ta Anahtar Zinciri, Linux'ta gizli bilgi servisi ya da
    /// — o yoksa — yalnızca sahibinin okuyabildiği şifresiz dosya.
    /// </summary>
    public string SecretStorageKey => secrets.Kind switch
    {
        SecretStorageKind.MacKeychain => "Epic.SecretStorage.Body.Keychain",
        SecretStorageKind.LinuxSecretService => "Epic.SecretStorage.Body.SecretService",
        SecretStorageKind.OwnerOnlyFile => "Epic.SecretStorage.Body.File",
        _ => "Epic.SecretStorage.Body",
    };

    /// <summary>Secret şifresiz saklanıyorsa form bunu uyarı olarak gösteriyor.</summary>
    public bool IsSecretStorageUnencrypted => !secrets.IsEncrypted;

    /// <summary>
    /// Boş liste metni ürüne göre: Epic ürününde SteamPipe içe aktarmasından söz
    /// etmek anlamsız ve kafa karıştırıcı.
    /// </summary>
    public bool SupportsSteam => product.Supports(PublishProviderId.Steam);

    public bool IsEpicOnly => !SupportsSteam;

    /// <summary>Kayıtlı bir secret var mı; forma "girilmiş" bilgisi olarak yansıyor.</summary>
    public bool EditHasStoredSecret =>
        Editing is { } card && secrets.Has(card.Profile.Id);

    public void Refresh()
    {
        var previous = Cards.ToDictionary(c => c.Profile.Id, c => (c.Session, c.SessionDetail));

        Cards.Clear();

        // Ürünün desteklemediği profiller gizleniyor ama SİLİNMİYOR: kullanıcı Hub'da
        // Epic profili oluşturup Steam Pipe Manager'a dönerse verisini kaybetmemeli.
        foreach (var profile in repository.Profiles.Where(product.CanShow))
        {
            var card = new ProfileCard(profile);

            // Yeniden yüklemede daha önce öğrenilen oturum durumu korunur.
            if (previous.TryGetValue(profile.Id, out var state))
            {
                card.Session = state.Session;
                card.SessionDetail = state.SessionDetail;
            }

            // Avatar diskteki önbellekten anında gelir; ağa çıkılmaz.
            if (profile.SteamId64 is { } steamId)
            {
                card.AvatarPath = images.GetCachedAvatar(steamId);
            }

            Cards.Add(card);
        }
    }

    /// <summary>
    /// SteamCMD zaten kuruluysa oturumları arka planda bir kez kontrol eder.
    /// Kurulu değilse hiçbir şey yapılmaz: açılışta 43 MB indirme başlatmak istemeyiz.
    /// </summary>
    public async Task AutoCheckSessionsAsync()
    {
        if (_autoCheckDone || Cards.Count == 0)
        {
            return;
        }

        try
        {
            if (!await coordinator.IsInstalledAsync())
            {
                return;
            }
        }
        catch
        {
            // Ayarlar okunamıyorsa sessizce vazgeç; açılış bundan etkilenmemeli.
            return;
        }

        _autoCheckDone = true;

        // Tüm profiller aynı SteamCMD kurulumunu paylaştığı için kontroller sıralı yapılır.
        foreach (var card in Cards.ToList())
        {
            await CheckAsync(card);
        }
    }

    [RelayCommand]
    private void Select(ProfileCard card) => navigation.SelectProfile(card.Profile);

    [RelayCommand]
    private async Task CheckSessionAsync(ProfileCard card) => await CheckAsync(card);

    private async Task CheckAsync(ProfileCard card)
    {
        if (card.IsChecking)
        {
            return;
        }

        card.IsChecking = true;

        try
        {
            var result = await coordinator.CheckSessionAsync(card.Profile);
            card.Session = result.State;
            card.SessionDetail = result.Detail;

            await ApplySteamIdAsync(card, result.SteamId64);
        }
        catch (Exception ex)
        {
            card.Session = SessionState.CheckFailed;
            card.SessionDetail = ex.Message;
        }
        finally
        {
            card.IsChecking = false;
        }
    }

    /// <summary>
    /// Uygulama içi giriş penceresini açar. Konsol penceresi açılmaz; şifre yalnızca
    /// giriş süresince bellekte tutulur, diske yazılmaz.
    /// </summary>
    [RelayCommand]
    private async Task LoginAsync(ProfileCard card)
    {
        StatusMessage = null;

        var result = await login.BeginAsync(card.Profile);

        if (!result.Succeeded)
        {
            StatusMessage = result.Stage == LoginStage.Cancelled ? null : result.Message;
            return;
        }

        card.Session = SessionState.Active;
        card.SessionDetail = AppLocalizer.Instance.Get("Session.Active");
        StatusMessage = AppLocalizer.Instance.Format("Profile.LoginDone", card.SteamUsername);

        await ApplySteamIdAsync(card, result.SteamId64);
    }

    /// <summary>
    /// Giriş satırından gelen SteamID profile yazılır ve avatar bir kez indirilir.
    /// Avatar bulunamazsa sessizce baş harf rozetinde kalınır.
    /// </summary>
    private async Task ApplySteamIdAsync(ProfileCard card, ulong? steamId64)
    {
        if (steamId64 is { } id && card.Profile.SteamId64 != id)
        {
            card.Profile.SteamId64 = id;
            await repository.SaveAsync();
        }

        await LoadAvatarAsync(card);
    }

    private async Task LoadAvatarAsync(ProfileCard card)
    {
        if (card.Profile.SteamId64 is not { } id || card.HasAvatar)
        {
            return;
        }

        card.AvatarPath = await images.GetAvatarAsync(id);
    }

    /// <summary>
    /// Uygulama açılışında profil başına bir kez Steam'e sorar: avatar değiştiyse
    /// önbellek güncellenir, ağ erişilemezse eldeki görsel korunur.
    /// </summary>
    public async Task RefreshAvatarsAsync()
    {
        if (_avatarsRefreshed)
        {
            return;
        }

        _avatarsRefreshed = true;

        foreach (var card in Cards.ToList())
        {
            if (card.Profile.SteamId64 is not { } id)
            {
                continue;
            }

            try
            {
                if (await images.RefreshAvatarAsync(id) is { } path)
                {
                    // Aynı yola yazıldığı için bağlamayı zorlamak gerekiyor.
                    card.AvatarPath = null;
                    card.AvatarPath = path;
                }
            }
            catch
            {
                // Avatar tazelenemezse önbellekteki görselle devam edilir.
            }
        }
    }

    // --- Profil düzenleme ---

    [RelayCommand]
    private void BeginEdit(ProfileCard card)
    {
        Editing = card;
        EditName = card.DisplayName;
        EditUsername = card.SteamUsername;
        EditOrganizationId = card.Profile.Epic?.OrganizationId ?? "";
        EditClientId = card.Profile.Epic?.ClientId ?? "";
        EditClientSecret = "";
        ErrorMessage = null;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        Editing = null;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task ConfirmEditAsync()
    {
        if (Editing is not { } card)
        {
            return;
        }

        if (card.IsEpic)
        {
            await ConfirmEpicEditAsync(card);
            return;
        }

        var name = EditName.Trim();
        var username = EditUsername.Trim();

        if (name.Length == 0 || username.Length == 0)
        {
            ErrorMessage = AppLocalizer.Instance.Get("Profile.Error.Required");
            return;
        }

        var clash = repository.Profiles.Any(p =>
            p != card.Profile &&
            string.Equals(p.SteamUsername, username, StringComparison.OrdinalIgnoreCase));

        if (clash)
        {
            ErrorMessage = AppLocalizer.Instance.Format("Profile.Error.UsernameTaken", username);
            return;
        }

        // Kullanıcı adı değiştiyse eski oturum ve avatar artık o hesaba ait değil.
        if (!string.Equals(card.Profile.SteamUsername, username, StringComparison.OrdinalIgnoreCase))
        {
            card.Profile.SteamId64 = null;
            card.AvatarPath = null;
            card.Session = SessionState.Unknown;
            card.SessionDetail = "";
        }

        card.Profile.DisplayName = name;
        card.Profile.SteamUsername = username;
        await repository.SaveAsync();

        Editing = null;
        Refresh();
    }

    [RelayCommand]
    private void BeginAddProfile()
    {
        NewProfileName = "";
        NewSteamUsername = "";
        NewProvider = product.DefaultProvider;
        NewOrganizationId = "";
        NewClientId = "";
        NewClientSecret = "";
        ErrorMessage = null;
        IsAddingProfile = true;
    }

    [RelayCommand]
    private void CancelAddProfile()
    {
        IsAddingProfile = false;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task ConfirmAddProfileAsync()
    {
        var profile = NewProvider == PublishProviderId.Epic
            ? CreateEpicProfile()
            : CreateSteamProfile();

        if (profile is null)
        {
            return;
        }

        await repository.SaveAsync();

        IsAddingProfile = false;
        Refresh();
        navigation.SelectProfile(profile);
    }

    private UserProfile? CreateSteamProfile()
    {
        var name = NewProfileName.Trim();
        var username = NewSteamUsername.Trim();

        if (name.Length == 0 || username.Length == 0)
        {
            ErrorMessage = AppLocalizer.Instance.Get("Profile.Error.Required");
            return null;
        }

        if (repository.Profiles.Any(p =>
                p.Provider == PublishProviderId.Steam &&
                string.Equals(p.SteamUsername, username, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = AppLocalizer.Instance.Format("Profile.Error.Duplicate", username);
            return null;
        }

        return repository.AddProfile(name, username);
    }

    /// <summary>
    /// Epic profilinde Steam kullanıcı adı yok; kimlik organizasyon + client id ile
    /// kuruluyor. Secret profil dosyasına değil, şifreli ayrı depoya gidiyor.
    /// </summary>
    private UserProfile? CreateEpicProfile()
    {
        var name = NewProfileName.Trim();
        var organizationId = NewOrganizationId.Trim();
        var clientId = NewClientId.Trim();

        if (name.Length == 0 || organizationId.Length == 0 || clientId.Length == 0)
        {
            ErrorMessage = AppLocalizer.Instance.Get("Epic.Error.Required");
            return null;
        }

        var profile = repository.AddProfile(name, steamUsername: "");
        profile.Provider = PublishProviderId.Epic;
        profile.Epic = new EpicProfileSettings
        {
            OrganizationId = organizationId,
            ClientId = clientId,
        };

        if (NewClientSecret is { Length: > 0 } secret)
        {
            secrets.Write(profile.Id, secret);
        }

        // Formdaki secret hemen unutuluyor; bellekte gereğinden uzun durmasın.
        NewClientSecret = "";

        return profile;
    }

    /// <summary>
    /// Silme geri alınamaz ve altındaki tüm oyun/hedef tanımlarını götürür, bu yüzden
    /// onay isteniyor.
    /// </summary>
    [RelayCommand]
    /// <summary>
    /// Epic profilinin düzenlenmesi. Steam'deki kullanıcı adı kuralı burada geçersiz —
    /// Epic profilinde o alan hep boş, eskiden bu yüzden Epic profilleri hiç
    /// kaydedilemiyordu. Secret boş bırakılırsa kayıtlı olan korunuyor.
    /// </summary>
    private async Task ConfirmEpicEditAsync(ProfileCard card)
    {
        var name = EditName.Trim();
        var organizationId = EditOrganizationId.Trim();
        var clientId = EditClientId.Trim();

        if (name.Length == 0 || organizationId.Length == 0 || clientId.Length == 0)
        {
            ErrorMessage = AppLocalizer.Instance.Get("Epic.Error.Required");
            return;
        }

        var epic = card.Profile.Epic ??= new EpicProfileSettings();

        var identityChanged =
            !string.Equals(epic.OrganizationId, organizationId, StringComparison.Ordinal) ||
            !string.Equals(epic.ClientId, clientId, StringComparison.Ordinal) ||
            EditClientSecret.Length > 0;

        card.Profile.DisplayName = name;
        epic.OrganizationId = organizationId;
        epic.ClientId = clientId;

        if (EditClientSecret is { Length: > 0 } secret)
        {
            secrets.Write(card.Profile.Id, secret);
        }

        // Formdaki secret hemen unutuluyor; bellekte gereğinden uzun durmasın.
        EditClientSecret = "";

        // Kimlik değiştiyse önceki doğrulamanın sonucu artık bu hesaba ait değil.
        if (identityChanged)
        {
            card.Session = SessionState.Unknown;
            card.SessionDetail = "";
        }

        await repository.SaveAsync();

        Editing = null;
        Refresh();
    }

    private async Task DeleteAsync(ProfileCard card)
    {
        var detail = card.AppCount == 0
            ? AppLocalizer.Instance.Get("Profile.Delete.NoApps")
            : AppLocalizer.Instance.Format("Profile.Delete.HasApps", card.AppCount, card.SubAppCount);

        var confirmed = await confirmation.ConfirmAsync(
            AppLocalizer.Instance.Get("Profile.Delete.Title"),
            AppLocalizer.Instance.Format("Profile.Delete.Body", card.DisplayName, detail),
            confirmText: AppLocalizer.Instance.Get("Common.Delete"),
            isDestructive: true);

        if (!confirmed)
        {
            return;
        }

        repository.RemoveProfile(card.Profile);
        await repository.SaveAsync();

        // Profilin şifreli secret'ı artık kimseye ait değil; dosyada kalmasın.
        if (card.IsEpic)
        {
            secrets.Remove(card.Profile.Id);
        }

        Refresh();
    }
}
