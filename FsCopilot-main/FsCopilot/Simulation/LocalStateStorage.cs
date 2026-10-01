namespace FsCopilot.Simulation;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

internal static class LocalStateStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static readonly string BaseDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FsCopilot");

    public static T Load<T>(string fileName, T fallback)
    {
        try
        {
            var path = GetPath(fileName);
            if (!File.Exists(path))
                return fallback;

            var json = File.ReadAllText(path);
            var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return value ?? fallback;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Settings] Failed to load {FileName}", fileName);
            return fallback;
        }
    }

    public static void Save<T>(string fileName, T value)
    {
        try
        {
            Directory.CreateDirectory(BaseDirectory);
            var path = GetPath(fileName);
            var json = JsonSerializer.Serialize(value, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Settings] Failed to save {FileName}", fileName);
        }
    }

    private static string GetPath(string fileName) => Path.Combine(BaseDirectory, fileName);
}
