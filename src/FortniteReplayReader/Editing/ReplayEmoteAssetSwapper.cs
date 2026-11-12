using Unreal.Core;
using Unreal.Core.Models.Enums;

namespace FortniteReplayReader.Editing;

public sealed partial class ReplayCosmeticEditor
{
    /// <summary>Replaces every recorded use of selected emotes, including inherited animation references.</summary>
    public int ReplacePerformedEmotes(string playerSelector, IReadOnlyDictionary<string, ReplayEmoteSelection> replacements)
    {
        if (edited) throw new InvalidOperationException("Create a new cosmetic editor for each edit.");
        var player = EmotePlayer(playerSelector);
        var actor = layout.PlayerActors[player.PlayerId!];
        var byId = new Dictionary<string, ReplayEmoteSelection>(replacements, StringComparer.OrdinalIgnoreCase);
        foreach (var selection in byId.Values)
        {
            if (ReplayCosmeticGuidExports.SplitPath(selection.AssetPath).Object != selection.AssetId)
                throw new ArgumentException("The emote ID must match its resolved object path.");
            ReplayCosmeticGuidExports.SplitPath(selection.MontagePath);
        }
        var actions = GetPerformedEmotes(playerSelector);
        var selected = actions.Where(action => byId.ContainsKey(action.Emote.AssetId)).ToArray();
        if (selected.Length == 0) return 0;
        var montages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
        {
            var target = byId.TryGetValue(action.Emote.AssetId, out var replacement) ? replacement.MontagePath : action.Emote.MontagePath;
            if (montages.TryGetValue(action.Emote.MontagePath, out var previous) && previous != target)
                throw new NotSupportedException("These emotes share a recorded animation. Choose the same replacement for each shared emote.");
            montages[action.Emote.MontagePath] = target;
        }
        var scans = new List<ReplaySkinLayoutReader> { layout };
        foreach (var checkpoint in container.Chunks.Where(c => c.Type == ReplayChunkType.Checkpoint))
        {
            var scan = new ReplaySkinLayoutReader(container, checkpoint);
            scan.Scan();
            scans.Add(scan);
        }
        var imports = new ReplayCosmeticGuidExports(scans);
        var expected = new Dictionary<SkinProperty, ReplayBits>();
        var edits = new Dictionary<SkinBunch, List<ReplayBitEdit>>();
        var required = new Dictionary<SkinBunch, HashSet<uint>>();
        void Replace(SkinProperty property, ReplayBits value, uint guid = 0)
        {
            if (ReplaySkinSwapper.EqualBits(property.Value, value)) return;
            expected[property] = value;
            if (!edits.TryGetValue(property.Bunch, out var changes)) edits[property.Bunch] = changes = [];
            changes.Add(new(property.Start, property.Length, value));
            changes.Add(new(property.SizeStart, property.SizeLength, ReplayBits.Packed((uint)value.Length)));
            if (guid == 0) return;
            if (!required.TryGetValue(property.Bunch, out var guids)) required[property.Bunch] = guids = [];
            guids.Add(guid);
        }
        foreach (var scan in scans)
        {
            var downloadSlots = new Dictionary<uint, ReplayRepArray>();
            foreach (var property in scan.Properties.Where(p => BelongsTo(scan, p, actor)))
            {
                if (property.ObjectGuid is uint guid && TryObjectPath(scan, guid) is { } path)
                {
                    if (property.Name == "LastReplicatedEmoteExecuted" && byId.TryGetValue(ReplayCosmeticGuidExports.SplitPath(path).Object, out var selection))
                    {
                        var imported = imports.Import(selection.AssetPath);
                        Replace(property, ReplayBits.Packed(imported), imported);
                    }
                    else if (montages.TryGetValue(path, out var montage) && !path.Equals(montage, StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Name == "PawnMontage")
                        {
                            var imported = imports.Import(montage);
                            Replace(property, ReplayBits.Packed(imported), imported);
                        }
                        else if (SkinProperty.MontageNames.Contains(property.Name))
                        {
                            var imported = imports.Import(montage);
                            var reference = ReplayMontageReference.Read(property.Value);
                            Replace(property, property.Value.Patch([new(reference.Start, reference.Length, ReplayBits.Packed(imported))]), imported);
                        }
                    }
                }
                if (property.Name == "InGameEmoteIADSlots")
                {
                    if (!downloadSlots.TryGetValue(property.Actor, out var state)) downloadSlots[property.Actor] = state = new();
                    var update = ReplayRepArray.Read(property);
                    state.Merge(update);
                    foreach (var (index, fields) in update.Elements)
                    {
                        var oldId = state.Name(index, "AssetName");
                        if (oldId == null || !byId.TryGetValue(oldId, out var selection)) continue;
                        var (package, name) = ReplayCosmeticGuidExports.SplitPath(selection.AssetPath);
                        fields["PackageName"] = ReplaySkinSwapper.FName(package);
                        fields["AssetName"] = ReplaySkinSwapper.FName(name);
                    }
                    Replace(property, update.Write(property));
                }
                if (property.Name == "Montage")
                {
                    using var reader = new NetBitReader(property.Value.Bytes, property.Length);
                    var stringPath = reader.ReadBit() ? reader.ReadFString() : null;
                    if (!reader.IsError && reader.AtEnd() && stringPath != null && montages.TryGetValue(stringPath, out var replacement) && stringPath != replacement)
                        Replace(property, ReplayBits.Join([ReplayBits.UInt(1, 1), ReplaySkinSwapper.FString(replacement)]));
                }
            }
        }
        if (expected.Count == 0) return 0;
        Commit(scans, expected, edits, required, imports);
        edited = true;
        return selected.Count(action => action.Emote != byId[action.Emote.AssetId]);
    }
}
