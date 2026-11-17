using System.Diagnostics;

namespace MOIRAI.Services;

public static class Updater
{
    public static void ApplyAndRestart(string zipPath)
    {
        var target = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var exe = Environment.ProcessPath ?? Path.Combine(target, "Moirai.App.exe");
        var pid = Environment.ProcessId;
        var cmdPath = Path.Combine(Path.GetTempPath(), $"moirai-update-{Guid.NewGuid():N}.cmd");

        var script =
            "@echo off\r\n" +
            "rem MOIRAI self-update: wait for the app to close, extract the new build, restart.\r\n" +
            ":waitloop\r\n" +
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
            "if %errorlevel%==0 (\r\n" +
            "  timeout /t 1 /nobreak >nul\r\n" +
            "  goto waitloop\r\n" +
            ")\r\n" +
            $"tar -xf \"{zipPath}\" -C \"{target}\" --exclude=update-config.json\r\n" +
            $"del \"{zipPath}\" >nul 2>&1\r\n" +
            $"start \"\" \"{exe}\"\r\n" +
            "(goto) 2>nul & del \"%~f0\"\r\n";

        File.WriteAllText(cmdPath, script);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{cmdPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        });

        Environment.Exit(0);
    }
}
