namespace LocalCoop.MultiClientHarness;

public static class GameExecutablePathResolver
{
    public static string ResolveDefault(string baseDirectory)
    {
        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            if (string.Equals(directory.Name, "LocalCoopMod", StringComparison.OrdinalIgnoreCase)
                && directory.Parent is not null)
            {
                return Path.Combine(directory.Parent.FullName, "SlayTheSpire2.exe");
            }

            directory = directory.Parent;
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "..", "SlayTheSpire2.exe"));
    }
}
