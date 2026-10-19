using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace TunnlSplitVPN.Services;

public sealed class WireSockController
{
    private static readonly string[] DefaultPaths =
    {
        @"C:\Program Files\WireSock Secure Connect\bin\wiresock-client.exe",
        @"C:\Program Files\WireSock VPN Client\bin\wiresock-client.exe",
        @"C:\Program Files (x86)\WireSock Secure Connect\bin\wiresock-client.exe",
        @"C:\Program Files (x86)\WireSock VPN Client\bin\wiresock-client.exe",
        @"C:\Program Files\TunnlTo\wiresock-client.exe",
        @"C:\Program Files\TunnlTo\resources\bin\wiresock-client.exe"
    };

    private static readonly Regex SecretSetting = new(
        @"(?i)\b(?<name>PrivateKey|PresharedKey)\s*[:=]\s*[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EncodedKey = new(
        @"(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{42,43}={0,2}(?![A-Za-z0-9+/])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public event Action<string>? OutputReceived;
    public event Action<Process, int?>? ProcessExited;

    public string? FindClientBinary(string? preferredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
            return Path.GetFullPath(preferredPath);

        foreach (var path in DefaultPaths)
        {
            if (File.Exists(path))
                return path;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "wiresock-client.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public Process Start(string executablePath, string configFilePath)
    {
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("The selected WireSock client executable was not found.", executablePath);
        if (!File.Exists(configFilePath))
            throw new FileNotFoundException("The generated WireSock runtime configuration was not found.", configFilePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executablePath),
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))
                ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("-config");
        startInfo.ArgumentList.Add(Path.GetFullPath(configFilePath));
        startInfo.ArgumentList.Add("-log-level");
        startInfo.ArgumentList.Add("info");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => ForwardOutput(args.Data);
        process.ErrorDataReceived += (_, args) => ForwardOutput(args.Data);
        process.Exited += (_, _) =>
        {
            int? exitCode;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                exitCode = null;
            }

            ProcessExited?.Invoke(process, exitCode);
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Windows did not start the WireSock client process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    public static string SanitizeLogLine(string line)
    {
        var sanitized = SecretSetting.Replace(line,
            match => $"{match.Groups["name"].Value}=<redacted>");
        return EncodedKey.Replace(sanitized, "<redacted-key>");
    }

    public static void Stop(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.HasExited)
            return;

        process.Kill(entireProcessTree: true);
        if (!process.WaitForExit(5000))
            throw new TimeoutException("The WireSock process did not exit within five seconds.");
    }

    private void ForwardOutput(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
            OutputReceived?.Invoke(SanitizeLogLine(line));
    }
}
