using System.IO;

namespace WinDock;

/// <summary>
/// A small diagnostic log at %APPDATA%\WinDock\log.txt: when WinDock started, why it exited, and any
/// error that stopped it. It exists so "it didn't start" can be explained after the fact.
/// </summary>
internal static class Log
{
    static readonly string FilePath = Path.Combine(Settings.Folder, "log.txt");
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Folder);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 200_000)
                    File.WriteAllLines(FilePath, File.ReadLines(FilePath).TakeLast(500).ToList());   // keep it small
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
