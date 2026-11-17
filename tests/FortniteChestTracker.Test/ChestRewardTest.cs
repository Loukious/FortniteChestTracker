using System.Text;
using System.Text.Json;
using FortniteReplayReader;
using FortniteReplayReader.Analysis;
using FortniteReplayReader.Models;
using FortniteReplayReader.Models.NetFieldExports;
using Xunit;

namespace FortniteChestTracker.Core.Test;

public class ChestRewardTest
{
    [Fact]
    public void RewardsIncludeOtherHumanPlayersWithoutAddingThemToSquadChestTotals()
    {
        var match = Match([
            Reward(100, "OWNER", "DelilahPrize:dpid_item15"),
            Reward(101, "friend", "DelilahPrize:dpid_item17"),
            Reward(102, "other", "DelilahPrize:dpid_item1"),
            Reward(103, "bot", "DelilahPrize:dpid_item17")]);

        Assert.Equal(3, match.Rewards.Count);
        Assert.True(match.Rewards[0].IsOwner);
        Assert.True(match.Rewards[1].IsTeammate);
        Assert.False(match.Rewards[2].IsOwner);
        Assert.False(match.Rewards[2].IsTeammate);
        Assert.Equal("Gold outfit", match.Rewards[2].Category);
        Assert.Equal("Supreme", match.Rewards[2].Tier);
        Assert.Equal(["owner", "friend"], match.Players.Select(player => player.PlayerId));
        Assert.Equal(2, match.SquadChests);
    }

    [Fact]
    public void SamePrizeForDifferentActorsOrArrayElementsRemainsSeparate()
    {
        var first = Reward(100, "friend", "DelilahPrize:dpid_item17");
        var match = Match([first, first, Reward(101, "other", first.PrizeTemplateId),
            Reward(100, "friend", first.PrizeTemplateId, index: 1)]);

        Assert.Equal(3, match.Rewards.Count);
        Assert.Equal(3, new ChestScanReport([match], [], 1).RecordedRewards);
    }

    [Fact]
    public void UnknownRecipientAndUnknownPrizeAreRetainedWithoutGuessing()
    {
        var match = Match([Reward(100, null, "future-prize")]);
        var reward = Assert.Single(match.Rewards);

        Assert.Null(reward.PlayerId);
        Assert.Equal("Unidentified player", reward.PlayerName);
        Assert.False(reward.IsOwner);
        Assert.False(reward.IsTeammate);
        Assert.Equal("Treasure (unrecognized reward)", reward.Category);
        Assert.Equal("Unknown", reward.Tier);
        Assert.Contains("recipients could not be identified", match.RewardCoverage);
    }

    [Fact]
    public void EmptyRewardsAndRewardReadFailuresDoNotChangeChestCoverage()
    {
        var baseline = Match([]);
        var errors = Match([], rewardErrors: true);

        Assert.Empty(errors.Rewards);
        Assert.Equal(0, new ChestScanReport([errors], [], 1).RecordedRewards);
        Assert.Equal(baseline.Coverage, errors.Coverage);
        Assert.Equal(baseline.SquadChests, errors.SquadChests);
        Assert.Contains("Some reward updates could not be read", errors.RewardCoverage);
        Assert.Contains("Gizmo category cannot be confirmed", errors.RewardCoverage);
    }

    [Fact]
    public void DuplicateRecordingsDoNotAddRewardRecordsTogether()
    {
        var original = Match([Reward(100, "owner", "DelilahPrize:dpid_item15")]);
        var copy = original with { FilePath = "edited-copy.replay" };
        var report = ChestReportBuilder.Build([original, copy], [], 2);

        Assert.Single(report.Matches);
        Assert.Single(report.Skipped);
        Assert.Equal(1, report.RecordedRewards);
    }

    [Fact]
    public void SerializedScanReportsRetainRewardsAndRecipientRoles()
    {
        var report = new ChestScanReport([Match([Reward(100, "other", "DelilahPrize:dpid_item17")])], [], 1);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var copy = JsonSerializer.Deserialize<ChestScanReport>(JsonSerializer.Serialize(report, options), options)!;

        var reward = Assert.Single(Assert.Single(copy.Matches).Rewards);
        Assert.Equal("Other", reward.PlayerName);
        Assert.False(reward.IsTeammate);
        Assert.Equal("Loading screen", reward.Category);
        Assert.Equal(12.5f, reward.ReplayTimeSeconds);
        Assert.Equal(1, copy.RecordedRewards);
    }

    [Theory]
    [InlineData("DelilahPrize:dpid_item2", "Pickaxe", "Major")]
    [InlineData("DELILAHPRIZE:DPID_ITEM20", "Back bling", "Major")]
    [InlineData("DelilahPrize:dpid_item31", "Jam track", "Major")]
    [InlineData("DelilahPrize:dpid_item32", "Loading screen", "Major")]
    [InlineData("DelilahPrize:dpid_item33", "Gold car decal", "Major")]
    [InlineData("DelilahPrize:dpid_item29", "Gold outfit", "Supreme")]
    [InlineData("9f73f94d-2e61-4523-97d7-d61235d3365e", "Pirate Shop Access", "Supreme")]
    [InlineData("DelilahPrize:dpid_item10", "Gizmo", "Standard")]
    public void ExplicitPrizeIdsMapToCategoriesWithoutExposingSpecificGizmos(string id, string category, string tier)
    {
        Assert.Equal((category, tier), ChestRewardCatalog.Get(id));
    }

    [Fact]
    public void CsvSeparatesRewardsFromChestCountsAndEscapesRecipientNames()
    {
        var match = Match([]) with { Rewards = [new("  =HYPERLINK(\"example\")", "other", false, false,
            "Loading screen", "Major", "DelilahPrize:dpid_item17", 986.29297f, 100, 0)] };
        var path = Path.Combine(Path.GetTempPath(), $"reward-report-{Guid.NewGuid():N}.csv");
        try
        {
            ChestReportExporter.WriteCsv(new([match], [], 1), path);
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            Assert.Equal(4, lines.Length);
            Assert.EndsWith("Row type,Reward category,Reward tier,Reward replay time,Prize template ID,Reward evidence", lines[0]);
            Assert.Contains("\"'  =HYPERLINK(\"\"example\"\")\"", lines[3]);
            Assert.Contains("\"Other player\",\"\",\"\",\"\",\"\",", lines[3]);
            Assert.EndsWith("\"Reward\",\"Loading screen\",\"Major\",\"16:26\",\"DelilahPrize:dpid_item17\",\"Successful prize result\"", lines[3]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ActualTreasureReceiptsAreUnknownTypeWithRecipientRolesAndIndependentSpecIdentity()
    {
        var first = Grant(50, "owner");
        var match = Match([], grants: [first, first, Grant(51, "friend"), Grant(52, "other"),
            Grant(53, "bot"), Grant(50, "owner", sequence: 1)]);
        Assert.Equal(4, match.Rewards.Count);
        Assert.Equal(2, match.Rewards.Count(reward => reward.IsOwner));
        Assert.Single(match.Rewards, reward => reward.IsTeammate);
        Assert.All(match.Rewards, reward =>
        {
            Assert.Equal("Treasure (unknown type)", reward.Category);
            Assert.Equal("Unknown", reward.Tier);
            Assert.Equal("", reward.PrizeTemplateId);
            Assert.Equal(ChestRewardEvidence.Receipt, reward.Evidence);
            Assert.NotNull(reward.AbilitySystemActorGuid);
        });
        Assert.Equal(2, match.SquadChests);
    }

    [Fact]
    public void UniqueReceiptNearSameRecipientsPrizeResultDoesNotDoubleCountTheReward()
    {
        var match = Match([Reward(100, "OWNER", "DelilahPrize:dpid_item15")],
            grants: [Grant(50, "owner", time: 10)]);
        var reward = Assert.Single(match.Rewards);
        Assert.Equal("Back bling", reward.Category);
        Assert.Equal(ChestRewardEvidence.PrizeResult, reward.Evidence);
    }

    [Fact]
    public void SharedTeammateReceiptIsNotJoinedToAnotherPlayersPrizeResult()
    {
        var match = Match([Reward(100, "friend", "DelilahPrize:dpid_item15")],
            grants: [Grant(50, "owner")]);
        Assert.Equal(2, match.Rewards.Count);
        Assert.Equal(ChestRewardEvidence.Receipt, Assert.Single(match.Rewards, reward => reward.IsOwner).Evidence);
    }

    [Fact]
    public void AmbiguousNearTimeReceiptsAndDistantOrUnknownRecipientReceiptsRemainSeparate()
    {
        var match = Match([Reward(100, "owner", "DelilahPrize:dpid_item15")],
            grants: [Grant(50, "owner"), Grant(51, "owner"), Grant(52, "owner", time: 30), Grant(53, null)]);
        Assert.Equal(5, match.Rewards.Count);
        Assert.Equal(4, match.Rewards.Count(reward => reward.Evidence == ChestRewardEvidence.Receipt));
        Assert.Equal("Unidentified player", Assert.Single(match.Rewards, reward => reward.PlayerId == null).PlayerName);
    }

    [Fact]
    public void ReceiptReadFailureDoesNotChangeChestCountsAndReceiptEvidenceSurvivesSerialization()
    {
        var match = Match([], grants: [Grant(50, "owner")], grantErrors: true);
        Assert.Equal(2, match.SquadChests);
        Assert.Contains("Some treasure receipts could not be read", match.RewardCoverage);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var copy = JsonSerializer.Deserialize<ChestMatch>(JsonSerializer.Serialize(match, options), options)!;
        Assert.Equal(ChestRewardEvidence.Receipt, Assert.Single(copy.Rewards).Evidence);
        Assert.Equal(50u, copy.Rewards[0].AbilitySystemActorGuid);
    }

    private static ChestMatch Match(ReplayTreasureReward[] rewards, bool rewardErrors = false,
        ReplayTreasureGrant[]? grants = null, bool grantErrors = false)
    {
        var builder = new FortniteReplayBuilder();
        FortPlayerState[] players = [
            new() { UniqueId = "owner", TeamIndex = 4 }, new() { UniqueId = "friend", TeamIndex = 4 },
            new() { UniqueId = "other", TeamIndex = 7 }, new() { bIsABot = true, BotUniqueId = "bot", TeamIndex = 4 }];
        for (uint channel = 0; channel < players.Length; channel++) builder.UpdatePlayerState(channel, players[channel]);
        var replay = builder.Build(new FortniteReplay { Info = new() { LengthInMs = 100_000 } });
        replay.GameData.CurrentPlaylist = "Playlist_DefaultSquad";
        foreach (var player in replay.PlayerData) player.PlayerName = player.PlayerId == "other" ? "Other" : player.PlayerId;
        var owner = replay.PlayerData.Single(player => player.PlayerId == "owner");
        owner.IsReplayOwner = true;
        ReplayChestOpening[] openings = [new(1, "Chest", null, null, "owner", 1), new(2, "Chest", null, null, "friend", 2)];
        var analysis = new ReplayChestAnalysis(replay, openings, [], 2, 4, false)
            { Rewards = rewards, HasRewardDecodeErrors = rewardErrors, Grants = grants ?? [], HasGrantDecodeErrors = grantErrors };
        return ChestMatchFactory.Build(analysis, owner, "UnsavedReplay-test.replay", "match-a", "Battle Royale · Build", new(2026, 11, 10));
    }

    private static ReplayTreasureReward Reward(uint actor, string? player, string template, int index = 0) =>
        new(actor, "RewardChest", 1, 2, 3, player, template, index, 12.5f);

    private static ReplayTreasureGrant Grant(uint component, string? player, float time = 12.5f,
        int spec = 128, int sequence = 0) => new(component, 3, player, spec, sequence,
            "GA_TreasureHandler_Generic_C", time);
}
