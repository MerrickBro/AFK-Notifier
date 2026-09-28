using System.Diagnostics;
using AFKNotifier.Models;

namespace AFKNotifier.Services;

public sealed class ProcessService
{
    public IReadOnlyList<AudioProcessOption> GetApplications()
    {
        var applications = new List<AudioProcessOption>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                var processName = process.ProcessName;
                var windowTitle = process.MainWindowTitle;
                var displayName = string.IsNullOrWhiteSpace(windowTitle)
                    ? $"{processName} ({process.Id})"
                    : $"{processName} — {windowTitle} ({process.Id})";

                applications.Add(new AudioProcessOption(process.Id, processName, displayName));
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return applications
            .OrderBy(application => application.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(application => application.ProcessId)
            .ToArray();
    }
}
