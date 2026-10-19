using System.IO;
using System.Text.Json;
using TunnlSplitVPN.Models;

namespace TunnlSplitVPN.Services;

public sealed class TunnelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public TunnelStore()
    {
        RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TunnlSplitVPN");
        TunnelsDirectory = Path.Combine(RootDirectory, "tunnels");
        RuntimeDirectory = Path.Combine(RootDirectory, "runtime");
        SettingsPath = Path.Combine(RootDirectory, "settings.json");

        Directory.CreateDirectory(TunnelsDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
    }

    public string RootDirectory { get; }
    public string TunnelsDirectory { get; }
    public string RuntimeDirectory { get; }
    public string SettingsPath { get; }

    public AppSettings LoadSettings()
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings();

        var json = File.ReadAllText(SettingsPath);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
            ?? throw new InvalidDataException("The settings file is empty or invalid.");
    }

    public void SaveSettings(AppSettings settings)
    {
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    public IReadOnlyList<TunnelProfile> LoadTunnels(out IReadOnlyList<string> errors)
    {
        var profiles = new List<TunnelProfile>();
        var loadErrors = new List<string>();

        foreach (var filePath in Directory.GetFiles(TunnelsDirectory, "*.conf")
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(filePath);
            var content = File.ReadAllText(filePath);
            if (!WireSockConfigBuilder.TryValidate(content, out var error))
            {
                loadErrors.Add($"{fileName}: {error}");
                continue;
            }

            profiles.Add(CreateProfile(Path.GetFileNameWithoutExtension(filePath), filePath, content));
        }

        errors = loadErrors;
        return profiles;
    }

    public TunnelProfile Import(string sourcePath)
    {
        if (!Path.GetExtension(sourcePath).Equals(".conf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a WireGuard .conf file.");

        var content = File.ReadAllText(sourcePath);
        if (!WireSockConfigBuilder.TryValidate(content, out var error))
            throw new InvalidDataException(error);

        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var target = Path.Combine(TunnelsDirectory, name + ".conf");
        var suffix = 2;
        while (File.Exists(target))
        {
            if (string.Equals(File.ReadAllText(target), content, StringComparison.Ordinal))
                return CreateProfile(name, target, content);

            target = Path.Combine(TunnelsDirectory, $"{name} ({suffix++}).conf");
        }

        File.WriteAllText(target, content);
        return CreateProfile(Path.GetFileNameWithoutExtension(target), target, content);
    }

    public string WriteRuntimeConfig(string tunnelName, string configText)
    {
        var safeName = string.Concat(tunnelName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        if (string.IsNullOrWhiteSpace(safeName))
            throw new InvalidDataException("The selected tunnel name is not valid.");

        var runtimePath = Path.Combine(RuntimeDirectory, $"{safeName}-{Guid.NewGuid():N}.conf");
        File.WriteAllText(runtimePath, configText);
        return runtimePath;
    }

    public void DeleteRuntimeConfig(string runtimePath)
    {
        var fullPath = Path.GetFullPath(runtimePath);
        var runtimeRoot = Path.GetFullPath(RuntimeDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(runtimeRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Refusing to delete a file outside the runtime configuration directory.");

        File.Delete(fullPath);
    }

    private static TunnelProfile CreateProfile(string name, string filePath, string config)
    {
        var addresses = ParseAddressList(config);
        return new TunnelProfile
        {
            Name = name,
            FilePath = filePath,
            ConfigText = config,
            Addresses = addresses
        };
    }

    private static string ParseAddressList(string config)
    {
        var inInterface = false;
        foreach (var line in config.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inInterface = trimmed.Equals("[Interface]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inInterface)
                continue;

            var equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex > 0 &&
                trimmed[..equalsIndex].Trim().Equals("Address", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join(", ", trimmed[(equalsIndex + 1)..]
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            }
        }

        return string.Empty;
    }
}
