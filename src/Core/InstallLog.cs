using System.Text;

namespace AStoryWealth.Core;

/// <summary>
/// File + console logger. One log per run: instance/install-YYYYMMDD-HHMMSS.log
/// plus a rolling tail at instance/install.log. Crashes are diagnosable from
/// the last timestamped lines; every phase change is logged explicitly.
/// </summary>
public static class InstallLog
{
    private static string? _logPath;
    private static readonly object Gate = new();
    private static StreamWriter? _swLog;
    private static StreamWriter? _swTail;
    private static string? _tailPath;

    public static void Init(string instanceDir)
    {
        Directory.CreateDirectory(instanceDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _logPath = Path.Combine(instanceDir, $"install-{stamp}.log");
        _tailPath = Path.Combine(instanceDir, "install.log");

        // A second Init in the same process must release the earlier writers
        // first, or opening the new FileStreams throws IOException (sharing violation).
        DisposeWriters();

        _swLog = new StreamWriter(new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false };
        _swTail = new StreamWriter(new FileStream(_tailPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false };
        Info("log", $"install log started — instance {instanceDir}");
    }

    /// <summary>Releases the file handles held by the current writers, tolerating
    /// a partially-disposed or already-closed writer.</summary>
    private static void DisposeWriters()
    {
        if (_swLog != null) { try { _swLog.Dispose(); } catch { } _swLog = null; }
        if (_swTail != null) { try { _swTail.Dispose(); } catch { } _swTail = null; }
    }

    public static void Info(string phase, string msg) => Write("INFO", phase, msg);
    public static void Warn(string phase, string msg) => Write("WARN", phase, msg);
    public static void Error(string phase, string msg) => Write("ERROR", phase, msg);

    /// <summary>Raised with every formatted line (file logging and UI).</summary>
    public static event Action<string>? Written;

    private static void Write(string level, string phase, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level,-5}] [{phase}] {msg}";
        lock (Gate)
        {
            Console.WriteLine(line);
            try
            {
                if (_swLog != null) { _swLog.WriteLine(line); _swLog.Flush(); }
                if (_swTail != null) { _swTail.WriteLine(line); _swTail.Flush(); }
            }
            catch { /* never let logging kill the install */ }
            try { Written?.Invoke(line); } catch { }
        }
    }
}
