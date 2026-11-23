namespace FortniteChestTracker.Core;

public sealed record ChestScanOptions(string ReplayDirectory, DateOnly Since);
public sealed record ChestScanProgress(int Completed, int Total, string FileName);
public sealed record SkippedReplay(string FileName, string Reason);

public sealed record ChestPlayer(string Name, string PlayerId, bool IsOwner, int? Chests, string Coverage);
public sealed record ChestOpeningTime(string PlayerId, float ReplayTimeSeconds);
public sealed record ChestPlayerDisconnect(string PlayerId, float? ReplayTimeSeconds);

public sealed record ChestReward(
    string PlayerName, string? PlayerId, bool IsOwner, bool IsTeammate,
    string Category, string Tier, string PrizeTemplateId, float? ReplayTimeSeconds,
    uint RewardActorGuid, int PrizeIndex)
{
    public string Evidence { get; init; } = ChestRewardEvidence.PrizeResult;
    public uint? SourceChestActorGuid { get; init; }
    public uint? AbilitySystemActorGuid { get; init; }
    public int? AbilitySpecId { get; init; }
    public int? GrantSequence { get; init; }
}

public static class ChestRewardEvidence
{
    public const string PrizeResult = "Successful prize result";
    public const string Receipt = "Treasure received (may be shared)";
}

public sealed record ChestMatch(
    string FilePath, string MatchId, string OwnerName, string OwnerId, string Mode,
    string Playlist, DateTime MatchDate, IReadOnlyList<ChestPlayer> Players, string Coverage)
{
    public string FileName => Path.GetFileName(FilePath);
    public uint RecordingLengthInMs { get; init; }
    // Null identifies legacy reports or unavailable search data. Missing timestamps
    // remain absent; consumers compare timed events with the recorded count.
    public IReadOnlyList<ChestOpeningTime>? OpeningTimeline { get; init; }
    public string? MatchmakingRegion { get; init; }
    public string MatchmakingRegionCoverage { get; init; } = "Reported matchmaking region unavailable";
    public IReadOnlyList<ChestPlayerDisconnect>? PlayerDisconnects { get; init; }
    public bool? DisconnectMonitoringAvailable { get; init; }
    public bool? IsStandardMode { get; init; }
    public IReadOnlyList<ChestReward> Rewards { get; init; } = [];
    public string RewardCoverage { get; init; } = ChestRewardCoverage.RecordedOnly;
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
    public int RecordedRewards => Matches.Sum(m => m.Rewards.Count);
}

public static class ChestRewardCoverage
{
    public const string RecordedOnly = "Recorded reward results and treasure receipts only; replay may miss distant rewards. Unknown-type receipts may be shared; Gizmo category cannot be confirmed.";
}

public static class ChestCoverage
{
    public const string Complete = "Complete chest data";
    public const string Unavailable = "Chest opening data unavailable";
}
