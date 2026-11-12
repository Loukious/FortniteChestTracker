using FortniteReplayReader.Models;
using FortniteReplayReader.Models.NetFieldExports;
using Unreal.Core;
using Unreal.Core.Models.Enums;

namespace FortniteReplayReader.Editing;

/// <summary>Applies resolved cosmetics to one player, preserving the original replay's packet sequence.</summary>
public sealed partial class ReplayCosmeticEditor
{
    private readonly ReplayContainer container;
    private readonly ReplaySkinLayoutReader layout;
    private bool edited;
    public IReadOnlyList<PlayerData> Players { get; }

    public ReplayCosmeticEditor(ReplayContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (!ReplaySkinSwapper.IsSupported(container))
            throw new NotSupportedException("Cosmetic editing supports Fortnite 43.00 (network version 45) full-checkpoint replays.");
        this.container = container;
        layout = new(container);
        Players = layout.Scan().PlayerData.ToArray();
    }

    /// <summary>Updates decoded chunks atomically. Save the container to a new replay file afterwards.</summary>
    public ReplayCosmeticEditResult ApplyCosmetics(string playerSelector, IReadOnlyList<ReplayCosmeticSelection> selections) =>
        ApplyCosmetics(playerSelector, selections, []);

    internal ReplayCosmeticEditResult ApplyCosmetics(string playerSelector, IReadOnlyList<ReplayCosmeticSelection> selections,
        IReadOnlyList<Dictionary<string, ReplayBits>> recordedOutfitVariants)
    {
        if (edited) throw new InvalidOperationException("Create a new cosmetic editor for each player's edits.");
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Count == 0) throw new ArgumentException("Choose at least one cosmetic.");
        ValidateSelections(selections);
        var matches = Players.Where(p => string.Equals(p.PlayerName, playerSelector, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.PlayerId, playerSelector, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new ArgumentException(matches.Length == 0 ? $"Player '{playerSelector}' was not found." : "That player name is ambiguous; select the player by ID.");
        var player = matches[0];
        if (player.PlayerId == null || !layout.PlayerActors.TryGetValue(player.PlayerId, out var actor))
            throw new InvalidOperationException("The selected player has no recorded actor.");
        var scans = new List<ReplaySkinLayoutReader> { layout };
        foreach (var checkpoint in container.Chunks.Where(c => c.Type == ReplayChunkType.Checkpoint))
        {
            var scan = new ReplaySkinLayoutReader(container, checkpoint);
            scan.Scan();
            scans.Add(scan);
        }
        var outfit = selections.SingleOrDefault(s => s.Slot == ReplayCosmeticSlot.Character);
        var backpack = selections.SingleOrDefault(s => s.Slot == ReplayCosmeticSlot.Backpack);
        var pickaxe = selections.SingleOrDefault(s => s.Slot == ReplayCosmeticSlot.Pickaxe);
        var imports = new ReplayCosmeticGuidExports(scans);
        var characterGuid = outfit == null ? 0 : imports.Import(outfit.AssetPath);
        var heroGuid = outfit?.HeroTypePath == null ? 0 : imports.Import(outfit.HeroTypePath);
        var pickaxeWeaponGuid = pickaxe == null ? 0 : imports.Import(pickaxe.PickaxeWeaponDefinitionPath!);
        var maximumAssets = scans.SelectMany(s => s.Properties.Where(p => BelongsTo(s, p, actor) && p.Name == "PrimaryAssets"))
            .GroupBy(p => p.Actor).ToDictionary(g => g.Key, g => g.Max(p => ReplayRepArray.Read(p).Count));
        var expected = new Dictionary<SkinProperty, ReplayBits>();
        var edits = new Dictionary<SkinBunch, List<ReplayBitEdit>>();
        var required = new Dictionary<SkinBunch, HashSet<uint>>();
        var covered = new HashSet<(ReplayCosmeticSlot, int)>();
        var sawParts = false;
        var sawCharacter = false;

        void Replace(SkinProperty property, ReplayBits value, uint guid = 0)
        {
            if (ReplaySkinSwapper.EqualBits(property.Value, value)) return;
            if (!edits.TryGetValue(property.Bunch, out var changes)) edits[property.Bunch] = changes = [];
            changes.Add(new(property.Start, property.Length, value));
            changes.Add(new(property.SizeStart, property.SizeLength, ReplayBits.Packed((uint)value.Length)));
            expected[property] = value;
            if (guid != 0)
            {
                if (!required.TryGetValue(property.Bunch, out var guids)) required[property.Bunch] = guids = [];
                guids.Add(guid);
            }
        }

        foreach (var scan in scans)
        {
            var states = new Dictionary<uint, LoadoutState>();
            foreach (var bunch in scan.Bunches)
            {
                var properties = bunch.Properties.Where(p => BelongsTo(scan, p, actor)).ToArray();
                foreach (var group in properties.GroupBy(p => p.Actor))
                {
                    if (!states.TryGetValue(group.Key, out var state)) states[group.Key] = state = new();
                    var values = group.ToArray();
                    foreach (var property in values.Where(p => ArrayNames.Contains(p.Name)))
                    {
                        if (!state.Arrays.TryGetValue(property.Name, out var array)) state.Arrays[property.Name] = array = new();
                        array.Merge(ReplayRepArray.Read(property));
                    }
                    var assetProperty = values.SingleOrDefault(p => p.Name == "PrimaryAssets");
                    var indexProperty = values.SingleOrDefault(p => p.Name == "AssetIndices");
                    var variantProperty = values.SingleOrDefault(p => p.Name == "CharacterVariantChannels");
                    if (assetProperty != null || indexProperty != null || variantProperty != null)
                    {
                        if (state.Arrays.TryGetValue("AssetIndices", out var indices) && state.Arrays.TryGetValue("PrimaryAssets", out var assets))
                        {
                            var variants = state.Array("CharacterVariantChannels");
                            var dances = state.Array("Dances").Count;
                            var wraps = state.Array("ItemWraps").Count;
                            var routing = RouteSlots(indices, assets, variants.Count, dances, wraps);
                            var deletedVariants = Enumerable.Range(0, variants.Count).Where(i =>
                            {
                                var id = indices.Int(4 + i, "AssetIndices");
                                var type = id < 0 ? null : assets.Name(id, "Name");
                                return selections.Any(s => s.Slot is not (ReplayCosmeticSlot.Emote or ReplayCosmeticSlot.Wrap) && AssetType(s.Slot) == type);
                            }).ToHashSet();
                            var deletedIndices = deletedVariants.Select(i => i + 4).ToHashSet();
                            var mapped = new Dictionary<int, int>();
                            for (var i = 0; i < selections.Count; i++)
                            {
                                var selection = selections[i];
                                if (!routing.TryGetValue((selection.Slot, selection.Index), out var position))
                                    throw new NotSupportedException($"{player.PlayerName} has no recorded {selection.Slot} slot {selection.Index + 1}. Choose an existing slot.");
                                mapped[position] = maximumAssets[group.Key] + i;
                                covered.Add((selection.Slot, selection.Index));
                            }
                            if (assetProperty != null)
                            {
                                var update = ReplayRepArray.Read(assetProperty);
                                var appended = selections.Select((selection, index) => KeyValuePair.Create(maximumAssets[group.Key] + index,
                                    new Dictionary<string, ReplayBits>
                                    {
                                        ["Name"] = ReplaySkinSwapper.FName(AssetType(selection.Slot)),
                                        ["PrimaryAssetName"] = ReplaySkinSwapper.FName(selection.AssetId)
                                    }));
                                Replace(assetProperty, update.Write(assetProperty, maximumAssets[group.Key] + selections.Count, update.Elements.Concat(appended)));
                                state.AssetsWritten = true;
                            }
                            if (indexProperty != null)
                            {
                                if (!state.AssetsWritten) throw new NotSupportedException("The recording references a loadout before exporting its primary assets.");
                                int Mapped(int i) => mapped.TryGetValue(i, out var id) ? id : indices.Int(i, "AssetIndices");
                                var variantEnd = 4 + variants.Count;
                                var outputIndices = Enumerable.Range(0, variantEnd).Where(i => !deletedIndices.Contains(i)).Select(Mapped)
                                    .Concat(Enumerable.Repeat(mapped.GetValueOrDefault(3), recordedOutfitVariants.Count))
                                    .Concat(Enumerable.Range(variantEnd, indices.Count - variantEnd).Select(Mapped))
                                    .Select(id => new Dictionary<string, ReplayBits> { ["AssetIndices"] = ReplayBits.UInt(unchecked((uint)id), 32) });
                                Replace(indexProperty, ReplaySkinSwapper.WriteArray(indexProperty, outputIndices));
                            }
                            else if (!deletedVariants.SetEquals(state.DeletedVariants) || (recordedOutfitVariants.Count > 0 && !state.DonorVariantsWritten))
                                throw new NotSupportedException("This recording updates cosmetic variants without their asset indices.");
                            if (variantProperty != null && (deletedVariants.Count != 0 || recordedOutfitVariants.Count != 0))
                            {
                                var kept = Enumerable.Range(0, variants.Count).Where(i => !deletedVariants.Contains(i))
                                    .Select(i => variants.Elements.GetValueOrDefault(i) ?? throw new InvalidDataException("Incomplete cosmetic variant array."));
                                Replace(variantProperty, ReplaySkinSwapper.WriteArray(variantProperty, kept.Concat(recordedOutfitVariants)));
                                state.DonorVariantsWritten = true;
                            }
                            else if (variantProperty == null && (!deletedVariants.SetEquals(state.DeletedVariants) || (recordedOutfitVariants.Count > 0 && !state.DonorVariantsWritten)))
                                throw new NotSupportedException("This recording changes variant routing without the variant array.");
                            state.DeletedVariants = deletedVariants;
                        }
                        else if (assetProperty != null || indexProperty != null)
                            throw new NotSupportedException("The recorded cosmetic loadout is incomplete.");
                    }
                    foreach (var property in values)
                    {
                        if (property.Name is "SoftParts" or "RequestedSoftCharacterParts" && (outfit != null || backpack != null))
                        {
                            var original = ReadParts(state.Array(property.Name));
                            var replacement = (outfit == null ? original.Where(p => p.Type != 3) : outfit.CharacterParts!.Where(p => p.Type != 3))
                                .Concat(backpack == null ? original.Where(p => p.Type == 3) : backpack.CharacterParts!.Where(p => p.Type == 3)).ToArray();
                            Replace(property, ReplaySkinSwapper.WriteParts(property, replacement.Select(p => new SoftCharacterPart
                                { Type = p.Type, Part = new() { Path = p.Path.Length == 0 ? null : p.Path } }).ToArray()));
                            if (property.Name == "SoftParts") sawParts = true;
                        }
                        else if (outfit != null)
                        {
                            switch (property.Name)
                            {
                                case "CachedLoadoutCharacter":
                                case "CachedLoadoutCharacterUngated":
                                case "Character":
                                    Replace(property, ReplayBits.Packed(characterGuid), characterGuid);
                                    sawCharacter = true;
                                    break;
                                case "HeroType" when heroGuid != 0:
                                    Replace(property, ReplayBits.Packed(heroGuid), heroGuid);
                                    break;
                                case "VariantRequiredCharacterParts":
                                    Replace(property, ReplayBits.Join([ReplayBits.Packed(0), ReplayBits.Packed(0)]));
                                    break;
                                case "bIsDefaultCharacter":
                                    Replace(property, ReplayBits.UInt(0, 1));
                                    break;
                                case "CharacterGender" when outfit.CharacterGender.HasValue:
                                    Replace(property, WriteEnum(outfit.CharacterGender.Value, property.Length));
                                    break;
                                case "CharacterBodyType" when outfit.CharacterBodyType.HasValue:
                                    Replace(property, WriteEnum(outfit.CharacterBodyType.Value, property.Length));
                                    break;
                            }
                        }
                    }
                }
            }
            if (pickaxe != null)
            {
                foreach (var property in scan.Properties.Where(p => p.Name == "WeaponData" && IsPlayerPickaxe(scan, p, actor)))
                    Replace(property, ReplayBits.Packed(pickaxeWeaponGuid), pickaxeWeaponGuid);
            }
        }
        if (covered.Count != selections.Count || ((outfit != null || backpack != null) && !sawParts) || (outfit != null && !sawCharacter))
            throw new InvalidOperationException("The selected player has no complete editable cosmetic loadout.");

        var committed = Commit(scans, expected, edits, required, imports);
        edited = true;
        return new(player.PlayerName ?? playerSelector, selections.ToArray(), expected.Count, committed.ChangedPackets,
            committed.ChangedCheckpoints, imports.Definitions.Count);
    }

    private (int ChangedPackets, int ChangedCheckpoints) Commit(IReadOnlyList<ReplaySkinLayoutReader> scans,
        Dictionary<SkinProperty, ReplayBits> expected, Dictionary<SkinBunch, List<ReplayBitEdit>> edits,
        Dictionary<SkinBunch, HashSet<uint>> required, ReplayCosmeticGuidExports imports)
    {
        var fragments = new Dictionary<SkinFragment, List<SkinFragment>>();
        foreach (var (bunch, changes) in edits)
        {
            foreach (var block in bunch.Blocks)
            {
                var delta = changes.Where(e => e.Start >= block.Start && e.Start < block.Start + block.Length).Sum(e => e.Delta);
                if (delta != 0) changes.Add(new(block.SizeStart, block.SizeLength, ReplayBits.Packed(checked((uint)(block.Length + delta)))));
            }
            if (required.TryGetValue(bunch, out var newGuids))
            {
                var guids = bunch.RequiredGuids.Concat(newGuids.Where(g => !bunch.RequiredGuids.Contains(g))).ToArray();
                if (guids.Length > ushort.MaxValue) throw new InvalidDataException("Too many required cosmetic assets in a bunch.");
                var prefix = ReplayBits.Join(new[] { ReplayBits.UInt((uint)guids.Length, 16) }.Concat(guids.Select(ReplayBits.Packed)));
                changes.Add(new(Math.Max(0, bunch.RequiredGuidPrefixStart), bunch.RequiredGuidPrefixLength, prefix));
            }
            ReplaySkinSwapper.SplitBunch(bunch, bunch.Bits.Patch(changes), fragments, required.ContainsKey(bunch));
        }
        var chunkEdits = new Dictionary<ReplayContainerChunk, List<(int Start, int Length, byte[] Bytes)>>();
        void Add(ReplayContainerChunk chunk, int start, int length, byte[] bytes)
        {
            if (!chunkEdits.TryGetValue(chunk, out var list)) chunkEdits[chunk] = list = [];
            list.Add((start, length, bytes));
        }
        var changedPackets = 0;
        foreach (var packet in scans.SelectMany(s => s.Packets).Where(p => p.Fragments.Any(fragments.ContainsKey)))
        {
            Add(packet.Chunk, packet.Start, packet.Length, ReplaySkinSwapper.EncodePackets(packet, fragments));
            changedPackets++;
        }
        if (imports.Definitions.Count != 0)
        {
            foreach (var scan in scans)
            {
                var section = scan.GuidExportSections.FirstOrDefault() ?? throw new NotSupportedException("A replay snapshot has no GUID export frame.");
                Add(section.Chunk, section.Start, section.Length, imports.Prepend(section));
            }
        }
        var updated = chunkEdits.ToDictionary(kv => kv.Key, kv => PatchBytes(kv.Key.Data, kv.Value));
        var originals = updated.Keys.ToDictionary(c => c, c => c.Data);
        try
        {
            foreach (var (chunk, bytes) in updated) chunk.Data = bytes;
            for (var i = 0; i < scans.Count; i++)
            {
                var checkpoint = i == 0 ? null : container.Chunks.Where(c => c.Type == ReplayChunkType.Checkpoint).ElementAt(i - 1);
                var after = new ReplaySkinLayoutReader(container, checkpoint);
                var replay = after.Scan();
                ReplaySkinSwapper.ValidateProperties(scans[i], after, expected);
                if (scans[i].Packets.Count != after.Packets.Count || scans[i].Bunches.Count != after.Bunches.Count)
                    throw new InvalidDataException("Cosmetic validation failed: network packet or bunch count changed.");
                for (var b = 0; b < after.Bunches.Count; b++)
                {
                    var original = scans[i].Bunches[b];
                    var final = after.Bunches[b];
                    var expectedGuids = original.RequiredGuids.Concat(required.TryGetValue(original, out var added)
                        ? added.Where(g => !original.RequiredGuids.Contains(g)) : []);
                    if (!expectedGuids.SequenceEqual(final.RequiredGuids) || !ReplaySkinSwapper.EqualBits(OpaqueBits(original), OpaqueBits(final)))
                        throw new InvalidDataException("Cosmetic validation failed: unrelated actor data or asset requirements changed.");
                }
                foreach (var definition in scans[i].GuidDefinitions.Values)
                    if (!after.GuidDefinitions.TryGetValue(definition.Guid, out var actual) || actual != definition)
                        throw new InvalidDataException("Cosmetic validation failed: an original asset definition changed.");
                foreach (var definition in imports.Definitions)
                    if (!after.GuidDefinitions.TryGetValue(definition.Guid, out var actual) || actual != definition)
                        throw new InvalidDataException("Cosmetic validation failed: an imported asset path did not survive the rewrite.");
                if (i == 0 && !Players.Select(p => p.PlayerId).Order().SequenceEqual(replay.PlayerData.Select(p => p.PlayerId).Order()))
                    throw new InvalidDataException("Cosmetic validation failed: player identities changed.");
            }
        }
        catch
        {
            foreach (var (chunk, bytes) in originals) chunk.Data = bytes;
            throw;
        }
        return (changedPackets, updated.Keys.Count(c => c.Type == ReplayChunkType.Checkpoint));
    }

    private static readonly HashSet<string> ArrayNames = new(StringComparer.Ordinal)
        { "PrimaryAssets", "AssetIndices", "CharacterVariantChannels", "Dances", "ItemWraps", "SoftParts", "RequestedSoftCharacterParts" };

    private sealed class LoadoutState
    {
        public Dictionary<string, ReplayRepArray> Arrays { get; } = [];
        public bool AssetsWritten { get; set; }
        public bool DonorVariantsWritten { get; set; }
        public HashSet<int> DeletedVariants { get; set; } = [];
        public ReplayRepArray Array(string name) => Arrays.GetValueOrDefault(name) ?? new();
    }

    private static bool BelongsTo(ReplaySkinLayoutReader scan, SkinProperty property, uint actor) =>
        property.Actor == actor || scan.PawnStates.GetValueOrDefault(property.Actor) == actor;

    internal static bool IsPlayerPickaxe(ReplaySkinLayoutReader scan, SkinProperty property, uint playerActor)
    {
        if (!ReplaySkinLayoutReader.IsPickaxeGroup(property.Group)) return false;
        var owner = property.WeaponOwner is > 0 ? property.WeaponOwner.Value : scan.WeaponOwners.GetValueOrDefault(property.Actor);
        var instigator = property.WeaponInstigator is > 0 ? property.WeaponInstigator.Value : scan.WeaponInstigators.GetValueOrDefault(property.Actor);
        return scan.PawnStates.GetValueOrDefault(owner) == playerActor || scan.PawnStates.GetValueOrDefault(instigator) == playerActor;
    }

    internal static IReadOnlyList<Dictionary<string, ReplayBits>> RecordedOutfitVariants(ReplaySkinLayoutReader scan, uint actor)
    {
        var states = new Dictionary<uint, LoadoutState>();
        uint latestPawn = 0;
        foreach (var property in scan.Properties.Where(p => BelongsTo(scan, p, actor) &&
            p.Name is "CharacterVariantChannels" or "PrimaryAssets" or "AssetIndices"))
        {
            if (!states.TryGetValue(property.Actor, out var state)) states[property.Actor] = state = new();
            if (!state.Arrays.TryGetValue(property.Name, out var array)) state.Arrays[property.Name] = array = new();
            array.Merge(ReplayRepArray.Read(property));
            latestPawn = property.Actor;
        }
        if (!states.TryGetValue(latestPawn, out var final)) return [];
        var assets = final.Array("PrimaryAssets");
        var variants = final.Array("CharacterVariantChannels");
        var indices = final.Array("AssetIndices");
        return Enumerable.Range(0, variants.Count).Where(i =>
        {
            var index = indices.Int(4 + i, "AssetIndices");
            return index >= 0 && assets.Name(index, "Name") == "AthenaCharacter";
        }).Select(i => variants.Elements[i]).ToArray();
    }

    private static ReplayBits WriteEnum(int value, int length)
    {
        if (value < 0 || length is < 1 or > 32 || (length < 32 && (uint)value >= (1u << length)))
            throw new ArgumentException("The selected cosmetic has an invalid character gender or body type.");
        return ReplayBits.UInt((uint)value, length);
    }

    private static ReplayBits OpaqueBits(SkinBunch bunch)
    {
        var exclusions = bunch.Properties.Select(p => new ReplayBitEdit(p.SizeStart, p.SizeLength + p.Length, ReplayBits.Empty))
            .Concat(bunch.Blocks.Select(b => new ReplayBitEdit(b.SizeStart, b.SizeLength, ReplayBits.Empty))).ToList();
        if (bunch.RequiredGuidPrefixStart >= 0)
            exclusions.Add(new(bunch.RequiredGuidPrefixStart, bunch.RequiredGuidPrefixLength, ReplayBits.Empty));
        return bunch.Bits.Patch(exclusions);
    }

    internal static string AssetType(ReplayCosmeticSlot slot) => slot switch
    {
        ReplayCosmeticSlot.Character => "AthenaCharacter", ReplayCosmeticSlot.Backpack => "AthenaBackpack",
        ReplayCosmeticSlot.Pickaxe => "AthenaPickaxe", ReplayCosmeticSlot.Glider => "AthenaGlider",
        ReplayCosmeticSlot.Contrail => "AthenaSkyDiveContrail", ReplayCosmeticSlot.LoadingScreen => "AthenaLoadingScreen",
        ReplayCosmeticSlot.MusicPack => "AthenaMusicPack", ReplayCosmeticSlot.Emote => "AthenaDance", ReplayCosmeticSlot.Wrap => "AthenaItemWrap",
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };

    internal static Dictionary<(ReplayCosmeticSlot Slot, int Index), int> RouteSlots(ReplayRepArray indices, ReplayRepArray assets,
        int variants, int dances, int wraps)
    {
        // FortAthenaLoadout's reflected object order in 43.00. Each variant has
        // one ItemVariantIsUsedFor object. Array counts remain in the legacy
        // replicated fields; deduplicated PrimaryAssets do not describe slots.
        if (indices.Count != 15 + variants + dances + wraps)
            throw new NotSupportedException("This player uses an unrecognized cosmetic loadout layout; the replay was preserved.");
        var result = new Dictionary<(ReplayCosmeticSlot, int), int>
        {
            [(ReplayCosmeticSlot.Contrail, 0)] = 0, [(ReplayCosmeticSlot.Glider, 0)] = 1,
            [(ReplayCosmeticSlot.Pickaxe, 0)] = 2, [(ReplayCosmeticSlot.Character, 0)] = 3,
            [(ReplayCosmeticSlot.Backpack, 0)] = 4 + variants,
            [(ReplayCosmeticSlot.LoadingScreen, 0)] = 7 + variants,
            [(ReplayCosmeticSlot.MusicPack, 0)] = 8 + variants + dances
        };
        for (var i = 0; i < dances; i++) result[(ReplayCosmeticSlot.Emote, i)] = 8 + variants + i;
        for (var i = 0; i < wraps; i++) result[(ReplayCosmeticSlot.Wrap, i)] = 9 + variants + dances + i;
        foreach (var (slot, position) in result)
        {
            var id = indices.Int(position, "AssetIndices");
            if (id < -1 || id >= assets.Count || (id >= 0 && assets.Name(id, "Name") != AssetType(slot.Item1)))
                throw new NotSupportedException($"This player's {slot.Item1} reference does not match the supported loadout layout.");
        }
        return result;
    }

    private static ReplayCharacterPart[] ReadParts(ReplayRepArray array) => Enumerable.Range(0, array.Count).Select(index =>
    {
        var type = array.Field(index, "Type") ?? throw new InvalidDataException("Character part has no type.");
        var part = array.Field(index, "SoftPart") ?? throw new InvalidDataException("Character part has no path.");
        using var typeReader = new NetBitReader(type.Bytes, type.Length);
        using var pathReader = new NetBitReader(part.Bytes, part.Length);
        var kind = checked((int)typeReader.ReadBitsToInt(4));
        var path = pathReader.ReadBit() ? pathReader.ReadFString() : "";
        if (typeReader.IsError || pathReader.IsError || !typeReader.AtEnd() || !pathReader.AtEnd()) throw new InvalidDataException("Invalid character part.");
        return new ReplayCharacterPart(kind, path);
    }).ToArray();

    private static void ValidateSelections(IReadOnlyList<ReplayCosmeticSelection> selections)
    {
        if (selections.Select(s => (s.Slot, s.Index)).Distinct().Count() != selections.Count)
            throw new ArgumentException("Choose one cosmetic for each slot.");
        foreach (var selection in selections)
        {
            _ = AssetType(selection.Slot);
            var (_, name) = ReplayCosmeticGuidExports.SplitPath(selection.AssetPath);
            if (string.IsNullOrWhiteSpace(selection.AssetId) || !string.Equals(name, selection.AssetId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The cosmetic ID must match the object name in its resolved asset path.");
            if (selection.Index < 0 || (selection.Slot is not (ReplayCosmeticSlot.Emote or ReplayCosmeticSlot.Wrap) && selection.Index != 0))
                throw new ArgumentException("Only emotes and wraps have an indexed cosmetic slot.");
            if (selection.Slot == ReplayCosmeticSlot.Character && selection.CharacterParts?.Any(p => p.Type == 1) != true)
                throw new ArgumentException("The selected outfit has no resolved body part. Load its character parts from the installed game files.");
            if (selection.Slot == ReplayCosmeticSlot.Backpack && selection.CharacterParts?.Any(p => p.Type == 3) != true)
                throw new ArgumentException("The selected back bling has no resolved backpack part. Load its character parts from the installed game files.");
            foreach (var part in selection.CharacterParts ?? [])
            {
                if (part.Type is < 0 or > 15) throw new ArgumentException("Invalid character part type.");
                ReplayCosmeticGuidExports.SplitPath(part.Path);
            }
            if (selection.HeroTypePath != null) ReplayCosmeticGuidExports.SplitPath(selection.HeroTypePath);
            if (selection.Slot == ReplayCosmeticSlot.Pickaxe)
            {
                if (selection.PickaxeWeaponDefinitionPath == null)
                    throw new ArgumentException("The selected pickaxe has no resolved harvest weapon definition. Reload its details from the cosmetic library.");
                ReplayCosmeticGuidExports.SplitPath(selection.PickaxeWeaponDefinitionPath);
                if (selection.PickaxeWeaponActorClassPath != null) ReplayCosmeticGuidExports.SplitPath(selection.PickaxeWeaponActorClassPath);
            }
            if (selection.CharacterGender is < 0 or > 3 || selection.CharacterBodyType is < 0 or > 8)
                throw new ArgumentException("The selected cosmetic has an invalid character gender or body type.");
        }
    }

    private static byte[] PatchBytes(byte[] source, IEnumerable<(int Start, int Length, byte[] Bytes)> changes)
    {
        using var output = new MemoryStream();
        var offset = 0;
        foreach (var change in changes.OrderBy(e => e.Start))
        {
            if (change.Start < offset || change.Start > source.Length - change.Length) throw new InvalidDataException("Overlapping or invalid replay edits.");
            output.Write(source.AsSpan(offset, change.Start - offset));
            output.Write(change.Bytes);
            offset = change.Start + change.Length;
        }
        output.Write(source.AsSpan(offset));
        return output.ToArray();
    }
}


