using FortniteReplayReader.Analysis;
using FortniteReplayReader.Models;

namespace FortniteChestTracker.Core;

internal static class ChestMatchFactory
{
    internal static ChestMatch Build(ReplayChestAnalysis analysis, PlayerData owner, string path,
        string matchId, string mode, DateTime matchDate)
    {
        // Hired NPCs can share TeamIndex; they are not squad teammates for this report.
        // Unknown identities stay separate and cannot be joined across recordings.
        var squad = analysis.Replay.PlayerData.Select((player, index) => (player, index))
            .Where(item => !item.player.IsBot &&
                (item.player == owner || owner.TeamIndex is > 0 && item.player.TeamIndex == owner.TeamIndex))
            .OrderByDescending(item => item.player == owner)
            .GroupBy(item => string.IsNullOrWhiteSpace(item.player.PlayerId)
                ? $"unknown:{matchId}:{owner.PlayerId}:{item.index}" : item.player.PlayerId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
        var available = analysis.SearchFieldsDecoded > 0;
        var coverage = !available ? ChestCoverage.Unavailable : analysis.HasDecodeErrors
            ? "Some replay data unreadable; counted recorded chest openings"
            : "Chests opened; replay may miss distant openings";
        var players = squad.Select(item => new ChestPlayer(
            item.player.PlayerName ?? item.player.PlayerNameCustomOverride ?? item.player.StreamerModeName ?? item.player.PlayerId ?? "Unnamed player",
            string.IsNullOrWhiteSpace(item.player.PlayerId) ? $"unknown:{matchId}:{owner.PlayerId}:{item.index}" : item.player.PlayerId,
            item.player == owner,
            available && !string.IsNullOrWhiteSpace(item.player.PlayerId)
                ? analysis.Openings.Count(opening => opening.PlayerId != null &&
                    opening.PlayerId.Equals(item.player.PlayerId, StringComparison.OrdinalIgnoreCase)) : null,
            string.IsNullOrWhiteSpace(item.player.PlayerId) ? "Player identity unavailable" : coverage)).ToArray();
        var unresolved = analysis.Openings.Count(opening => opening.PlayerId is null);
        if (unresolved > 0) coverage += $"; {unresolved} openings have no identified player";
        if (owner.TeamIndex is null or <= 0) coverage += "; team membership unavailable";
        var rewards = analysis.Rewards
            .DistinctBy(reward => (reward.RewardActorGuid, reward.PrizeIndex))
            .Select(reward =>
            {
                var recipient = analysis.Replay.PlayerData.FirstOrDefault(player =>
                    reward.PlayerId is not null && player.PlayerId is not null &&
                    player.PlayerId.Equals(reward.PlayerId, StringComparison.OrdinalIgnoreCase));
                if (recipient?.IsBot == true) return null;
                var isOwner = reward.PlayerId is not null &&
                    reward.PlayerId.Equals(owner.PlayerId, StringComparison.OrdinalIgnoreCase);
                var isTeammate = !isOwner && owner.TeamIndex is > 0 && recipient?.TeamIndex == owner.TeamIndex;
                var (category, tier) = ChestRewardCatalog.Get(reward.PrizeTemplateId);
                return new ChestReward(
                    recipient?.PlayerName ?? recipient?.PlayerNameCustomOverride ?? recipient?.StreamerModeName ?? "Unidentified player",
                    reward.PlayerId, isOwner, isTeammate, category, tier, reward.PrizeTemplateId,
                    reward.ReplayTimeSeconds, reward.RewardActorGuid, reward.PrizeIndex);
            }).OfType<ChestReward>()
            .OrderBy(reward => reward.ReplayTimeSeconds ?? float.MaxValue).ToArray();
        var rewardCoverage = ChestRewardCoverage.RecordedOnly;
        if (analysis.HasRewardDecodeErrors) rewardCoverage += " Some reward updates could not be read.";
        if (rewards.Any(reward => reward.PlayerName == "Unidentified player"))
            rewardCoverage += " Some reward recipients could not be identified.";
        return new ChestMatch(path, matchId, players.Single(player => player.IsOwner).Name,
            owner.PlayerId!, mode, analysis.Replay.GameData.CurrentPlaylist, matchDate, players, coverage)
            { RecordingLengthInMs = analysis.Replay.Info.LengthInMs, Rewards = rewards, RewardCoverage = rewardCoverage };
    }
}
