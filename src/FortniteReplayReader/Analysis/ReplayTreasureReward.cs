using System.Text;
using Unreal.Core;
using Unreal.Core.Models;

namespace FortniteReplayReader.Analysis;

/// <summary>A successful treasure reward explicitly recorded on a reward animation actor.</summary>
public sealed record ReplayTreasureReward(uint RewardActorGuid, string RewardClass, uint? SourceChestActorGuid,
    uint? OpeningPawnGuid, uint? PlayerStateActorGuid, string? PlayerId, string PrizeTemplateId,
    int PrizeIndex, float? ReplayTimeSeconds);

internal sealed class ReplayTreasureRewardAccumulator
{
    private sealed class RewardState(string rewardClass)
    {
        public string Class { get; } = rewardClass;
        public bool? Success { get; set; }
        public uint? OpeningPawn { get; set; }
        public uint? SourceChest { get; set; }
        public float? SuccessTime { get; set; }
        public Dictionary<int, string> Prizes { get; } = [];
    }

    private readonly Dictionary<uint, RewardState> rewards = [];

    internal bool ReadUpdate(uint actor, NetFieldExportGroup group, FBitArchive archive, bool checksum, float? time)
    {
        // An incomplete property stream must not apply a partial success or array update.
        if (!ReplayTreasureRewardDecoder.TryReadUpdate(archive, group, checksum, out var update)) return false;
        if (!rewards.TryGetValue(actor, out var state)) rewards[actor] = state = new(group.PathName);
        if (update.Success is bool success)
        {
            state.Success = success;
            if (success) state.SuccessTime ??= time;
        }
        // Each animation actor represents one roll. Keep its explicit opener/source
        // when later cleanup updates clear those references.
        if (update.OpeningPawn is > 0) state.OpeningPawn ??= update.OpeningPawn;
        if (update.SourceChest is > 0) state.SourceChest ??= update.SourceChest;
        foreach (var array in update.PrizeArrays)
        {
            foreach (var removed in state.Prizes.Keys.Where(index => index >= array.Length).ToArray())
                state.Prizes.Remove(removed);
            foreach (var pair in array.Values) state.Prizes[pair.Key] = pair.Value;
        }
        return true;
    }

    internal IReadOnlyList<ReplayTreasureReward> Finish(IReadOnlyDictionary<uint, uint> pawnStates,
        IReadOnlyDictionary<uint, string> playerStates)
    {
        var result = new List<ReplayTreasureReward>();
        foreach (var pair in rewards)
        {
            var state = pair.Value;
            if (state.Success != true) continue;
            uint? playerState = state.OpeningPawn is uint pawn && pawnStates.TryGetValue(pawn, out var guid) ? guid : null;
            var playerId = playerState is uint player && playerStates.TryGetValue(player, out var id) ? id : null;
            foreach (var prize in state.Prizes.OrderBy(prize => prize.Key))
                if (!string.IsNullOrWhiteSpace(prize.Value))
                    result.Add(new(pair.Key, state.Class, state.SourceChest, state.OpeningPawn,
                        playerState, playerId, prize.Value, prize.Key, state.SuccessTime));
        }
        return result;
    }
}

internal sealed class ReplayTreasureRewardUpdate
{
    public bool? Success { get; set; }
    public uint? OpeningPawn { get; set; }
    public uint? SourceChest { get; set; }
    public List<ReplayTreasurePrizeArrayUpdate> PrizeArrays { get; } = [];
}

internal sealed record ReplayTreasurePrizeArrayUpdate(int Length, IReadOnlyDictionary<int, string> Values);

internal static class ReplayTreasureRewardDecoder
{
    private const string ActorDirectory = "/DelilahTreasureHuntGameplay/Gameplay/Actors/";
    private static readonly string[] RewardClassNames = ["BGA_DelilahTreasureHuntAnimatedChest",
        "BGA_DelilahTreasureHuntAnimatedChest_Rare", "BGA_DelilahTreasureHuntAnimatedChest_Sprite"];

    internal static bool IsRewardGroup(NetFieldExportGroup group)
    {
        if (!group.PathName.StartsWith(ActorDirectory, StringComparison.Ordinal)) return false;
        var objectName = group.PathName[ActorDirectory.Length..];
        foreach (var name in RewardClassNames)
            if (objectName.Equals(name + "." + name + "_C", StringComparison.Ordinal)) return true;
        return false;
    }

    internal static bool TryReadUpdate(FBitArchive archive, NetFieldExportGroup group, bool checksum,
        out ReplayTreasureRewardUpdate update)
    {
        update = new();
        if (checksum) archive.ReadBit();
        while (!archive.AtEnd() && !archive.IsError)
        {
            var handle = archive.ReadIntPacked();
            if (archive.IsError) return false;
            if (handle == 0) return true;
            handle--;
            if (!group.IsValidIndex(handle)) return false;
            var count = archive.ReadIntPacked();
            if (archive.IsError || count > archive.GetBitsLeft() || count > int.MaxValue) return false;
            var name = group.NetFieldExports[handle]?.Name;
            if (count == 0) continue;
            if (name is not ("bSuccess" or "OpeningPawn" or "SourceContainer" or "PrizeTemplateId"))
            {
                archive.SkipBits(count);
                continue;
            }
            using var value = new NetBitReader(archive.ReadBits(count), (int)count);
            switch (name)
            {
                case "bSuccess": update.Success = value.ReadBit(); break;
                case "OpeningPawn": update.OpeningPawn = value.ReadIntPacked(); break;
                case "SourceContainer": update.SourceChest = value.ReadIntPacked(); break;
                case "PrizeTemplateId":
                    if (!TryReadPrizeArray(value, group, out var array)) return false;
                    update.PrizeArrays.Add(array);
                    break;
            }
            if (value.IsError || !value.AtEnd()) return false;
        }
        return false;
    }

    private static bool TryReadPrizeArray(FBitArchive archive, NetFieldExportGroup group,
        out ReplayTreasurePrizeArrayUpdate array)
    {
        array = null!;
        var count = archive.ReadIntPacked();
        if (archive.IsError || count > int.MaxValue) return false;
        // Keep only exported elements. The declared array length can grow or shrink
        // without allocating an array or losing unchanged sparse elements.
        var values = new Dictionary<int, string>();
        while (!archive.AtEnd() && !archive.IsError)
        {
            var wireIndex = archive.ReadIntPacked();
            if (archive.IsError) return false;
            if (wireIndex == 0)
            {
                array = new((int)count, values);
                return true;
            }
            if (wireIndex > count || values.ContainsKey((int)wireIndex - 1)) return false;
            string? element = null;
            while (!archive.AtEnd() && !archive.IsError)
            {
                var handle = archive.ReadIntPacked();
                if (archive.IsError) return false;
                if (handle == 0) break;
                handle--;
                if (!group.IsValidIndex(handle) || group.NetFieldExports[handle]?.Name != "PrizeTemplateId" || element != null)
                    return false;
                var bits = archive.ReadIntPacked();
                if (archive.IsError || bits > archive.GetBitsLeft() || bits > int.MaxValue) return false;
                using var value = new NetBitReader(archive.ReadBits(bits), (int)bits);
                if (!TryReadString(value, out element) || value.IsError || !value.AtEnd()) return false;
            }
            if (archive.IsError || element == null) return false;
            values[(int)wireIndex - 1] = element;
        }
        return false;
    }

    private static bool TryReadString(FBitArchive archive, out string value)
    {
        value = string.Empty;
        if (archive.GetBitsLeft() < 32) return false;
        var length = archive.ReadInt32();
        if (length == 0) return true;
        if (length == int.MinValue) return false;
        var unicode = length < 0;
        var bytes = (long)Math.Abs(length) * (unicode ? 2 : 1);
        if (bytes * 8 > archive.GetBitsLeft() || bytes > int.MaxValue) return false;
        var data = archive.ReadBits((int)bytes * 8);
        var terminatorBytes = unicode ? 2 : 1;
        if (data[^1] != 0 || unicode && data[^2] != 0) return false;
        value = (unicode ? Encoding.Unicode : Encoding.UTF8).GetString(data[..^terminatorBytes]);
        return !value.Contains('\0');
    }
}
