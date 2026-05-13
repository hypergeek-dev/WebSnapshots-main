// Logger.cs
using System.Diagnostics;
using System.Text;

namespace WebSnapshots;

public sealed class Logger : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    public string LogFilePath { get; }

    public Logger(string logFilePath)
    {
        LogFilePath = logFilePath;

        var dir = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        _writer = new StreamWriter(File.Open(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
            NewLine = "\n"
        };
    }

    public void Info(string msg) => WriteLine("INFO", msg);
    public void Warn(string msg) => WriteLine("WARN", msg);
    public void Error(string msg) => WriteLine("ERROR", msg);

    public void Event(string name, params (string Key, object? Value)[] fields)
    {
        var sb = new StringBuilder();
        sb.Append(name);

        if (fields != null)
        {
            foreach (var kv in fields)
            {
                sb.Append(' ');
                sb.Append(kv.Key);
                sb.Append('=');
                sb.Append(FormatValue(kv.Value));
            }
        }

        WriteLine("EVT", sb.ToString());
    }

    public IDisposable Scope(string name, params (string Key, object? Value)[] fields)
    {
        Event(name + "_START", fields);
        var sw = Stopwatch.StartNew();

        return new ScopeGuard(() =>
        {
            sw.Stop();

            var endFields = new (string Key, object? Value)[(fields?.Length ?? 0) + 1];
            if (fields != null)
            {
                for (int i = 0; i < fields.Length; i++)
                    endFields[i] = fields[i];
            }
            endFields[endFields.Length - 1] = ("ms", sw.ElapsedMilliseconds);

            Event(name + "_END", endFields);
        });
    }

    private void WriteLine(string level, string msg)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} +{_sw.ElapsedMilliseconds,6}ms {level} {msg}";

        lock (_lock)
        {
            _writer.WriteLine(line);
        }

        Console.WriteLine(line);
    }

    private static string FormatValue(object? v)
    {
        if (v is null) return "null";

        if (v is bool b) return b ? "true" : "false";

        if (v is Exception ex)
            return QuoteIfNeeded(ex.GetType().Name + ": " + ex.Message);

        if (v is string s)
            return QuoteIfNeeded(s);

        return QuoteIfNeeded(v.ToString() ?? "");
    }

    private static string QuoteIfNeeded(string s)
    {
        if (s.Length == 0) return "\"\"";

        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch) || ch is '=' or '"' or '\\')
                return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        return s;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }

    private sealed class ScopeGuard : IDisposable
    {
        private readonly Action _onDispose;
        private bool _disposed;

        public ScopeGuard(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _onDispose();
        }
    }
}
