using System.Text;
using FortniteReplayReader.Models;
using FortniteReplayReader.Models.NetFieldExports;
using Unreal.Core;
using Unreal.Core.Models.Enums;

namespace FortniteReplayReader.Editing;

/// <summary>Copies a recorded player's outfit to another player in a Fortnite 43.00 replay.</summary>
public sealed class ReplaySkinSwapper
{
    private readonly ReplayContainer container;
    private readonly ReplaySkinLayoutReader? layout;
    private bool edited;

    public FortniteReplay Replay { get; }
    public IReadOnlyList<PlayerData> Players { get; }

    public ReplaySkinSwapper(ReplayContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        this.container = container;
        if (IsSupported(container))
        {
            layout = new(container);
            Replay = layout.Scan();
        }
        else
        {
            using var stream = new MemoryStream(container.GetBytes(), writable: false);
            Replay = new ReplayReader(parseMode: ParseMode.Normal).ReadReplay(stream);
        }
        Players = Replay.PlayerData.ToArray();
    }

    /// <summary>Changes decoded data in the container. Save it to a new file with ReplayContainer.Save.</summary>
    public SkinSwapResult CopySkin(string targetPlayer, string donorPlayer)
    {
        if (layout == null)
            throw new NotSupportedException("Skin editing currently supports Fortnite 43.00 (network version 45) full-checkpoint replays.");
        if (edited) throw new InvalidOperationException("Create a new swapper for each skin edit.");
        var target = FindPlayer(targetPlayer);
        var donor = FindPlayer(donorPlayer);
        if (target == donor) throw new ArgumentException("Choose two different players.");
        if (string.IsNullOrEmpty(donor.Cosmetics.Character) || donor.Cosmetics.SoftParts is not { Length: > 0 })
            throw new InvalidOperationException("The donor has no complete recorded outfit and character parts.");
        if (target.PlayerId == null || donor.PlayerId == null ||
            !layout.PlayerActors.ContainsKey(target.PlayerId) ||
            !layout.PlayerActors.TryGetValue(donor.PlayerId, out var donorActor))
            throw new InvalidOperationException("Could not resolve both players to recorded actors.");

        var characterProperty = layout.Properties.LastOrDefault(p => IsPlayerProperty(layout, p, donorActor) &&
            p.Name is "CachedLoadoutCharacterUngated" or "CachedLoadoutCharacter" && p.ObjectGuid.HasValue);
        if (characterProperty?.ObjectGuid is not uint characterGuid)
            throw new InvalidOperationException("The donor's outfit has no recorded asset definition.");
        var hero = layout.Properties.LastOrDefault(p => IsPlayerProperty(layout, p, donorActor) && p.Name == "HeroType")?.ObjectGuid;
        var selection = new ReplayCosmeticSelection(ReplayCosmeticSlot.Character, donor.Cosmetics.Character,
            ObjectPath(layout, characterGuid), donor.Cosmetics.SoftParts.Where(p => p.Type != 3)
                .Select(p => new ReplayCharacterPart(p.Type ?? 0, p.Part?.Path ?? throw new InvalidDataException("The donor has an unresolved character part."))).ToArray(),
            hero is > 0 ? ObjectPath(layout, hero.Value) : null, CharacterGender: donor.Cosmetics.CharacterGender,
            CharacterBodyType: donor.Cosmetics.CharacterBodyType);
        var result = new ReplayCosmeticEditor(container).ApplyCosmetics(target.PlayerId, [selection],
            ReplayCosmeticEditor.RecordedOutfitVariants(layout, donorActor));
        edited = true;
        return new(target.PlayerName ?? targetPlayer, donor.PlayerName ?? donorPlayer, target.Cosmetics.Character,
            donor.Cosmetics.Character, result.ChangedProperties, result.ChangedPackets, result.ChangedCheckpoints);
    }

    private static string ObjectPath(ReplaySkinLayoutReader scan, uint guid)
    {
        var parts = new List<string>();
        var visited = new HashSet<uint>();
        while (guid != 0)
        {
            if (!visited.Add(guid) || !scan.GuidDefinitions.TryGetValue(guid, out var definition))
                throw new InvalidOperationException("The donor's asset path is not fully exported in this replay.");
            parts.Add(definition.Path);
            guid = definition.Outer;
        }
        parts.Reverse();
        return string.Join(".", parts);
    }

    internal static void ValidateProperties(ReplaySkinLayoutReader before, ReplaySkinLayoutReader after,
        Dictionary<SkinProperty, ReplayBits> replacements)
    {
        if (before.Properties.Count != after.Properties.Count)
            throw new InvalidDataException("Skin edit validation failed: a recorded cosmetic property was lost or added.");
        for (var i = 0; i < before.Properties.Count; i++)
        {
            var original = before.Properties[i];
            var actual = after.Properties[i];
            var expected = replacements.GetValueOrDefault(original) ?? original.Value;
            if (original.Actor != actual.Actor || original.Group != actual.Group || original.Name != actual.Name || !EqualBits(expected, actual.Value))
                throw new InvalidDataException($"Skin edit validation failed for actor {original.Actor}, property {original.Name}.");
            if (replacements.ContainsKey(original) && actual.ObjectGuid.HasValue && !actual.ObjectResolved)
                throw new InvalidDataException($"The replacement asset for {actual.Name} (GUID {actual.ObjectGuid}) is not exported before the target uses it.");
        }
    }

    internal static bool IsSupported(ReplayContainer container) =>
        container.Header.Branch.StartsWith("++Fortnite+Release-43.00", StringComparison.Ordinal) &&
        (int)container.Header.EngineNetworkVersion == 45 && (int)container.Header.NetworkVersion == 19 &&
        container.Header.Flags.HasFlag(ReplayHeaderFlags.HasStreamingFixes) &&
        !container.Header.Flags.HasFlag(ReplayHeaderFlags.DeltaCheckpoints);

    private PlayerData FindPlayer(string selector)
    {
        var matches = Players.Where(p => string.Equals(p.PlayerName, selector, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.PlayerId, selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"Player '{selector}' was not found. Use inspect to list recorded players."),
            _ => throw new ArgumentException($"Player name '{selector}' is ambiguous; use the player ID from inspect.")
        };
    }

    private static bool IsPlayerProperty(ReplaySkinLayoutReader scan, SkinProperty property, uint actor) =>
        property.Actor == actor || scan.PawnStates.GetValueOrDefault(property.Actor) == actor;

    internal static ReplayBits ReplaceCharacterAsset(SkinProperty property, string character, Dictionary<int, string> types)
    {
        var value = property.Value;
        using var reader = new NetBitReader(value.Bytes, value.Length) { EngineNetworkVersion = (EngineNetworkVersionHistory)45 };
        var count = checked((int)reader.ReadIntPacked());
        foreach (var index in types.Keys.Where(i => i >= count).ToArray()) types.Remove(index);
        var edits = new List<ReplayBitEdit>();
        while (!reader.AtEnd())
        {
            var index = checked((int)reader.ReadIntPacked()) - 1;
            if (index < 0) break;
            if (index >= count) throw new InvalidDataException("Invalid primary asset array index.");
            var names = new List<(int SizeStart, int SizeLength, int Start, int Length)>();
            while (!reader.AtEnd())
            {
                var handle = reader.ReadIntPacked();
                if (handle == 0) break;
                if (!property.ExportGroup.IsValidIndex(handle - 1)) throw new InvalidDataException("Invalid primary asset field.");
                var sizeStart = reader.Position;
                var length = checked((int)reader.ReadIntPacked());
                var start = reader.Position;
                var field = property.ExportGroup.NetFieldExports[handle - 1]?.Name;
                var fieldValue = value.Slice(start, length);
                if (field == "Name")
                {
                    using var typeReader = new NetBitReader(fieldValue.Bytes, fieldValue.Length) { EngineNetworkVersion = (EngineNetworkVersionHistory)45 };
                    types[index] = typeReader.ReadFName();
                    if (typeReader.IsError || !typeReader.AtEnd()) throw new InvalidDataException("Invalid primary asset type name.");
                }
                else if (field == "PrimaryAssetName") names.Add((sizeStart, start - sizeStart, start, length));
                reader.SkipBits(length);
                if (reader.IsError) throw new InvalidDataException("Truncated primary asset array.");
            }
            if (types.GetValueOrDefault(index) == "AthenaCharacter")
            {
                var replacement = FName(character);
                foreach (var name in names)
                {
                    edits.Add(new(name.Start, name.Length, replacement));
                    edits.Add(new(name.SizeStart, name.SizeLength, ReplayBits.Packed((uint)replacement.Length)));
                }
            }
        }
        return value.Patch(edits);
    }

    internal static ReplayBits WriteParts(SkinProperty property, SoftCharacterPart[] parts) =>
        WriteArray(property, parts.Select(part => new Dictionary<string, ReplayBits>
        {
            ["SoftPart"] = part.Part?.Path is string path ? ReplayBits.Join([ReplayBits.UInt(1, 1), FString(path)]) : ReplayBits.UInt(0, 1),
            ["Type"] = ReplayBits.UInt(checked((uint)(part.Type ?? 0)), 4)
        }));

    internal static ReplayBits WriteArray(SkinProperty property, IEnumerable<Dictionary<string, ReplayBits>> values)
    {
        var elements = values.ToArray();
        var output = new List<ReplayBits> { ReplayBits.Packed((uint)elements.Length) };
        for (var i = 0; i < elements.Length; i++)
        {
            output.Add(ReplayBits.Packed((uint)i + 1));
            var fields = elements[i].Select(field =>
            {
                var export = property.ExportGroup.NetFieldExports.Skip((int)property.Handle + 1)
                    .FirstOrDefault(e => e?.Name == field.Key) ?? throw new InvalidDataException($"Missing array field {field.Key}.");
                return (Handle: export.Handle, Value: field.Value);
            }).OrderBy(field => field.Handle);
            foreach (var field in fields)
            {
                output.Add(ReplayBits.Packed(field.Handle + 1));
                output.Add(ReplayBits.Packed((uint)field.Value.Length));
                output.Add(field.Value);
            }
            output.Add(ReplayBits.Packed(0));
        }
        output.Add(ReplayBits.Packed(0));
        return ReplayBits.Join(output);
    }

    internal static ReplayBits FString(string value)
    {
        var unicode = value.Any(character => character > 127);
        var bytes = (unicode ? Encoding.Unicode : Encoding.ASCII).GetBytes(value + "\0");
        var length = unicode ? unchecked((uint)-(value.Length + 1)) : (uint)bytes.Length;
        return ReplayBits.Join([ReplayBits.UInt(length, 32), new(bytes, bytes.Length * 8)]);
    }

    internal static ReplayBits FName(string value) => ReplayBits.Join([ReplayBits.UInt(0, 1), FString(value), ReplayBits.UInt(0, 32)]);

    internal static bool EqualBits(ReplayBits a, ReplayBits b) =>
        a.Length == b.Length && a.Slice(0, a.Length).Bytes.AsSpan().SequenceEqual(b.Slice(0, b.Length).Bytes);

    internal static void SplitBunch(SkinBunch bunch, ReplayBits bits, Dictionary<SkinFragment, List<SkinFragment>> replacements, bool requireMappedGuids = false)
    {
        if (bunch.Fragments.Any(f => f.Exports || f.ConsumedExportBits != 0))
            throw new NotSupportedException("Editing cosmetics in an inline package-map export bunch is unsupported.");
        var position = 0;
        for (var i = 0; i < bunch.Fragments.Count; i++)
        {
            var original = bunch.Fragments[i];
            var left = bits.Length - position;
            var capacity = Math.Min(16383, original.Payload.Length + 16383 - original.Packet.Bits.Length);
            var final = i == bunch.Fragments.Count - 1;
            var reserve = 8 * (bunch.Fragments.Count - i - 1);
            var length = final ? left : Math.Min(capacity & ~7, (left - reserve) & ~7);
            if (length < (final ? 1 : 8) || length > capacity)
                throw new NotSupportedException("This cosmetic is too large for the original replay packets. Choose a smaller combination or a different recording.");
            replacements[original] = [original with
            {
                // Preserve every original fragment and packet. Adding packets
                // changes implicit unreliable bunch sequence numbers on seek.
                MustMap = requireMappedGuids || original.MustMap,
                Payload = bits.Slice(position, length)
            }];
            position += length;
        }
    }

    private static ReplayBits EncodeFragment(SkinFragment fragment)
    {
        static ReplayBits Bit(bool value) => ReplayBits.UInt(value ? 1u : 0u, 1);
        var parts = new List<ReplayBits> { Bit(fragment.Open || fragment.Close) };
        if (fragment.Open || fragment.Close)
        {
            parts.Add(Bit(fragment.Open)); parts.Add(Bit(fragment.Close));
        }
        if (fragment.Close) parts.Add(ReplayBits.Serialized(fragment.CloseReason, 15));
        parts.Add(Bit(fragment.Paused)); parts.Add(Bit(fragment.Reliable));
        parts.Add(ReplayBits.Packed(fragment.Channel));
        parts.Add(Bit(fragment.Exports)); parts.Add(Bit(fragment.MustMap)); parts.Add(Bit(fragment.Partial));
        if (fragment.Partial)
        {
            parts.Add(Bit(fragment.Initial)); parts.Add(Bit(fragment.CustomFinal)); parts.Add(Bit(fragment.Final));
        }
        if (fragment.Open || fragment.Reliable) parts.Add(fragment.Name);
        parts.Add(ReplayBits.Serialized((uint)fragment.Payload.Length, 16384));
        parts.Add(fragment.Payload);
        return ReplayBits.Join(parts);
    }

    internal static byte[] EncodePackets(SkinPacket packet, Dictionary<SkinFragment, List<SkinFragment>> replacements)
    {
        var values = packet.Fragments.SelectMany(f => replacements.TryGetValue(f, out var list) ? list : [f]);
        using var output = new MemoryStream();
        using var writer = new System.IO.BinaryWriter(output, Encoding.UTF8, true);
        var pending = new List<ReplayBits>();
        var pendingLength = 0;
        void Flush()
        {
            if (pending.Count == 0) return;
            var complete = ReplayBits.Join(pending.Append(ReplayBits.UInt(1, 1)));
            writer.Write(ReplayBits.Packed(packet.Level).Bytes);
            writer.Write(complete.Bytes.Length);
            writer.Write(complete.Bytes);
            pending.Clear(); pendingLength = 0;
        }
        foreach (var value in values)
        {
            var encoded = EncodeFragment(value);
            if (encoded.Length + 1 > 16384) throw new InvalidDataException("Edited bunch exceeds maximum packet size.");
            if (pendingLength + encoded.Length + 1 > 16384)
                throw new NotSupportedException("This cosmetic change would require inserting a network packet. The original replay was preserved.");
            pending.Add(encoded); pendingLength += encoded.Length;
        }
        Flush();
        return output.ToArray();
    }
}

public sealed record SkinSwapResult(string TargetPlayer, string DonorPlayer, string OriginalCharacter,
    string ReplacementCharacter, int ChangedProperties, int ChangedPackets, int ChangedCheckpoints);

