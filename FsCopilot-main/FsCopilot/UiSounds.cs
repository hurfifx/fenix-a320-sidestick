namespace FsCopilot;

using System.Collections.Concurrent;
using System.Media;
using System.Reflection;

public static class UiSounds
{
    private static readonly ConcurrentDictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static void Play(string name)
    {
        var payload = Cache.GetOrAdd(name, LoadSoundBytes);
        if (payload == null || payload.Length == 0)
            return;

        _ = Task.Run(() =>
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var player = new SoundPlayer(stream);
            player.Load();
            player.PlaySync();
        });
    }

    private static byte[]? LoadSoundBytes(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using Stream? stream = asm.GetManifestResourceStream($"FsCopilot.Assets.{name}.wav");
        if (stream == null)
            return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
