namespace LocalCoop.MultiClientHarness;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        if (string.Equals(args[0], "prepare-clients", StringComparison.OrdinalIgnoreCase))
        {
            return PrepareClients(args);
        }

        if (string.Equals(args[0], "prepare-two-client", StringComparison.OrdinalIgnoreCase))
        {
            return PrepareTwoClient(args);
        }

        PrintUsage();
        return 1;
    }

    private static int PrepareClients(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("Missing configRoot or clientCount.");
            return 1;
        }

        var rootDirectory = args[1];
        if (!int.TryParse(args[2], out var clientCount))
        {
            Console.WriteLine("clientCount must be an integer from 2 through 4.");
            return 1;
        }

        var sessionId = args.Length > 3 ? args[3] : "local-test";
        var port = args.Length > 4 && int.TryParse(args[4], out var parsedPort) ? parsedPort : 38989;
        var gameExe = args.Length > 5
            ? args[5]
            : GameExecutablePathResolver.ResolveDefault(AppContext.BaseDirectory);
        var controllerDevices = args.Length > 6 ? args[6] : null;

        ClientHarnessPreparationResult result;
        try
        {
            result = ClientHarnessPreparation.Prepare(
                rootDirectory,
                gameExe,
                sessionId,
                "127.0.0.1",
                port,
                clientCount,
                controllerDevices);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }

        PrintResult(result.BrokerCommand, result.LaunchCommands);
        return 0;
    }

    private static int PrepareTwoClient(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Missing configRoot.");
            return 1;
        }

        var rootDirectory = args[1];
        var sessionId = args.Length > 2 ? args[2] : "local-test";
        var port = args.Length > 3 && int.TryParse(args[3], out var parsedPort) ? parsedPort : 38989;
        var gameExe = args.Length > 4
            ? args[4]
            : GameExecutablePathResolver.ResolveDefault(AppContext.BaseDirectory);

        TwoClientHarnessPreparationResult result;
        try
        {
            result = TwoClientHarnessPreparation.Prepare(rootDirectory, gameExe, sessionId, "127.0.0.1", port);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }

        PrintResult(result.BrokerCommand, result.LaunchCommands);
        return 0;
    }

    private static void PrintResult(string brokerCommand, IReadOnlyList<string> launchCommands)
    {
        Console.WriteLine("Broker:");
        Console.WriteLine(brokerCommand);
        Console.WriteLine();
        Console.WriteLine("Clients:");
        foreach (var command in launchCommands)
        {
            Console.WriteLine(command);
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project tools\\LocalCoop.MultiClientHarness -- prepare-clients <configRoot> <clientCount> [sessionId] [port] [gameExe] [controllerDevices]");
        Console.WriteLine("  dotnet run --project tools\\LocalCoop.MultiClientHarness -- prepare-two-client <configRoot> [sessionId] [port] [gameExe]");
    }
}
