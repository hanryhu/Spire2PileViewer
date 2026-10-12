using System;
using System.IO;
using Godot;

namespace Spire2PileViewer;

/// <summary>
/// Minimal file logger. Each game instance in a local multiplayer test gets its own file
/// (spire2pileviewer_host_log.txt, spire2pileviewer_1000_log.txt, ...), same scheme as BetterSpire2.
/// </summary>
public static class ModLog
{
    private static readonly object _lock = new();
    private static string? _path;

    private static string LogPath => _path ??= Path.Combine(OS.GetUserDataDir(), "spire2pileviewer" + InstanceSuffix() + "_log.txt");

    public static void Init()
    {
        try
        {
            File.WriteAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Spire2PileViewer log started\n");
        }
        catch
        {
        }
    }

    public static void Info(string message) => Write(message);

    public static void Error(string context, Exception ex) => Write($"ERROR {context}: {ex}");

    private static void Write(string line)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {line}\n");
            }
        }
        catch
        {
        }
    }

    private static string InstanceSuffix()
    {
        try
        {
            string[] args = OS.GetCmdlineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-clientId" && i + 1 < args.Length) return "_" + args[i + 1];
                if (args[i].StartsWith("host_")) return "_host";
            }
        }
        catch
        {
        }
        return "";
    }
}
