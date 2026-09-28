using System.Diagnostics;

namespace AFKNotifier.Updater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var values = ParseArguments(args);
            var processId = int.Parse(values["pid"]);
            var sourceDirectory = values["source"];
            var targetDirectory = values["target"];
            var executablePath = values["exe"];

            try
            {
                using var process = Process.GetProcessById(processId);
                process.WaitForExit(30000);
            }
            catch (ArgumentException)
            {
            }

            Thread.Sleep(250);
            CopyDirectory(sourceDirectory, targetDirectory);

            Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true
            });

            try
            {
                var updateRoot = Directory.GetParent(sourceDirectory)?.FullName;
                if (!string.IsNullOrWhiteSpace(updateRoot) && Directory.Exists(updateRoot))
                {
                    Directory.Delete(updateRoot, true);
                }
            }
            catch
            {
            }

            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            var key = args[index].TrimStart('-');
            values[key] = args[index + 1];
        }

        foreach (var required in new[] { "pid", "source", "target", "exe" })
        {
            if (!values.ContainsKey(required))
            {
                throw new ArgumentException($"Missing --{required}.");
            }
        }

        return values;
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destination = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }
}
