using FortniteReplayReader.Models;
using Unreal.Core;
using Unreal.Core.Models.Enums;

namespace FortniteReplayReader.Editing;

public sealed record ReplayEmoteSelection(string AssetId, string AssetPath, string MontagePath);
public sealed record ReplayPerformedEmote(string PlayerId, string PlayerName, uint PawnActor, float StartSeconds,
    float EndSeconds, ReplayEmoteSelection Emote);
public sealed record ReplayPerformedEmoteEditResult(ReplayPerformedEmote Original, ReplayEmoteSelection Replacement,
    int ChangedProperties, int ChangedPackets, int ChangedCheckpoints);

public sealed partial class ReplayCosmeticEditor
{
    /// <summary>Lists recorded emote actions with a known animation and start/stop times.</summary>
    public IReadOnlyList<ReplayPerformedEmote> GetPerformedEmotes(string playerSelector)
    {
        var player = EmotePlayer(playerSelector);
        var actor = layout.PlayerActors[player.PlayerId!];
        var properties = layout.Properties.Where(p => BelongsTo(layout, p, actor)).ToArray();
        var events = new List<ReplayPerformedEmote>();
        for (var i = 0; i < properties.Length; i++)
        {
            var start = properties[i];
            if (start.Name != "LastReplicatedEmoteExecuted" || start.ObjectGuid is not > 0) continue;
            var later = properties.Skip(i + 1).Where(p => p.Actor == start.Actor).ToArray();
            var end = later.FirstOrDefault(p => p.Name == "LastReplicatedEmoteExecuted" ||
                (p.Name == "bIsPlayingEmote" && p.Length == 1 && (p.Value.Bytes[0] & 1) == 0));
            if (end == null) continue;
            // PawnMontage may precede the emote ID in the same frame, or remain
            // unchanged when the same dance is performed again. Never borrow the
            // next action's montage at the shared end/start timestamp.
            var montage = properties.FirstOrDefault(p => p.Actor == start.Actor && p.Name == "PawnMontage" &&
                p.ObjectGuid is > 0 && p.TimeSeconds >= start.TimeSeconds && p.TimeSeconds < end.TimeSeconds)
                ?? properties.LastOrDefault(p => p.Actor == start.Actor && p.Name == "PawnMontage" &&
                    p.ObjectGuid is > 0 && p.TimeSeconds <= start.TimeSeconds);
            if (montage?.ObjectGuid == null) continue;
            var assetPath = TryObjectPath(layout, start.ObjectGuid.Value);
            var montagePath = TryObjectPath(layout, montage.ObjectGuid.Value);
            if (assetPath == null || montagePath == null) continue;
            events.Add(new(player.PlayerId!, player.PlayerName ?? playerSelector, start.Actor, start.TimeSeconds, end.TimeSeconds,
                new(ReplayCosmeticGuidExports.SplitPath(assetPath).Object, assetPath, montagePath)));
        }
        return events;
    }

    /// <summary>Replaces one recorded action's assets while retaining its original timing and animation-state bits.</summary>
    public ReplayPerformedEmoteEditResult ReplacePerformedEmote(string playerSelector, float startSeconds, ReplayEmoteSelection selection)
    {
        if (edited) throw new InvalidOperationException("Create a new cosmetic editor for each edit.");
        ArgumentNullException.ThrowIfNull(selection);
        var (_, assetName) = ReplayCosmeticGuidExports.SplitPath(selection.AssetPath);
        ReplayCosmeticGuidExports.SplitPath(selection.MontagePath);
        if (selection.AssetId != assetName) throw new ArgumentException("The emote ID must match its resolved object path.");
        var matches = GetPerformedEmotes(playerSelector).Where(e => Math.Abs(e.StartSeconds - startSeconds) < 0.001f).ToArray();
        if (matches.Length != 1) throw new ArgumentException("Select one recorded emote using its exact start time from GetPerformedEmotes.");
        var action = matches[0];
        var actor = layout.PlayerActors[action.PlayerId];
        var scans = new List<ReplaySkinLayoutReader> { layout };
        foreach (var checkpoint in container.Chunks.Where(c => c.Type == ReplayChunkType.Checkpoint))
        {
            var reader = new ReplaySkinLayoutReader(container, checkpoint);
            reader.Scan();
            scans.Add(reader);
        }
        var imports = new ReplayCosmeticGuidExports(scans);
        var emoteGuid = imports.Import(selection.AssetPath);
        var montageGuid = imports.Import(selection.MontagePath);
        var expected = new Dictionary<SkinProperty, ReplayBits>();
        var edits = new Dictionary<SkinBunch, List<ReplayBitEdit>>();
        var required = new Dictionary<SkinBunch, HashSet<uint>>();
        var changedNames = new HashSet<string>();
        void Replace(SkinProperty property, ReplayBits value, uint guid = 0)
        {
            if (ReplaySkinSwapper.EqualBits(property.Value, value)) return;
            expected[property] = value;
            if (!edits.TryGetValue(property.Bunch, out var changes)) edits[property.Bunch] = changes = [];
            changes.Add(new(property.Start, property.Length, value));
            changes.Add(new(property.SizeStart, property.SizeLength, ReplayBits.Packed((uint)value.Length)));
            if (guid != 0)
            {
                if (!required.TryGetValue(property.Bunch, out var guids)) required[property.Bunch] = guids = [];
                guids.Add(guid);
            }
            changedNames.Add(property.Name);
        }
        foreach (var scan in scans)
        {
            var downloadSlots = new ReplayRepArray();
            foreach (var property in scan.Properties.Where(p => BelongsTo(scan, p, actor)))
            {
                if (property.Name == "InGameEmoteIADSlots") downloadSlots.Merge(ReplayRepArray.Read(property));
                if (property.TimeSeconds < action.StartSeconds || property.TimeSeconds > action.EndSeconds) continue;
                if (property.Actor == action.PawnActor && property.ObjectGuid is uint guid)
                {
                    if (property.Name == "LastReplicatedEmoteExecuted" && SameAsset(scan, guid, action.Emote.AssetPath))
                        Replace(property, ReplayBits.Packed(emoteGuid), emoteGuid);
                    else if (property.Name == "PawnMontage" && SameAsset(scan, guid, action.Emote.MontagePath))
                        Replace(property, ReplayBits.Packed(montageGuid), montageGuid);
                    else if (SkinProperty.MontageNames.Contains(property.Name) && SameAsset(scan, guid, action.Emote.MontagePath))
                    {
                        var reference = ReplayMontageReference.Read(property.Value);
                        Replace(property, property.Value.Patch([new(reference.Start, reference.Length, ReplayBits.Packed(montageGuid))]), montageGuid);
                    }
                }
                if (property.Name == "InGameEmoteIADSlots")
                {
                    var (oldPackage, oldName) = ReplayCosmeticGuidExports.SplitPath(action.Emote.AssetPath);
                    var (newPackage, newName) = ReplayCosmeticGuidExports.SplitPath(selection.AssetPath);
                    var update = ReplayRepArray.Read(property);
                    foreach (var (index, fields) in update.Elements)
                        if (downloadSlots.Name(index, "PackageName") == oldPackage && downloadSlots.Name(index, "AssetName") == oldName)
                        {
                            fields["PackageName"] = ReplaySkinSwapper.FName(newPackage);
                            fields["AssetName"] = ReplaySkinSwapper.FName(newName);
                        }
                    Replace(property, update.Write(property));
                }
                if (property.Actor == action.PawnActor && property.Name == "Montage")
                {
                    using var reader = new NetBitReader(property.Value.Bytes, property.Length);
                    var path = reader.ReadBit() ? reader.ReadFString() : null;
                    if (!reader.IsError && reader.AtEnd() && path == action.Emote.MontagePath)
                        Replace(property, ReplayBits.Join([ReplayBits.UInt(1, 1), ReplaySkinSwapper.FString(selection.MontagePath)]));
                }
            }
        }
        if (!changedNames.Contains("LastReplicatedEmoteExecuted") || !changedNames.Contains("PawnMontage") ||
            !changedNames.Contains("ReplayRepAnimMontageInfo"))
            throw new NotSupportedException("This recorded emote does not have a complete editable animation event.");
        var committed = Commit(scans, expected, edits, required, imports);
        edited = true;
        return new(action, selection, expected.Count, committed.ChangedPackets, committed.ChangedCheckpoints);
    }

    private PlayerData EmotePlayer(string selector)
    {
        var matches = Players.Where(p => string.Equals(p.PlayerId, selector, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.PlayerName, selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].PlayerId == null || !layout.PlayerActors.ContainsKey(matches[0].PlayerId!))
            throw new ArgumentException("Select one recorded player by name or player ID.");
        return matches[0];
    }

    private bool SameAsset(ReplaySkinLayoutReader scan, uint guid, string path) =>
        string.Equals(TryObjectPath(scan, guid), path, StringComparison.OrdinalIgnoreCase);

    private string? TryObjectPath(ReplaySkinLayoutReader scan, uint guid)
    {
        if (guid == 0) return null;
        var parts = new List<string>();
        var visited = new HashSet<uint>();
        while (guid != 0)
        {
            if (!visited.Add(guid)) return null;
            var definition = scan.GuidDefinitions.GetValueOrDefault(guid) ?? layout.GuidDefinitions.GetValueOrDefault(guid);
            if (definition == null) return null;
            parts.Add(definition.Path);
            guid = definition.Outer;
        }
        parts.Reverse();
        return string.Join('.', parts);
    }
}
