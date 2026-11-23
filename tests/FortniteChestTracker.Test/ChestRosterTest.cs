using FortniteReplayReader;
using FortniteReplayReader.Analysis;
using FortniteReplayReader.Models;
using FortniteReplayReader.Models.NetFieldExports;
using Xunit;

namespace FortniteChestTracker.Core.Test;

public class ChestRosterTest
{
    [Fact]
    public void OnlyRecordedHumanSquadMembersContributeToTotals()
    {
        var match = Build("match-a", [Player("owner", 4), Player("teammate", 4),
            Player("enemy", 7), Player("hired-npc", 4, bot: true)],
            [Opening(1, "OWNER"), Opening(2, "teammate"), Opening(3, "enemy"), Opening(4, "hired-npc")]);

        Assert.Equal(["owner", "teammate"], match.Players.Select(player => player.PlayerId));
        Assert.Equal(1, match.OwnerChests);
        Assert.Equal(1, match.TeammateChests);
        Assert.Equal(2, match.SquadChests);
    }

    [Fact]
    public void MissingIdentitiesRemainSeparateAndMakeTeammateTotalUnknown()
    {
        var match = Build("match-a", [Player("owner", 4), Player(null, 4), Player(null, 4)],
            [Opening(1, "owner"), Opening(2, null)]);

        Assert.Equal(3, match.Players.Count);
        Assert.Equal(3, match.Players.Select(player => player.PlayerId).Distinct().Count());
        Assert.All(match.Players.Where(player => !player.IsOwner), player => Assert.Null(player.Chests));
        Assert.Null(match.TeammateChests);
        Assert.Null(match.SquadChests);
        Assert.Equal(1, new ChestScanReport([match], [], 1).SquadChests);
    }

    [Fact]
    public void TimelinePreservesOnlyFiniteAttributedHumanSquadOpenings()
    {
        var match = Build("match-a", [Player("owner", 4), Player("teammate", 4), Player("enemy", 7), Player("bot", 4, true)],
            [Opening(1, "OWNER") with { ReplayTimeSeconds = 42 },
             Opening(2, "teammate") with { ReplayTimeSeconds = 30, IsDestruction = true },
             Opening(3, "owner") with { ReplayTimeSeconds = null },
             Opening(4, "owner") with { ReplayTimeSeconds = float.NaN },
             Opening(5, "owner") with { ReplayTimeSeconds = -1 },
             Opening(6, "enemy"), Opening(7, "bot"), Opening(8, null)]);
        Assert.Equal([30f, 42f], match.OpeningTimeline!.Select(o => o.ReplayTimeSeconds));
        Assert.Equal(["teammate", "OWNER"], match.OpeningTimeline!.Select(o => o.PlayerId));
        Assert.Equal(4, match.OwnerChests); // Missing timing never removes counted openings.
    }

    [Fact]
    public void TimelineDistinguishesUnavailableDataFromRecordedZero()
    {
        Assert.Null(Build("match-a", [Player("owner", 4)], [], fields: 0).OpeningTimeline);
        Assert.Empty(Build("match-a", [Player("owner", 4)], []).OpeningTimeline!);
    }

    [Fact]
    public void ConsistentHumanReportsExposeBroadRegionAndExcludeBots()
    {
        var match = Build("match-a", [PlayerWithRegion("owner", 4, " na "), PlayerWithRegion("enemy", 7, "NA"),
            PlayerWithRegion("bot", 4, "EU", true)], []);
        Assert.Equal("NA", match.MatchmakingRegion);
        Assert.Contains("exact server/subregion unconfirmed", match.MatchmakingRegionCoverage);
    }

    [Fact]
    public void ConflictingAndUnrecognizedRegionsRemainUnknown()
    {
        var conflicting = Build("match-a", [PlayerWithRegion("owner", 4, "EU"), PlayerWithRegion("enemy", 7, "NA")], []);
        Assert.Null(conflicting.MatchmakingRegion);
        Assert.Contains("Conflicting", conflicting.MatchmakingRegionCoverage);
        Assert.Null(Build("match-a", [PlayerWithRegion("owner", 4, "not-a-region")], []).MatchmakingRegion);
        Assert.Null(Build("match-a", [Player("owner", 4)], []).MatchmakingRegion);
    }

    [Fact]
    public void RegionReportsSurviveAbsentUpdatesAndPreserveChanges()
    {
        var builder = new FortniteReplayBuilder();
        builder.UpdatePlayerState(0, PlayerWithRegion("owner", 4, "EU"));
        builder.UpdatePlayerState(0, new());
        builder.UpdatePlayerState(0, new() { KeepPlayingTogetherMatchmakingRegion = "NA" });
        var player = Assert.Single(builder.Build(new FortniteReplay()).PlayerData);
        Assert.Equal(["EU", "NA"], player.ReportedMatchmakingRegions.Order());
    }

    [Fact]
    public void DisconnectFlagReflectsLaterUpdatesInsteadOfFreezingInitialFalse()
    {
        var builder = new FortniteReplayBuilder();
        builder.UpdatePlayerState(0, Player("owner", 4));
        builder.UpdatePlayerState(0, new() { bIsDisconnected = false });
        builder.UpdatePlayerState(0, new() { bIsDisconnected = true });
        Assert.True(Assert.Single(builder.Build(new FortniteReplay()).PlayerData).Disconnected);
        builder.UpdatePlayerState(0, new() { bIsDisconnected = false });
        Assert.False(Assert.Single(builder.Build(new FortniteReplay()).PlayerData).Disconnected);
    }

    [Fact]
    public void SquadDisconnectsExcludeBotsAndOpponentsAndKeepUnavailableTimesNull()
    {
        var match = Build("match-a", [Player("owner", 4), Player("teammate", 4), Player("enemy", 7), Player("bot", 4, true)], [],
            disconnects: [new(1, "OWNER", 50), new(2, "teammate", float.NaN), new(3, "enemy", 70), new(4, "bot", 80)], monitoring: true);
        Assert.True(match.DisconnectMonitoringAvailable);
        Assert.Equal(2, match.PlayerDisconnects!.Count);
        Assert.Equal("OWNER", match.PlayerDisconnects[0].PlayerId);
        Assert.Null(match.PlayerDisconnects[1].ReplayTimeSeconds);
    }

    [Fact]
    public void AnonymousMembersCannotBeJoinedAcrossSeparateMatches()
    {
        var first = Build("match-a", [Player("owner", 4), Player(null, 4)], []);
        var second = Build("match-b", [Player("owner", 4), Player(null, 4)], []);

        Assert.NotEqual(first.Players[1].PlayerId, second.Players[1].PlayerId);
    }

    [Fact]
    public void MissingOwnerTeamDoesNotTreatAllUnassignedPlayersAsTeammates()
    {
        var match = Build("match-a", [Player("owner", null), Player("other", null)],
            [Opening(1, "owner"), Opening(2, "other")]);

        Assert.Single(match.Players);
        Assert.Equal(1, match.OwnerChests);
        Assert.Null(match.TeammateChests);
        Assert.Null(match.SquadChests);
        Assert.Contains("team membership unavailable", match.Coverage);
    }

    [Fact]
    public void NoDecodedSearchFieldsProducesUnknownCountsInsteadOfZero()
    {
        var match = Build("match-a", [Player("owner", 4), Player("teammate", 4)], [], fields: 0);

        Assert.All(match.Players, player => Assert.Null(player.Chests));
        Assert.Equal(ChestCoverage.Unavailable, match.Coverage);
    }

    private static ChestMatch Build(string matchId, FortPlayerState[] players, ReplayChestOpening[] openings, int fields = 1,
        ReplayPlayerDisconnect[]? disconnects = null, bool monitoring = false)
    {
        var builder = new FortniteReplayBuilder();
        for (uint channel = 0; channel < players.Length; channel++) builder.UpdatePlayerState(channel, players[channel]);
        var replay = builder.Build(new FortniteReplay { Info = new() { LengthInMs = 100_000 } });
        replay.GameData.CurrentPlaylist = "Playlist_DefaultSquad";
        var owner = replay.PlayerData.Single(player => player.PlayerId == "owner");
        owner.IsReplayOwner = true;
        return ChestMatchFactory.Build(new(replay, openings, [], 1, fields, false) { Disconnects = disconnects ?? [], DisconnectMonitoringAvailable = monitoring }, owner,
            Path.Combine(Path.GetTempPath(), matchId + ".replay"), matchId, "Battle Royale · Build", new(2026, 11, 10));
    }

    private static FortPlayerState Player(string? id, int? team, bool bot = false) => new()
    {
        UniqueId = id!, TeamIndex = team, bIsABot = bot, BotUniqueId = bot ? id : null
    };
    private static FortPlayerState PlayerWithRegion(string id, int team, string region, bool bot = false)
    {
        var player = Player(id, team, bot);
        player.KeepPlayingTogetherMatchmakingRegion = region;
        return player;
    }

    private static ReplayChestOpening Opening(uint actor, string? player) => new(actor, "Chest", null, null, player, 1);
}
