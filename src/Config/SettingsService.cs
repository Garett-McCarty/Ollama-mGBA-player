
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace OllamaNetGB.Config;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions serializerOptions = new()
    {
        WriteIndented = true
    };

    public AppSettings Current { get; private set; } = new();
    public string ConfigFilePath { get; }
    
    public SettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OllamaNetGB");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        ConfigFilePath = Path.Combine(dir, "settings.json");
    }

    public void Load()
    {
        if (!File.Exists(ConfigFilePath))
        {
            Current = new AppSettings();
            File.WriteAllText(
                ConfigFilePath,
                JsonSerializer.Serialize(Current, serializerOptions));
            return;
        }

        try
        {
            var json = File.ReadAllText(ConfigFilePath);
            Current = JsonSerializer.Deserialize<AppSettings>(json, serializerOptions) ?? new AppSettings();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Could not load settings; defaults will be used: {exception.Message}");
            Current = new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        await using var fs = File.Create(ConfigFilePath);
        await JsonSerializer.SerializeAsync(fs, Current, serializerOptions);
    }
}
