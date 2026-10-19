using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Models;

namespace LocalCoop.Mod.Runtime;

public static class BrokerPlayerDisplayNames
{
    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, string> ProfileKeysByPlayerId = [];

    private static readonly NameProfile UnknownProfile = new(
        "Unknown",
        [
            "Fabled",
            "Dusk",
            "Curious",
            "Wobbly",
            "Brave Enough",
            "Pocket",
            "Mystery",
            "Velvet",
            "Shiny",
            "Tiny"
        ],
        [
            "Hero",
            "Relic",
            "Crown",
            "Odyssey",
            "Blade",
            "Lord",
            "Sage",
            "Champion",
            "Wanderer",
            "Legend"
        ]);

    private static readonly Dictionary<string, NameProfile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ironclad"] = new(
            "Ironclad",
            [
                "Ember",
                "Iron",
                "Bashful",
                "Forge",
                "Blazing",
                "Snackforged",
                "Rusty",
                "Hearth",
                "Mighty",
                "Bonk"
            ],
            [
                "Bulwark",
                "Blade",
                "Hero",
                "Lord",
                "Hammer",
                "Champion",
                "Shield",
                "Crown",
                "Brawler",
                "Paladin"
            ]),
        ["Silent"] = new(
            "Silent",
            [
                "Dusk",
                "Venom",
                "Moonlit",
                "Quiet",
                "Velvet",
                "Winking",
                "Shadow",
                "Pocket",
                "Dagger",
                "Whisper"
            ],
            [
                "Blade",
                "King",
                "Odyssey",
                "Rogue",
                "Viper",
                "Waltz",
                "Crown",
                "Dancer",
                "Sage",
                "Bandit"
            ]),
        ["Regent"] = new(
            "Regent",
            [
                "Fabled",
                "Velvet",
                "Tiny",
                "Gleaming",
                "Royal",
                "Starry",
                "Fancy",
                "Dramatic",
                "Crowned",
                "Polished"
            ],
            [
                "Crown",
                "Majesty",
                "Monarch",
                "Scepter",
                "Lord",
                "Oracle",
                "Baron",
                "Pageant",
                "Dynasty",
                "Taxman"
            ]),
        ["Necrobinder"] = new(
            "Necrobinder",
            [
                "Lantern",
                "Bone",
                "Grave",
                "Dusty",
                "Cozy",
                "Moonlit",
                "Crypt",
                "Mildly Haunted",
                "Candle",
                "Rattling"
            ],
            [
                "Warden",
                "Librarian",
                "Sage",
                "Binder",
                "Lord",
                "Crown",
                "Archivist",
                "Odyssey",
                "Caretaker",
                "Bookkeeper"
            ]),
        ["Defect"] = new(
            "Defect",
            [
                "Static",
                "Frost",
                "Spark",
                "Polite",
                "Orbital",
                "Chrome",
                "Overclocked",
                "Friendly",
                "Clockwork",
                "Zap"
            ],
            [
                "Oracle",
                "Baron",
                "Odyssey",
                "Gadget",
                "Lord",
                "Circuit",
                "Crown",
                "Protocol",
                "Sage",
                "Knight"
            ])
    };

    public static void RegisterCharacter(ulong playerId, CharacterModel? character)
    {
        if (BrokerPlayerId.ToClientIndex(playerId) < 0)
        {
            return;
        }

        lock (Gate)
        {
            ProfileKeysByPlayerId[playerId] = ProfileKeyFor(character);
        }
    }

    public static bool TryGetName(ulong playerId, out string name)
    {
        if (BrokerPlayerId.ToClientIndex(playerId) < 0)
        {
            name = string.Empty;
            return false;
        }

        string? profileKey;
        lock (Gate)
        {
            ProfileKeysByPlayerId.TryGetValue(playerId, out profileKey);
        }

        var profile = ProfileFor(profileKey);
        name = Generate(playerId, profile);
        return true;
    }

    public static void ClearForTesting()
    {
        lock (Gate)
        {
            ProfileKeysByPlayerId.Clear();
        }
    }

    public static Regex CharacterNamePatternForTesting(string profileKey)
    {
        var profile = ProfileFor(profileKey);
        var prefixes = string.Join("|", profile.Prefixes.Select(Regex.Escape));
        var nouns = string.Join("|", profile.Nouns.Select(Regex.Escape));
        return new Regex($"^({prefixes}) ({nouns})$", RegexOptions.CultureInvariant);
    }

    private static string Generate(ulong playerId, NameProfile profile)
    {
        var prefix = profile.Prefixes[SelectIndex(playerId, profile.Key, salt: 0, profile.Prefixes.Count)];
        var noun = profile.Nouns[SelectIndex(playerId, profile.Key, salt: 1, profile.Nouns.Count)];
        return $"{prefix} {noun}";
    }

    private static int SelectIndex(ulong playerId, string profileKey, int salt, int count)
    {
        var hash = 14695981039346656037UL;
        foreach (var character in profileKey)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }

        hash ^= playerId + 0x9E3779B97F4A7C15UL + ((ulong)salt * 0xBF58476D1CE4E5B9UL);
        hash ^= hash >> 30;
        hash *= 0xBF58476D1CE4E5B9UL;
        hash ^= hash >> 27;
        hash *= 0x94D049BB133111EBUL;
        hash ^= hash >> 31;
        return (int)(hash % (uint)count);
    }

    private static NameProfile ProfileFor(string? profileKey)
    {
        return profileKey is not null && Profiles.TryGetValue(profileKey, out var profile)
            ? profile
            : UnknownProfile;
    }

    private static string ProfileKeyFor(CharacterModel? character)
    {
        var typeName = character?.GetType().Name;
        var idEntry = SafeIdEntry(character);

        foreach (var key in Profiles.Keys)
        {
            if (ContainsProfileName(typeName, key) || ContainsProfileName(idEntry, key))
            {
                return key;
            }
        }

        return UnknownProfile.Key;
    }

    private static string? SafeIdEntry(CharacterModel? character)
    {
        try
        {
            return character?.Id?.Entry;
        }
        catch
        {
            return null;
        }
    }

    private static bool ContainsProfileName(string? value, string profileKey)
    {
        return value?.Contains(profileKey, StringComparison.OrdinalIgnoreCase) == true;
    }

    private sealed record NameProfile(string Key, IReadOnlyList<string> Prefixes, IReadOnlyList<string> Nouns);
}
