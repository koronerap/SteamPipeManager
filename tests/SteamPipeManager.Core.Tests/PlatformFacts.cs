namespace SteamPipeManager.Core.Tests;

/// <summary>
/// Yalnızca Windows'ta anlamlı olan test: DPAPI ya da Windows'un zorunlu dosya
/// kilitleri (açık bir dosya taşınamaz/silinemez). Linux ve macOS'ta bu durum hiç
/// oluşmuyor; test orada başarısız sayılmak yerine gerekçesiyle atlanıyor.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = $"Yalnızca Windows: {reason}";
        }
    }
}
