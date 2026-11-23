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
            ? "Some replay data unreadable; counted recorded openings and attributed unopened chest destructions"
            : "Opened includes attributed unopened chest destructions; replay may miss distant interactions";
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
        var squadIds = players.Select(player => player.PlayerId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var disconnects = analysis.Disconnects.Where(d => d.PlayerId is not null && squadIds.Contains(d.PlayerId))
            .Select(d => new ChestPlayerDisconnect(d.PlayerId!, d.ReplayTimeSeconds is float time && float.IsFinite(time) && time >= 0 ? time : null))
            .OrderBy(d => d.ReplayTimeSeconds ?? float.MaxValue).ToArray();
        // This field describes play-again matchmaking. Preserve its reported broad
        // region; do not infer an exact server or choose among conflicting players.
        var regions = analysis.Replay.PlayerData.Where(player => !player.IsBot && !string.IsNullOrWhiteSpace(player.PlayerId))
            .SelectMany(player => player.ReportedMatchmakingRegions).Select(region => region.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] supportedRegions = ["EU", "NA", "NAE", "NAW", "NAC", "ASIA", "BR", "OCE", "ME"];
        var region = regions.Length == 1 && supportedRegions.Contains(regions[0]) ? regions[0] : null;
        var regionCoverage = region is not null ? "Reported by KeepPlayingTogetherMatchmakingRegion; broad play-again matchmaking region, exact server/subregion unconfirmed"
            : regions.Length > 1 ? "Conflicting reported matchmaking regions: " + string.Join(", ", regions)
            : regions.Length == 1 ? "Unrecognized reported matchmaking region" : "Reported matchmaking region unavailable";
        var timeline = available ? analysis.Openings.Where(opening => opening.PlayerId is not null &&
            squadIds.Contains(opening.PlayerId) && opening.ReplayTimeSeconds is float time &&
            float.IsFinite(time) && time >= 0)
            .Select(opening => new ChestOpeningTime(opening.PlayerId!, opening.ReplayTimeSeconds!.Value))
            .OrderBy(opening => opening.ReplayTimeSeconds).ToArray() : null;
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
                    reward.ReplayTimeSeconds, reward.RewardActorGuid, reward.PrizeIndex)
                    { SourceChestActorGuid = reward.SourceChestActorGuid };
            }).OfType<ChestReward>()
            .ToList();
        var grants = analysis.Grants.DistinctBy(grant => (grant.AbilitySystemActorGuid,
            grant.PlayerStateActorGuid, grant.AbilitySpecId, grant.GrantSequence)).ToArray();
        foreach (var grant in grants)
        {
            // The animation and handler can both describe the same treasure. Only
            // suppress a receipt with a unique same-recipient result and a unique
            // receipt in that result's short animation window. Never join teammates
            // or infer an opener from proximity. Ambiguous records remain visible.
            var nearbyResults = rewards.Where(reward => reward.Evidence == ChestRewardEvidence.PrizeResult &&
                SameRecipientNearTime(grant, reward)).ToArray();
            if (nearbyResults.Length == 1 && grants.Count(other =>
                SameRecipientNearTime(other, nearbyResults[0])) == 1) continue;
            var recipient = analysis.Replay.PlayerData.FirstOrDefault(player =>
                grant.PlayerId is not null && player.PlayerId is not null &&
                player.PlayerId.Equals(grant.PlayerId, StringComparison.OrdinalIgnoreCase));
            if (recipient?.IsBot == true) continue;
            var isOwner = grant.PlayerId is not null &&
                grant.PlayerId.Equals(owner.PlayerId, StringComparison.OrdinalIgnoreCase);
            var isTeammate = !isOwner && owner.TeamIndex is > 0 && recipient?.TeamIndex == owner.TeamIndex;
            rewards.Add(new ChestReward(
                recipient?.PlayerName ?? recipient?.PlayerNameCustomOverride ?? recipient?.StreamerModeName ?? "Unidentified player",
                grant.PlayerId, isOwner, isTeammate, "Treasure (unknown type)", "Unknown", "",
                grant.ReplayTimeSeconds, 0, grant.AbilitySpecId)
                { Evidence = ChestRewardEvidence.Receipt, AbilitySystemActorGuid = grant.AbilitySystemActorGuid,
                    AbilitySpecId = grant.AbilitySpecId, GrantSequence = grant.GrantSequence });
        }
        var orderedRewards = rewards.OrderBy(reward => reward.ReplayTimeSeconds ?? float.MaxValue).ToArray();
        var rewardCoverage = ChestRewardCoverage.RecordedOnly;
        if (analysis.HasRewardDecodeErrors) rewardCoverage += " Some reward updates could not be read.";
        if (analysis.HasGrantDecodeErrors) rewardCoverage += " Some treasure receipts could not be read.";
        if (rewards.Any(reward => reward.PlayerName == "Unidentified player"))
            rewardCoverage += " Some reward recipients could not be identified.";
        return new ChestMatch(path, matchId, players.Single(player => player.IsOwner).Name,
            owner.PlayerId!, mode, analysis.Replay.GameData.CurrentPlaylist, matchDate, players, coverage)
            { RecordingLengthInMs = analysis.Replay.Info.LengthInMs, OpeningTimeline = timeline,
                MatchmakingRegion = region, MatchmakingRegionCoverage = regionCoverage,
                PlayerDisconnects = disconnects, DisconnectMonitoringAvailable = analysis.DisconnectMonitoringAvailable,
                Rewards = orderedRewards, RewardCoverage = rewardCoverage,
                IsStandardMode = ChestEligibility.GetMode(analysis.Replay.GameData.CurrentPlaylist,
                    analysis.Replay.GameData.IsTournamentRound, analysis.Replay.GameData.IsLargeTeamGame == true) is not null };
    }

    private static bool SameRecipientNearTime(ReplayTreasureGrant grant, ChestReward reward) =>
        !string.IsNullOrWhiteSpace(grant.PlayerId) && !string.IsNullOrWhiteSpace(reward.PlayerId) &&
        grant.PlayerId.Equals(reward.PlayerId, StringComparison.OrdinalIgnoreCase) &&
        grant.ReplayTimeSeconds is float receipt && float.IsFinite(receipt) && receipt >= 0 &&
        reward.ReplayTimeSeconds is float result && float.IsFinite(result) && result >= 0 &&
        MathF.Abs(receipt - result) <= 5;
}
