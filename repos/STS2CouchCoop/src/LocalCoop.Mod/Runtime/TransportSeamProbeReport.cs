namespace LocalCoop.Mod.Runtime;

public static class TransportSeamProbeReport
{
    public static IReadOnlyList<string> Format(TransportSeamProbeResult result)
    {
        var lines = new List<string>
        {
            "LocalCoop transport seam probe"
        };

        foreach (var entry in result.Entries)
        {
            lines.Add($"{entry.Label}: {entry.TypeName}");
            lines.AddRange(entry.Members.Select(member => $"  {member}"));
        }

        return lines;
    }
}

public sealed record TransportSeamProbeResult(IReadOnlyList<TransportSeamProbeEntry> Entries);

public sealed record TransportSeamProbeEntry(string Label, string TypeName, IReadOnlyList<string> Members);

