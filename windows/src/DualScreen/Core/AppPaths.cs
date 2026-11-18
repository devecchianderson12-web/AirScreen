using System;
using System.IO;

namespace DualScreen.Core;

/// <summary>
/// Resolves where DualScreen keeps its data. By default %LOCALAPPDATA%\DualScreen.
/// If a file named "portable.txt" sits next to the executable, data is kept in ".\DualScreenData" instead,
/// so the program can run fully self-contained from a USB drive.
/// </summary>
public static class AppPaths
{
    public static string ExePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DualScreen.exe");
    public static string ExeDir { get; } = Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;

    public static string DataDir { get; } = ResolveDataDir();
    public static string LogDir => Ensure(Path.Combine(DataDir, "logs"));
    public static string UpdateDir => Ensure(Path.Combine(DataDir, "update"));
    public static string DriverDir => Ensure(Path.Combine(DataDir, "driver"));
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string DevicesFile => Path.Combine(DataDir, "devices.json");
    public static string IdentityFile => Path.Combine(DataDir, "identity.bin");

    private static string ResolveDataDir()
    {
        string dir;
        if (File.Exists(Path.Combine(ExeDir, "portable.txt")))
            dir = Path.Combine(ExeDir, "DualScreenData");
        else
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.ProductName);
        return Ensure(dir);
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
