namespace StreamTweakService;

/// <summary>
/// The service's own log (10.3.0), for what happens while nobody is signed in — a Fast Startup
/// shut down logs every user off before the moment that matters, so StreamTweak's debug.log
/// cannot have it, and the Event Log only takes warnings from this host.
///
/// %ProgramData%\FoggyBytes\StreamTweak\logs\service.log. The folder is made by the setup with
/// its own rights (SYSTEM and administrators full, users read — Installer.iss), never by the
/// service: this process is LocalSystem, and a folder a user had made first, or a link planted on
/// the way, would let them point its writes at any file. So it writes only into a folder that is
/// there and is no link, and otherwise it writes nothing. Never throws.
/// </summary>
public static class ServiceLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object _lock = new();

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "FoggyBytes", "StreamTweak", "logs");

    public static void Write(string line)
    {
        try
        {
            lock (_lock)
            {
                if (!FolderIsSafe()) return;
                string file = Path.Combine(Folder, "service.log");
                var info = new FileInfo(file);
                if (info.Exists)
                {
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
                    if (info.Length > MaxBytes)
                        File.Move(file, file + ".1", overwrite: true);
                }
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // A log line is never worth an exception in a power callback.
        }
    }

    private static bool FolderIsSafe()
    {
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string path = common;
        foreach (string part in new[] { "FoggyBytes", "StreamTweak", "logs" })
        {
            path = Path.Combine(path, part);
            var dir = new DirectoryInfo(path);
            if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        return true;
    }
}
