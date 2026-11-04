using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ControlCenter.Services;

public sealed record MaintenanceScriptResult(int ExitCode, string Output, string Error);

public static class DataMaintenanceService
{
    public static string? ResolveMonitoringEngineDirectory()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "monitoring_engine");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "main.py")))
                return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }

        string dbPath = LocalDatabaseService.ResolveDatabasePath();
        if (!string.IsNullOrEmpty(dbPath))
        {
            string? dbDir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dbDir))
            {
                string? parent = Directory.GetParent(dbDir)?.FullName;
                if (!string.IsNullOrEmpty(parent) && File.Exists(Path.Combine(parent, "main.py")))
                    return parent;
            }
        }

        return null;
    }

    public static string ResolvePythonExecutable(string engineDir)
    {
        string venvPython = Path.Combine(engineDir, "venv", "Scripts", "python.exe");
        if (File.Exists(venvPython))
            return venvPython;

        return "python";
    }

    public static bool IsEngineRunning()
    {
        try
        {
            var pythonProcs = Process.GetProcessesByName("python")
                .Concat(Process.GetProcessesByName("pythonw"))
                .ToArray();

            if (pythonProcs.Length == 0)
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Process -Filter \\\"name like 'python%'\\\" | Select-Object -ExpandProperty CommandLine\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(4000);

            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string lower = line.Trim().ToLowerInvariant();
                if (lower.Contains("main.py") || lower.Contains("monitor.py"))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DataMaintenanceService] IsEngineRunning check failed: {ex}");
        }

        return false;
    }

    public static async Task<MaintenanceScriptResult> RunScriptAsync(
        string scriptName,
        string? arguments,
        string? stdinInput)
    {
        string? engineDir = ResolveMonitoringEngineDirectory();
        if (string.IsNullOrEmpty(engineDir))
        {
            return new MaintenanceScriptResult(1, string.Empty, "Could not find monitoring_engine directory.");
        }

        string pythonExe = ResolvePythonExecutable(engineDir);
        string scriptPath = Path.Combine("tools", scriptName);

        var args = string.IsNullOrWhiteSpace(arguments)
            ? $"\"{scriptPath}\""
            : $"\"{scriptPath}\" {arguments}";

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = args,
            WorkingDirectory = engineDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        if (!string.IsNullOrEmpty(stdinInput))
        {
            await process.StandardInput.WriteLineAsync(stdinInput);
            await process.StandardInput.FlushAsync();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        return new MaintenanceScriptResult(process.ExitCode, stdout, stderr);
    }
}
