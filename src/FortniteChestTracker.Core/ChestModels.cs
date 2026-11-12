namespace FortniteChestTracker.Core;

public sealed record ChestScanOptions(string ReplayDirectory, DateOnly Since);
public sealed record ChestScanProgress(int Completed, int Total, string FileName);
public sealed record SkippedReplay(string FileName, string Reason);

public sealed record ChestPlayer(string Name, string PlayerId, bool IsOwner, int? Chests, string Coverage);

public sealed record ChestMatch(
    string FilePath, string MatchId, string OwnerName, string OwnerId, string Mode,
    string Playlist, DateTime MatchDate, IReadOnlyList<ChestPlayer> Players, string Coverage)
{
    public string FileName => Path.GetFileName(FilePath);
    public uint RecordingLengthInMs { get; init; }
    public int? OwnerChests => Players.Single(p => p.IsOwner).Chests;
    public int? TeammateChests => Coverage.Contains("team membership unavailable", StringComparison.Ordinal)
        ? null : SumKnown(Players.Where(p => !p.IsOwner));
    public int? SquadChests => Coverage.Contains("team membership unavailable", StringComparison.Ordinal)
        ? null : SumKnown(Players);

    private static int? SumKnown(IEnumerable<ChestPlayer> players)
    {
        var values = players.ToArray();
        return values.Any(p => p.Chests is null) ? null : values.Sum(p => p.Chests!.Value);
    }
}

public sealed record ChestScanReport(
    IReadOnlyList<ChestMatch> Matches, IReadOnlyList<SkippedReplay> Skipped, int FilesScanned)
{
    // A missing counter contributes no value to the recorded subtotal, and remains null in match rows.
    public int OwnerChests => Matches.Sum(m => m.OwnerChests ?? 0);
    public int TeammateChests => Matches.Sum(m => m.Players.Where(p => !p.IsOwner).Sum(p => p.Chests ?? 0));
    public int SquadChests => OwnerChests + TeammateChests;
    public int IncompleteMatches => Matches.Count(m => m.SquadChests is null || m.Coverage != ChestCoverage.Complete);
}

public static class ChestCoverage
{
    public const string Complete = "Complete chest data";
    public const string Unavailable = "Chest opening data unavailable";
}
