using BepInEx.Logging;

namespace MegabonkSplitscreen;

// Writes to BepInEx's console/LogOutput.log and to MegabonkSplitscreen.log next to the plugin DLL.
// The file is recreated on every launch.
// 1.0 alpha (user: the Steam Deck dropped frames - does the logging slow the game?): diagnostic lines (those starting
// with a "[tag]" - [gold], [perf], [aim]...) are only written with DebugLogging on (config, off by default; always on
// in autotests); errors, warnings, untagged lines and the [down] / [pads] tags always are. The mod's file is flushed
// once a second (and at once on a warning or error) instead of after every line.
internal static class ModLog
{
    private static ManualLogSource source;
    private static StreamWriter file;
    private static readonly object fileLock = new();
    private static DateTime lastFlush;

    public static string Dir { get; private set; } = ".";
    public static bool DebugLogging { get; set; }
    public static bool Debug => DebugLogging || Autotest.Active;

    // Rare, player-relevant events kept in players' logs too (v1.0.5: a door teleport into a void left no trace)
    private static readonly string[] KeptTags = { "[down]", "[pads]", "[door]", "[world]", "[shrine]", "[float]" };

    public static void Init(ManualLogSource log, string dir)
    {
        source = log;
        Dir = dir;
        try
        {
            file = new StreamWriter(Path.Combine(dir, "MegabonkSplitscreen.log"), append: false) { AutoFlush = false };
        }
        catch (Exception e)
        {
            source.LogWarning($"Could not open log file: {e.Message}");
        }
    }

    // A "[tag] ..." line other than the kept ones: written only with debug logging
    private static bool Skipped(string msg)
    {
        if (Debug || string.IsNullOrEmpty(msg) || msg[0] != '[') return false;
        foreach (var t in KeptTags) if (msg.StartsWith(t, StringComparison.Ordinal)) return false;
        return true;
    }

    public static void Info(string msg)
    {
        if (Skipped(msg)) return;
        source?.LogInfo(msg);
        Write("INFO ", msg, false);
    }

    public static void Warn(string msg)
    {
        source?.LogWarning(msg);
        Write("WARN ", msg, true);
    }

    public static void Error(string msg)
    {
        source?.LogError(msg);
        Write("ERROR", msg, true);
    }

    private static void Write(string level, string msg, bool flushNow)
    {
        if (file == null) return;
        lock (fileLock)
        {
            file.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {level} {msg}");
            var now = DateTime.Now;
            if (flushNow || (now - lastFlush).TotalSeconds >= 1.0)
            {
                file.Flush();
                lastFlush = now;
            }
        }
    }

    // Quitting: what's left in the buffer
    public static void Flush()
    {
        if (file == null) return;
        lock (fileLock) { try { file.Flush(); } catch { } }
    }
}
