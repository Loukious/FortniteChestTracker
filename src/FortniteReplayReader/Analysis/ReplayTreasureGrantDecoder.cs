using Unreal.Core;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace FortniteReplayReader.Analysis;

internal sealed record ReplayAbilitySpecUpdate(int Id, uint? AbilityGuid, uint? SourceObjectGuid);
internal sealed record ReplayAbilityArrayUpdate(int ReplicationKey, int BaseReplicationKey,
    IReadOnlyList<int> DeletedIds, IReadOnlyList<ReplayAbilitySpecUpdate> ChangedSpecs);

internal static class ReplayTreasureGrantDecoder
{
    internal static bool IsAbilityGroup(NetFieldExportGroup group) => group.PathName is
        "/Script/FortniteGame.FortAbilitySystemComponentAthena" or
        "/Script/FortniteGame.FortAbilitySystemComponent" or
        "/Script/GameplayAbilities.AbilitySystemComponent";

    internal static bool TryGetHandlerClass(string path, out string handlerClass)
    {
        // Ability specs reference the class default object. Merely exporting a
        // treasure handler package or class is not a recorded receipt.
        const string prefix = "Default__GA_TreasureHandler_";
        var name = path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('.')) + 1)..];
        handlerClass = string.Empty;
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith("_C", StringComparison.Ordinal)) return false;
        var suffix = name.AsSpan(prefix.Length, name.Length - prefix.Length - 2);
        if (suffix.IsEmpty) return false;
        foreach (var character in suffix)
            if (!char.IsAsciiLetterOrDigit(character) && character != '_') return false;
        handlerClass = name["Default__".Length..];
        return true;
    }

    internal static bool TryReadArray(FBitArchive archive, NetFieldExportGroup group,
        out ReplayAbilityArrayUpdate update)
    {
        update = null!;
        if (archive.EngineNetworkVersion >= EngineNetworkVersionHistory.HISTORY_FAST_ARRAY_DELTA_STRUCT)
            archive.ReadBit();
        if (archive.GetBitsLeft() < 128 || archive.IsError) return false;
        var key = archive.ReadInt32();
        var baseKey = archive.ReadInt32();
        var deleteCount = archive.ReadInt32();
        var changeCount = archive.ReadInt32();
        if (archive.IsError || deleteCount < 0 || changeCount < 0 ||
            (long)deleteCount * 32 + (long)changeCount * 40 > archive.GetBitsLeft()) return false;
        if (changeCount > 0 && !group.NetFieldExports.Any(field => field?.Name == "ability")) return false;
        var deletes = new List<int>(deleteCount);
        var deletedIds = new HashSet<int>();
        for (var i = 0; i < deleteCount; i++)
        {
            var id = archive.ReadInt32();
            if (archive.IsError || id < 0 || !deletedIds.Add(id)) return false;
            deletes.Add(id);
        }
        var changes = new List<ReplayAbilitySpecUpdate>(changeCount);
        var changedIds = new HashSet<int>();
        for (var i = 0; i < changeCount; i++)
        {
            var id = archive.ReadInt32();
            if (archive.IsError || id < 0 || !changedIds.Add(id)) return false;
            uint? ability = null, source = null;
            var handles = new HashSet<uint>();
            while (true)
            {
                if (!TryReadPacked(archive, out var wireHandle)) return false;
                if (wireHandle == 0) break;
                var handle = wireHandle - 1;
                if (!group.IsValidIndex(handle) || handle >= group.NetFieldExports.Length || !handles.Add(handle) ||
                    !TryReadPacked(archive, out var count) || count > archive.GetBitsLeft() || count > int.MaxValue) return false;
                var name = group.NetFieldExports[handle]?.Name;
                if (name is not ("ability" or "SourceObject"))
                {
                    archive.SkipBits(count);
                    continue;
                }
                using var value = new NetBitReader(archive.ReadBits(count), (int)count);
                if (!TryReadPacked(value, out var guid) || value.IsError || !value.AtEnd()) return false;
                if (name == "ability") ability = guid;
                else source = guid;
            }
            changes.Add(new(id, ability, source));
        }
        if (archive.IsError || !archive.AtEnd()) return false;
        update = new(key, baseKey, deletes, changes);
        return true;
    }

    internal static bool TryReadOwner(FBitArchive archive, NetFieldExportGroup group, out uint? ownerActor)
    {
        ownerActor = null;
        archive.ReadBit(); // RepLayout property checksum presence.
        var handles = new HashSet<uint>();
        while (!archive.IsError)
        {
            if (!TryReadPacked(archive, out var wireHandle)) return false;
            if (wireHandle == 0) return true;
            var handle = wireHandle - 1;
            if (!group.IsValidIndex(handle) || handle >= group.NetFieldExports.Length || !handles.Add(handle) ||
                !TryReadPacked(archive, out var count) || count > archive.GetBitsLeft() || count > int.MaxValue) return false;
            if (group.NetFieldExports[handle]?.Name != "OwnerActor")
            {
                archive.SkipBits(count);
                continue;
            }
            using var value = new NetBitReader(archive.ReadBits(count), (int)count);
            if (!TryReadPacked(value, out var guid) || value.IsError || !value.AtEnd()) return false;
            ownerActor = guid;
        }
        return false;
    }

    private static bool TryReadPacked(FBitArchive archive, out uint value)
    {
        value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (archive.GetBitsLeft() < 8 || archive.IsError) return false;
            var data = archive.ReadByte();
            if (shift == 28 && (data & 0xe1) != 0) return false;
            value |= (uint)(data >> 1) << shift;
            if ((data & 1) == 0) return true;
        }
        return false;
    }
}
