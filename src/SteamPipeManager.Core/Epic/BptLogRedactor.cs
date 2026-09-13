using System.Text.RegularExpressions;

namespace SteamPipeManager.Core.Epic;

/// <summary>
/// BuildPatchTool log'unu paylaşılabilir hâle getirir.
///
/// Log dosyaları hata ayıklamak için çok değerli — ilerleme satırlarının biçimi, hata
/// sebepleri, aşama işaretleri hep orada. Ama aynı dosya organizasyon/ürün/artifact
/// kimliklerini, kullanıcı adını ve makinedeki tam yolları da taşıyor.
///
/// Bu sınıf ikisini ayırıyor: teknik bilgi kalıyor, kime ait olduğu gidiyor. Böylece
/// bir kullanıcı log'unu bize ya da bir arkadaşına gönderebiliyor.
///
/// Not: BuildPatchTool client secret'ı kendi log'unda zaten <c>******</c> ile
/// maskeliyor (ölçüldü), yani secret için ek bir şey yapmak gerekmiyor — yine de
/// kural olarak burada da yakalanıyor.
/// </summary>
public static partial class BptLogRedactor
{
    /// <summary>
    /// Log metnini maskeler.
    /// </summary>
    /// <param name="text">Ham log içeriği.</param>
    /// <param name="userName">
    /// Maskelenecek kullanıcı adı; verilmezse makinedeki kullanıcı adı kullanılır.
    /// Yolların içinde geçtiği için ayrıca ele alınıyor.
    /// </param>
    public static string Redact(string text, string? userName = null)
    {
        if (text is not { Length: > 0 })
        {
            return "";
        }

        var result = text;

        // Kimlik taşıyan parametreler: adı kalsın, değeri gitsin. Hangi parametrenin
        // verildiği teknik bilgi; değeri değil.
        result = IdentityParameter().Replace(result, m => $"-{m.Groups["key"].Value}=<gizlendi>");

        // Log satırlarındaki "OrganizationId: xyz" biçimi (yapılandırma bloğu).
        result = IdentityField().Replace(result, m => $"{m.Groups["key"].Value}: <gizlendi>");

        // Kullanıcı profili yolları.
        result = UserProfilePath().Replace(result, @"$1\<kullanıcı>\");

        var user = userName ?? Environment.UserName;

        if (user is { Length: > 2 })
        {
            result = result.Replace(user, "<kullanıcı>", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// Paylaşılacak dosyanın başına konan açıklama. Log'u alan kişinin neyin
    /// maskelendiğini bilmesi, eksik bilgiyi hata sanmasını engelliyor.
    /// </summary>
    public static string Header(string buildVersion, DateTimeOffset when) =>
        $"""
         # Steam Pipe Manager — BuildPatchTool log
         # Sürüm : {buildVersion}
         # Tarih : {when:yyyy-MM-dd HH:mm:ss zzz}
         #
         # Organizasyon, ürün, artifact kimlikleri, kullanıcı adı ve ev dizini yolları
         # <gizlendi> ile değiştirildi. Teknik satırlar olduğu gibi duruyor.

         """;

    [GeneratedRegex(
        @"-(?<key>ClientSecret|ClientId|OrganizationId|ProductId|ArtifactId|SandboxId|DeploymentId)=\S+",
        RegexOptions.IgnoreCase)]
    private static partial Regex IdentityParameter();

    [GeneratedRegex(
        @"\b(?<key>ClientId|OrganizationId|ProductId|ArtifactId|SandboxId):\s*\S+",
        RegexOptions.IgnoreCase)]
    private static partial Regex IdentityField();

    [GeneratedRegex(@"([A-Za-z]:[\\/]Users)[\\/][^\\/\s]+[\\/]", RegexOptions.IgnoreCase)]
    private static partial Regex UserProfilePath();
}
