using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using FortniteReplayReader.Models;
using FortniteReplayReader.Models.NetFieldExports;
using Unreal.Core;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;
using Archive = Unreal.Core.BinaryReader;

namespace FortniteReplayReader.Editing;

// Records the original framing while the normal reader interprets actor identities.
// Unknown properties and packets remain opaque: edits only replace named cosmetic fields.
internal sealed class ReplaySkinLayoutReader : ReplayReader
{
    private readonly ReplayContainer container;
    private readonly ReplayContainerChunk? checkpoint;
    private ReplayContainerChunk currentChunk = null!;
    private SkinPacket packet = null!;
    private int fragmentIndex;
    private List<SkinFragment>? partial;
    private SkinBunch? bunch;
    private int origin;
    private Exception? packetFailure;
    private float currentTime;

    public List<SkinPacket> Packets { get; } = [];
    public List<SkinBunch> Bunches { get; } = [];
    public List<SkinProperty> Properties { get; } = [];
    public Dictionary<uint, SkinGuidDefinition> GuidDefinitions { get; } = [];
    public HashSet<uint> KnownGuids { get; } = [];
    public List<SkinGuidExportSection> GuidExportSections { get; } = [];
    public Dictionary<string, uint> PlayerActors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<uint, uint> PawnStates { get; } = [];
    public Dictionary<uint, uint> WeaponOwners { get; } = [];
    public Dictionary<uint, uint> WeaponInstigators { get; } = [];

    internal ReplaySkinLayoutReader(ReplayContainer container, ReplayContainerChunk? checkpoint = null, ILogger? logger = null)
        : base(logger, ParseMode.Normal)
    {
        this.container = container;
        this.checkpoint = checkpoint;
    }

    public FortniteReplay Scan()
    {
        // All metadata and decoded chunks are already owned by the container.
        // In particular, scanning a modified checkpoint must not recompress the
        // entire replay just to reconstruct its outer stream.
        using var input = new MemoryStream();
        return ReadReplay(input);
    }

    public override void ReadReplayInfo(FArchive archive)
    {
        Replay.Info = container.Info;
        archive.ReplayVersion = container.Info.FileVersion;
    }

    public override void ReadReplayChunks(FArchive outer)
    {
        var headerChunk = container.Chunks.Single(c => c.Type == ReplayChunkType.Header);
        using var header = new Archive(headerChunk.Data.AsMemory()) { ReplayVersion = container.Info.FileVersion };
        ReadReplayHeader(header);

        foreach (var chunk in container.Chunks)
        {
            if (checkpoint == null ? chunk.Type != ReplayChunkType.ReplayData : chunk != checkpoint)
                continue;
            currentChunk = chunk;
            using var data = CreateArchive(chunk.Data);
            if (checkpoint != null)
            {
                ReadCheckpointData(data);
            }
            else
            {
                while (!data.AtEnd())
                {
                    var start = data.Position;
                    ReadDemoFrameIntoPlaybackPackets(data);
                    if (data.IsError || data.Position <= start)
                        throw new InvalidDataException($"Invalid frame in chunk {chunk.Index} at {start}.");
                }
            }
        }
        if (partial != null)
            throw new InvalidDataException("Replay ends with an incomplete fragmented network bunch.");
        KnownGuids.UnionWith(_netGuidCache.NetGuidToPathName.Keys);
    }

    public override void ReadNetExportGuids(FArchive archive)
    {
        var start = archive.Position;
        var count = archive.ReadIntPacked();
        var recordsStart = archive.Position;
        archive.Seek(start, SeekOrigin.Begin);
        base.ReadNetExportGuids(archive);
        GuidExportSections.Add(new(currentChunk, start, recordsStart - start, archive.Position - start, count));
    }

    private Archive CreateArchive(byte[] bytes) => new(bytes.AsMemory())
    {
        NetworkVersion = container.Header.NetworkVersion,
        EngineNetworkVersion = container.Header.EngineNetworkVersion,
        ReplayHeaderFlags = container.Header.Flags,
        ReplayVersion = container.Info.FileVersion,
        NetworkReplayVersion = new()
        {
            Major = container.Header.Major, Minor = container.Header.Minor,
            Patch = container.Header.Patch, Branch = container.Header.Branch,
            Changelist = container.Header.Changelist
        }
    };

    protected override FArchive DecryptBuffer(FArchive archive, int size) => CreateArchive(currentChunk.Data);
    protected override FArchive Decompress(FArchive archive) => archive;

    public override NetworkGUID InternalLoadObject(FArchive archive, bool isExportingNetGUIDBunch, int internalLoadObjectRecursionCount = 0)
    {
        var start = archive.Position;
        var guid = base.InternalLoadObject(archive, isExportingNetGUIDBunch, internalLoadObjectRecursionCount);
        KnownGuids.Add(guid.Value);
        if (!isExportingNetGUIDBunch || archive.IsError || archive.Position <= start)
            return guid;

        // The normal cache deliberately keeps only normalized leaf names. Keep
        // the exact export alongside it so cosmetic edits can validate both
        // an object and its package/outer chain without guessing a content path.
        var end = archive.Position;
        archive.Seek(start, SeekOrigin.Begin);
        using FArchive copy = archive is FBitArchive bits
            ? new NetBitReader(bits.ReadBits(end - start), end - start)
            : new Archive(archive.ReadMemory(end - start));
        archive.Seek(end, SeekOrigin.Begin);
        CaptureGuidDefinition(copy, internalLoadObjectRecursionCount);
        return guid;
    }

    private uint CaptureGuidDefinition(FArchive archive, int depth)
    {
        if (depth > 16) return 0;
        var guid = archive.ReadIntPacked();
        if (guid == 0) return 0;
        var flags = archive.ReadByte();
        if ((flags & (byte)ExportFlags.bHasPath) != 0)
        {
            var outer = CaptureGuidDefinition(archive, depth + 1);
            var path = archive.ReadFString();
            if ((flags & (byte)ExportFlags.bHasNetworkChecksum) != 0) archive.ReadUInt32();
            if (!archive.IsError)
                GuidDefinitions[guid] = new(guid, outer, path, flags);
        }
        return guid;
    }

    public override void ReadDemoFrameIntoPlaybackPackets(FArchive archive)
    {
        archive.ReadInt32(); // current level
        currentTime = archive.ReadSingle();
        ReadExportData(archive);
        var levels = archive.ReadIntPacked();
        for (uint i = 0; i < levels; i++) archive.ReadFString();
        var externalSize = archive.ReadUInt64();
        var externalStart = archive.Position;
        ReadExternalData(archive);
        if ((ulong)(archive.Position - externalStart) != externalSize)
            throw new InvalidDataException("External-data offset does not match the recorded frame layout.");
        if (archive.HasGameSpecificFrameData())
        {
            var gameSize = archive.ReadUInt64();
            if (gameSize > int.MaxValue) throw new InvalidDataException("Invalid game frame data length.");
            archive.SkipBytes((int)gameSize);
        }
        while (true)
        {
            var envelopeStart = archive.Position;
            var levelIndex = archive.ReadIntPacked();
            var length = archive.ReadInt32();
            if (length == 0) break;
            if (length < 1 || length > 2048)
                throw new InvalidDataException($"Invalid packet length {length} in chunk {currentChunk.Index}.");
            var bytes = archive.ReadMemory(length).ToArray();
            if (bytes[^1] == 0) throw new InvalidDataException("Missing replay packet terminator.");
            var bits = length * 8 - 1 - (System.Numerics.BitOperations.LeadingZeroCount((uint)bytes[^1]) - 24);
            packet = new(currentChunk, envelopeStart, archive.Position - envelopeStart, levelIndex, new(bytes, bits), currentTime);
            ReadFragments(packet);
            Packets.Add(packet);
            fragmentIndex = 0;
            packetFailure = null;
            base.ReceivedRawPacket(bytes.AsMemory());
            // The general-purpose parser logs and catches bunch exceptions. An
            // editor must not accept a partially collected layout after one.
            if (packetFailure != null)
                throw new InvalidDataException($"Could not collect packet at {envelopeStart} in chunk {currentChunk.Index}.", packetFailure);
            if (fragmentIndex != packet.Fragments.Count)
                throw new InvalidDataException("The replay reader did not consume every network bunch.");
        }
    }

    private void ReadFragments(SkinPacket document)
    {
        using var reader = new NetBitReader(document.Bits.Bytes, document.Bits.Length)
        {
            EngineNetworkVersion = container.Header.EngineNetworkVersion,
            NetworkVersion = container.Header.NetworkVersion,
            ReplayHeaderFlags = container.Header.Flags
        };
        while (!reader.AtEnd())
        {
            var control = reader.ReadBit();
            var open = control && reader.ReadBit();
            var close = control && reader.ReadBit();
            var reason = close ? reader.ReadSerializedInt((int)ChannelCloseReason.MAX) : 0;
            var paused = reader.ReadBit();
            var reliable = reader.ReadBit();
            var channel = reader.ReadIntPacked();
            var exports = reader.ReadBit();
            var mustMap = reader.ReadBit();
            var isPartial = reader.ReadBit();
            var initial = isPartial && reader.ReadBit();
            var customFinal = isPartial && reader.ReadBit();
            var final = isPartial && reader.ReadBit();
            var nameStart = reader.Position;
            if (reliable || open) reader.ReadFName();
            var name = document.Bits.Slice(nameStart, reader.Position - nameStart);
            var length = (int)reader.ReadSerializedInt(16384);
            var start = reader.Position;
            reader.SkipBits(length);
            if (reader.IsError || reader.Position != start + length)
                throw new InvalidDataException("Network bunch exceeds its packet.");
            document.Fragments.Add(new(document, open, close, reason, paused, reliable, channel,
                exports, mustMap, isPartial, initial, customFinal, final, name,
                start, document.Bits.Slice(start, length)));
        }
    }

    public override void ReceivedRawBunch(DataBunch value)
    {
        try
        {
            CollectBunch(value);
        }
        catch (Exception error)
        {
            packetFailure ??= error;
            throw;
        }
        finally
        {
            bunch = null;
        }
    }

    private void CollectBunch(DataBunch value)
    {
        var fragment = packet.Fragments[fragmentIndex++];
        if (fragment.Channel != value.ChIndex || fragment.Partial != value.bPartial)
            throw new InvalidDataException("Network framing differs from the parser.");
        fragment.ConsumedExportBits = value.bPartial ? value.Archive.Position : value.Archive.Position - fragment.PayloadStart;
        if (!value.bPartial)
        {
            bunch = new([fragment], fragment.Payload);
            origin = fragment.PayloadStart;
        }
        else
        {
            if (value.bPartialInitial)
                partial = [fragment];
            else
                partial?.Add(fragment);
            if (value.bPartialFinal && partial != null)
            {
                // The parser retains the initial export prefix, but entirely
                // excludes later export-only fragments from the assembled data.
                var parts = partial.Select((f, i) => i == 0 ? f.Payload : f.Exports ? ReplayBits.Empty :
                    f.Payload.Slice(f.ConsumedExportBits, f.Payload.Length - f.ConsumedExportBits));
                bunch = new(partial, ReplayBits.Join(parts));
                partial = null;
                origin = 0;
            }
            else bunch = null;
        }
        if (bunch != null) Bunches.Add(bunch);
        base.ReceivedRawBunch(value);
    }

    public override void ReceivedActorBunch(DataBunch value)
    {
        if (bunch != null && value.bHasMustBeMappedGUIDs)
        {
            var archive = value.Archive;
            var start = archive.Position;
            var count = archive.ReadUInt16();
            var required = new uint[count];
            for (var index = 0; index < count; index++) required[index] = archive.ReadIntPacked();
            if (archive.IsError)
                throw new InvalidDataException("Invalid required-GUID prefix in replay bunch.");
            bunch.RequiredGuids = required;
            bunch.RequiredGuidPrefixStart = start - origin;
            bunch.RequiredGuidPrefixLength = archive.Position - start;
            bunch.MissingRequiredGuids = required.Where(guid => !_netGuidCache.NetGuidToPathName.ContainsKey(guid)).Distinct().ToArray();
            archive.Seek(start, SeekOrigin.Begin);
        }
        base.ReceivedActorBunch(value);
    }

    public override uint? ReadContentBlockPayload(DataBunch value, out bool deleted, out bool layout, out uint? payload)
    {
        var headerStart = value.Archive.Position;
        value.Archive.ReadBit();
        var isActor = value.Archive.ReadBit();
        value.Archive.Seek(headerStart, SeekOrigin.Begin);
        var repObject = base.ReadContentBlockHeader(value, out layout, out deleted);
        payload = null;
        if (deleted) return repObject;
        var sizeStart = value.Archive.Position;
        payload = value.Archive.ReadIntPacked();
        if (bunch != null)
            bunch.Blocks.Add(new(sizeStart - origin, value.Archive.Position - sizeStart,
                value.Archive.Position - origin, checked((int)payload.Value), isActor, repObject));
        return repObject;
    }

    public override bool ReceiveProperties(FBitArchive archive, NetFieldExportGroup group, uint channelIndex,
        [NotNullWhen(true)] out INetFieldExportGroup? exportGroup, bool enablePropertyChecksum = true, bool netDeltaUpdate = false)
    {
        var isPickaxe = IsPickaxeGroup(group.PathName);
        if (bunch != null && (group.PathName == "/Script/FortniteGame.FortPlayerStateAthena" ||
            group.PathName == "/Game/Athena/PlayerPawn_Athena.PlayerPawn_Athena_C" ||
            group.PathName == "/Script/FortniteGame.FortPlayerStateComponent_CosmeticDownloads" || isPickaxe))
        {
            var save = archive.Position;
            if (enablePropertyChecksum) archive.ReadBit();
            while (!archive.AtEnd())
            {
                var handle = archive.ReadIntPacked();
                if (handle == 0) break;
                var sizeStart = archive.Position;
                var size = checked((int)archive.ReadIntPacked());
                var dataStart = archive.Position;
                if (!group.IsValidIndex(handle - 1) || size > archive.GetBitsLeft() || archive.IsError)
                    throw new InvalidDataException("Invalid cosmetic property framing.");
                var name = group.NetFieldExports[handle - 1]?.Name;
                if (name != null && (isPickaxe || SkinProperty.Names.Contains(name)) && Channels[channelIndex]?.ActorId is uint actor)
                {
                    var property = new SkinProperty(bunch, actor, group, handle - 1, name,
                        sizeStart - origin, dataStart - sizeStart, dataStart - origin, size);
                    if (SkinProperty.ObjectNames.Contains(name) && size > 0)
                    {
                        var bytes = property.Value;
                        using var objectReader = new NetBitReader(bytes.Bytes, bytes.Length);
                        var guid = objectReader.ReadIntPacked();
                        if (objectReader.IsError || !objectReader.AtEnd())
                            throw new InvalidDataException($"Unexpected object reference layout for {name}.");
                        property.ObjectGuid = guid;
                        property.ObjectResolved = guid == 0 || _netGuidCache.TryGetPathName(guid, out _);
                        if (isPickaxe && name == "Owner") WeaponOwners[actor] = guid;
                        if (isPickaxe && name == "Instigator") WeaponInstigators[actor] = guid;
                    }
                    if (isPickaxe)
                    {
                        property.WeaponOwner = WeaponOwners.GetValueOrDefault(actor);
                        property.WeaponInstigator = WeaponInstigators.GetValueOrDefault(actor);
                    }
                    if (SkinProperty.MontageNames.Contains(name) && size > 0)
                    {
                        var montage = ReplayMontageReference.Read(property.Value);
                        property.ObjectGuid = montage.Guid;
                        property.ObjectResolved = montage.Guid == 0 || _netGuidCache.TryGetPathName(montage.Guid, out _);
                    }
                    Properties.Add(property);
                    bunch.Properties.Add(property);
                }
                archive.SkipBits(size);
            }
            archive.Seek(save, SeekOrigin.Begin);
        }
        return base.ReceiveProperties(archive, group, channelIndex, out exportGroup, enablePropertyChecksum, netDeltaUpdate);
    }

    internal static bool IsPickaxeGroup(string path) => path.Contains("/FORT_Melee/", StringComparison.Ordinal) &&
        path.Contains("Pickaxe", StringComparison.OrdinalIgnoreCase) && !path.Contains(':') && !path.EndsWith("_ClassNetCache", StringComparison.Ordinal);

    protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? value)
    {
        if (Channels[channelIndex]?.ActorId is uint actor)
        {
            if (value is FortPlayerState state && (state.UniqueId ?? state.UniqueID ?? state.BotUniqueId) is string id)
                PlayerActors[id] = actor;
            // An eliminated pawn can replicate a null PlayerState. Keep its
            // last real owner so earlier loadouts and attached weapons remain
            // associated with that player across respawns.
            if (value is PlayerPawn pawn && pawn.PlayerState is uint stateActor && stateActor != 0)
                PawnStates[actor] = stateActor;
        }
        base.OnExportRead(channelIndex, value);
    }
}

internal sealed record SkinBlock(int SizeStart, int SizeLength, int Start, int Length, bool IsActor = true, uint? ObjectGuid = null);

internal sealed class SkinBunch(List<SkinFragment> fragments, ReplayBits bits)
{
    public List<SkinFragment> Fragments { get; } = fragments;
    public ReplayBits Bits { get; } = bits;
    public List<SkinBlock> Blocks { get; } = [];
    public List<SkinProperty> Properties { get; } = [];
    public uint[] RequiredGuids { get; set; } = [];
    public uint[] MissingRequiredGuids { get; set; } = [];
    public int RequiredGuidPrefixStart { get; set; } = -1;
    public int RequiredGuidPrefixLength { get; set; }
}

internal sealed record SkinGuidDefinition(uint Guid, uint Outer, string Path, byte ExportFlags);

internal sealed record SkinGuidExportSection(ReplayContainerChunk Chunk, int Start, int CountLength, int Length, uint Count);

internal sealed record SkinProperty(SkinBunch Bunch, uint Actor, NetFieldExportGroup ExportGroup, uint Handle, string Name,
    int SizeStart, int SizeLength, int Start, int Length)
{
    public string Group => ExportGroup.PathName;
    public uint? ObjectGuid { get; set; }
    public bool ObjectResolved { get; set; }
    public uint? WeaponOwner { get; set; }
    public uint? WeaponInstigator { get; set; }
    public ReplayBits Value => Bunch.Bits.Slice(Start, Length);
    public float TimeSeconds => Bunch.Fragments[^1].Packet.TimeSeconds;
    public static HashSet<string> MontageNames { get; } = ["RepAnimMontageInfo", "ReplayRepAnimMontageInfo"];
    public static HashSet<string> ObjectNames { get; } = new(StringComparer.Ordinal)
    {
        "CachedLoadoutCharacter", "CachedLoadoutCharacterUngated", "HeroType", "Parts", "Character", "Owner", "Instigator", "WeaponData",
        "LastReplicatedEmoteExecuted", "PawnMontage"
    };
    public static HashSet<string> Names { get; } = new(StringComparer.Ordinal)
    {
        "CachedLoadoutCharacter", "CachedLoadoutCharacterUngated", "SoftParts", "RequestedSoftCharacterParts",
        "CharacterGender", "CharacterBodyType", "HeroType", "Parts", "VariantRequiredCharacterParts",
        "Character", "CharacterVariantChannels", "PrimaryAssets", "AssetIndices", "bIsDefaultCharacter",
        "Dances", "ItemWraps", "Owner", "Instigator", "WeaponData", "LastReplicatedEmoteExecuted", "PawnMontage",
        "bIsPlayingEmote", "RepAnimMontageInfo", "ReplayRepAnimMontageInfo", "Montage", "InGameEmoteIADSlots"
    };
}

internal sealed class SkinPacket(ReplayContainerChunk chunk, int start, int length, uint level, ReplayBits bits, float timeSeconds = 0)
{
    public ReplayContainerChunk Chunk { get; } = chunk;
    public int Start { get; } = start;
    public int Length { get; } = length;
    public uint Level { get; } = level;
    public ReplayBits Bits { get; } = bits;
    public float TimeSeconds { get; } = timeSeconds;
    public List<SkinFragment> Fragments { get; } = [];
}

internal sealed record SkinFragment(SkinPacket Packet, bool Open, bool Close, uint CloseReason, bool Paused,
    bool Reliable, uint Channel, bool Exports, bool MustMap, bool Partial, bool Initial, bool CustomFinal,
    bool Final, ReplayBits Name, int PayloadStart, ReplayBits Payload)
{
    public int ConsumedExportBits { get; set; }
}

