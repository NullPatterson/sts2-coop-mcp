using LocalCoop.Broker;
using System.Net;

var sessionId = args.Length > 0 ? args[0] : "local-default";
var port = args.Length > 1 && int.TryParse(args[1], out var parsedPort) ? parsedPort : 38989;
var logPath = args.Length > 2 ? args[2] : Environment.GetEnvironmentVariable("LOCALCOOP_BROKER_LOG");
using var logWriter = CreateLogWriter(logPath);

void WriteLog(string message)
{
    Console.WriteLine(message);
    logWriter?.WriteLine(message);
}

await using var server = new BrokerTcpServer(sessionId, IPAddress.Loopback, port, WriteLog);
await server.StartAsync(CancellationToken.None);

WriteLog($"LocalCoop broker session '{sessionId}' listening on 127.0.0.1:{server.Port}.");
WriteLog("Press Ctrl+C to stop.");

var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stop.TrySetResult();
};

await stop.Task;

static StreamWriter? CreateLogWriter(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return null;
    }

    var fullPath = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    return new StreamWriter(new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
    {
        AutoFlush = true
    };
}
