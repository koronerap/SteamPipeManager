using System.Globalization;

namespace SteamPipeManager.Core.Updates;

/// <summary>
/// Güncelleme sonrası yeniden başlatılan sürece verilen argümanlar.
///
/// <c>--updated-from</c> kullanıcıya "şu sürümden güncellendi" demek için;
/// <c>--wait-pid</c> ise eski süreç kapanmadan <c>.spm-old</c> dosyalarının
/// silinmeye çalışılmaması için. <c>--restart-widget</c>: güncellemeden önce kapatılan
/// widget yeni sürümle yeniden açılsın.
/// </summary>
public sealed record UpdateLaunchArguments(AppVersion? UpdatedFrom, int? WaitForProcessId, bool RestartWidget = false)
{
    public const string UpdatedFromPrefix = "--updated-from=";

    public const string WaitPidPrefix = "--wait-pid=";

    public const string RestartWidgetFlag = "--restart-widget";

    public static UpdateLaunchArguments Parse(IEnumerable<string> args)
    {
        AppVersion? from = null;
        int? pid = null;
        var restartWidget = false;

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
            else if (string.Equals(arg, RestartWidgetFlag, StringComparison.OrdinalIgnoreCase))
            {
                restartWidget = true;
            }
        }

        return new UpdateLaunchArguments(from, pid, restartWidget);
    }

    public static string Build(AppVersion updatedFrom, int processId, bool restartWidget = false) =>
        $"{UpdatedFromPrefix}{updatedFrom} {WaitPidPrefix}{processId.ToString(CultureInfo.InvariantCulture)}" +
        (restartWidget ? " " + RestartWidgetFlag : "");
}
