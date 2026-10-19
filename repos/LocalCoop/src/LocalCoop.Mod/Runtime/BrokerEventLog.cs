using System.Collections.Concurrent;

namespace LocalCoop.Mod.Runtime;

public sealed class BrokerEventLog
{
    private static readonly ConcurrentDictionary<string, object> LocksByPath = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;

    public BrokerEventLog(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Log path must not be blank.", nameof(path));
        }

        _path = Path.GetFullPath(path);
    }

    public void Write(string message)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var fileLock = LocksByPath.GetOrAdd(_path, static _ => new object());
        lock (fileLock)
        {
            File.AppendAllLines(_path, [$"{DateTimeOffset.Now:O} {message}"]);
        }
    }
}
