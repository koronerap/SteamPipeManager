using System.Globalization;

namespace SteamPipeManager.Core.Updates;

/// <summary>
/// Güncelleme sonrası yeniden başlatılan sürece verilen argümanlar.
///
/// <c>--updated-from</c> kullanıcıya "şu sürümden güncellendi" demek için;
/// <c>--wait-pid</c> ise eski süreç kapanmadan <c>.spm-old</c> dosyalarının
/// silinmeye çalışılmaması için.
/// </summary>
public sealed record UpdateLaunchArguments(AppVersion? UpdatedFrom, int? WaitForProcessId)
{
    public const string UpdatedFromPrefix = "--updated-from=";

    public const string WaitPidPrefix = "--wait-pid=";

    public static UpdateLaunchArguments Parse(IEnumerable<string> args)
    {
        AppVersion? from = null;
        int? pid = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith(UpdatedFromPrefix, StringComparison.OrdinalIgnoreCase))
            {
                from = AppVersion.TryParse(arg[UpdatedFromPrefix.Length..]);
            }
            else if (arg.StartsWith(WaitPidPrefix, StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(arg[WaitPidPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                     id > 0)
            {
                pid = id;
            }
        }

        return new UpdateLaunchArguments(from, pid);
    }

    public static string Build(AppVersion updatedFrom, int processId) =>
        $"{UpdatedFromPrefix}{updatedFrom} {WaitPidPrefix}{processId.ToString(CultureInfo.InvariantCulture)}";
}
