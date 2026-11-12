namespace FortniteChestTracker.Core;

public static class ChestEligibility
{
    private static readonly HashSet<string> Build = new(StringComparer.OrdinalIgnoreCase)
    {
        "Playlist_DefaultSolo", "Playlist_DefaultDuo", "Playlist_DefaultTrio", "Playlist_DefaultSquad"
    };
    private static readonly HashSet<string> ZeroBuild = new(StringComparer.OrdinalIgnoreCase)
    {
        "Playlist_NoBuildBR_Solo", "Playlist_NoBuildBR_Duo", "Playlist_NoBuildBR_Trio", "Playlist_NoBuildBR_Squad"
    };

    public static string? GetMode(string? playlist, bool isTournament = false, bool isLargeTeam = false)
    {
        if (string.IsNullOrWhiteSpace(playlist) || isTournament || isLargeTeam) return null;
        // Readers may retain either a full asset path or its normalized leaf name.
        var name = playlist.Trim().Trim('\'', '"').Split('/').Last().Split('.').Last();
        if (Build.Contains(name)) return "Battle Royale · Build";
        if (ZeroBuild.Contains(name)) return "Battle Royale · Zero Build";
        return null;
    }

    public static bool IsOtherSubgame(IEnumerable<string>? gameSpecificData) => gameSpecificData?.Any(value =>
        value.StartsWith("SubGame=", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("SubGame=Athena", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("VerseURI=", StringComparison.OrdinalIgnoreCase) &&
        (value.Contains("/GameFeatures/CreativeRoot", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("/GameFeatures/VKPlayMinimumRoot", StringComparison.OrdinalIgnoreCase))) == true;

    public static DateTime LocalMatchDate(DateTime? utcMatchStart, DateTime recordingTimestamp)
    {
        // Fortnite's FDateTime has no kind; the game field is UTC, whereas local-file metadata is local.
        if (utcMatchStart is { } start)
            return DateTime.SpecifyKind(start, DateTimeKind.Utc).ToLocalTime();
        return recordingTimestamp.Kind == DateTimeKind.Utc ? recordingTimestamp.ToLocalTime() : recordingTimestamp;
    }
}
