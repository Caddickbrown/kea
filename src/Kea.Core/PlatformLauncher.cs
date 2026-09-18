using System.Diagnostics;

namespace Kea.Core;

/// <summary>Opens URLs and folders in whatever the current desktop uses.</summary>
/// <remarks>
/// The original called <c>Process.Start(url)</c>, which only works on .NET Framework: on .NET 8
/// <see cref="ProcessStartInfo.UseShellExecute"/> defaults to false, so that call throws even on
/// Windows, and there is no shell handler to fall back to on macOS or Linux.
/// </remarks>
public static class PlatformLauncher
{
    public static bool TryOpen(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Start(new ProcessStartInfo("open", [target]));
            }
            else
            {
                Start(new ProcessStartInfo("xdg-open", [target]));
            }

            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // No browser or file manager available — headless sessions and bare containers.
            return false;
        }
    }

    private static void Start(ProcessStartInfo info)
    {
        info.RedirectStandardOutput = false;
        info.RedirectStandardError = false;
        using Process? process = Process.Start(info);
    }
}
