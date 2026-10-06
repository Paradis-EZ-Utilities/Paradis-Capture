using System.Collections.Concurrent;
using System.Text;

namespace ParadisCapture.Services;

/// <summary>
/// Small bounded file logger: %LocalAppData%\ParadisCapture\Logs\ParadisCapture-yyyyMMdd.log.
/// Writes happen on a background thread. Each file is capped at 5 MB and only the 10 newest
/// files are kept, so logs can never grow without bound.
/// </summary>
public static class Log
{
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const int MaxFiles = 10;

    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 10_000);
    private static Thread? _writer;
    private static string _directory = "";

    public static string Directory => _directory;

    public static void Initialize()
    {
        _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ParadisCapture", "Logs");
        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            Prune();
        }
        catch
        {
            // Logging must never take the app down.
        }

        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "Log writer", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    public static void Info(string message) => Enqueue("INFO ", message);

    public static void Warn(string message) => Enqueue("WARN ", message);

    public static void Warn(string message, Exception ex) => Enqueue("WARN ", $"{message}: {Describe(ex)}");

    public static void Error(string message, Exception? ex = null) =>
        Enqueue("ERROR", ex == null ? message : $"{message}{Environment.NewLine}{ex}");

    public static string Describe(Exception ex) => $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}";

    /// <summary>Flushes pending lines (best effort, bounded wait). Call on exit.</summary>
    public static void Shutdown()
    {
        Queue.CompleteAdding();
        _writer?.Join(TimeSpan.FromSeconds(2));
    }

    private static void Enqueue(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [{Environment.CurrentManagedThreadId,3}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        try { Queue.TryAdd(line); } catch (InvalidOperationException) { /* shutting down */ }
    }

    private static void WriterLoop()
    {
        StreamWriter? writer = null;
        string? currentPath = null;
        try
        {
            foreach (string line in Queue.GetConsumingEnumerable())
            {
                try
                {
                    string path = CurrentPath();
                    if (writer == null || path != currentPath)
                    {
                        writer?.Dispose();
                        if (currentPath != null) Prune();
                        currentPath = path;
                        writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                    }
                    writer.WriteLine(line);
                    if (Queue.Count == 0) writer.Flush();
                }
                catch
                {
                    writer?.Dispose();
                    writer = null;
                }
            }
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private static string CurrentPath()
    {
        string basePath = Path.Combine(_directory, $"ParadisCapture-{DateTime.Now:yyyyMMdd}");
        for (int i = 0; ; i++)
        {
            string p = i == 0 ? basePath + ".log" : $"{basePath}-{i}.log";
            var fi = new FileInfo(p);
            if (!fi.Exists || fi.Length < MaxFileBytes) return p;
            if (i > 20) return p; // absurd volume; keep appending to the last one rather than spinning
        }
    }

    private static void Prune()
    {
        var files = new DirectoryInfo(_directory).GetFiles("ParadisCapture-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(MaxFiles - 1);
        foreach (var f in files)
        {
            try { f.Delete(); } catch { }
        }
    }
}
