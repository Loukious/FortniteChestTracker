using System.Diagnostics.CodeAnalysis;
using FortniteReplayReader.Editing;
using FortniteReplayReader.Models;
using FortniteReplayReader.Models.NetFieldExports;
using Unreal.Core;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;
using Archive = Unreal.Core.BinaryReader;

namespace FortniteReplayReader.Analysis;

/// <summary>
/// Reads named chest-search fields directly from replicated export tables, including
/// current chest classes unknown to the general gameplay model. Never infers an opener from distance.
/// </summary>
public sealed class ReplayChestAnalysisReader : ReplayReader
{
    private readonly ReplayContainer container;
    private readonly CancellationToken cancellationToken;
    private readonly ReplayChestAccumulator chestData = new();
    private readonly ReplayTreasureRewardAccumulator rewardData = new();
    private readonly Dictionary<uint, uint> pawnStates = [];
    private readonly Dictionary<uint, string> playerStates = [];
    private readonly List<string> diagnostics = [];
    private readonly HashSet<NetFieldExportGroup> filteredGroups = [];
    private static readonly HashSet<string> GameFields = new(StringComparer.Ordinal)
    {
        "RecorderPlayerState", "GameSessionId", "UtcTimeStartedMatch", "EventTournamentRound", "bIsLargeTeamGame", "TeamSize"
    };
    private static readonly HashSet<string> PlayerFields = new(StringComparer.Ordinal)
    {
        "PlayerId", "PlayerID", "UniqueId", "UniqueID", "PlatformUniqueNetId", "BotUniqueId",
        "bIsABot", "bIsSpectator", "bOnlySpectator", "TeamIndex", "Platform",
        "PlayerNameCustomOverride", "StreamerModeName", "bIsGameSessionOwner", "PartyOwnerUniqueId"
    };
    private static readonly HashSet<string> PawnFields = new(StringComparer.Ordinal) { "PlayerState" };
    private static readonly HashSet<string> PlaylistFields = new(StringComparer.Ordinal);
    private int searchFieldsDecoded;
    private int decodeErrors;
    private int rewardDecodeErrors;
    private float? currentTime;
    private bool scanned;

    public ReplayChestAnalysisReader(ReplayContainer container, CancellationToken cancellationToken = default)
        : base(null!, ParseMode.Minimal)
    {
        this.container = container;
        this.cancellationToken = cancellationToken;
    }

    public ReplayChestAnalysis Scan()
    {
        if (scanned) throw new InvalidOperationException("Create a new chest reader for each scan.");
        scanned = true;
        cancellationToken.ThrowIfCancellationRequested();
        using var input = new MemoryStream();
        var replay = ReadReplay(input);
        var openings = chestData.Finish(pawnStates, playerStates);
        var rewards = rewardData.Finish(pawnStates, playerStates);
        diagnostics.Add("Counts come from completed chest openings recorded in local replays. Distant teammate openings may be missing.");
        if (openings.Count(opening => opening.PlayerId == null) is int unattributed && unattributed > 0)
            diagnostics.Add($"{unattributed} visible opened chests have no recorded player identity and are excluded from player totals.");
        if (searchFieldsDecoded == 0)
            diagnostics.Add("No supported chest-search fields were recorded; chest coverage is unavailable.");
        if (decodeErrors > 0)
            diagnostics.Add($"{decodeErrors} chest field updates could not be decoded; totals may be incomplete.");
        if (rewardDecodeErrors > 0)
            diagnostics.Add($"{rewardDecodeErrors} treasure reward updates could not be decoded; reward results may be incomplete.");
        return new(replay, openings, diagnostics.ToArray(), chestData.ChestActorsSeen, searchFieldsDecoded, decodeErrors > 0)
            { Rewards = rewards, HasRewardDecodeErrors = rewardDecodeErrors > 0 };
    }

    public override void ReadReplayInfo(FArchive archive)
    {
        Replay.Info = container.Info;
        archive.ReplayVersion = container.Info.FileVersion;
    }

    public override void ReadReplayChunks(FArchive outer)
    {
        var headerChunk = container.Chunks.Single(chunk => chunk.Type == ReplayChunkType.Header);
        using var header = new Archive(headerChunk.Data.AsMemory()) { ReplayVersion = container.Info.FileVersion };
        ReadReplayHeader(header);
        foreach (var chunk in container.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Type != ReplayChunkType.ReplayData) continue;
            using var data = CreateArchive(chunk.Data);
            while (!data.AtEnd())
            {
                var start = data.Position;
                ReadDemoFrameIntoPlaybackPackets(data);
                if (data.IsError || data.Position <= start)
                    throw new InvalidDataException($"Invalid frame in replay chunk {chunk.Index} at {start}.");
            }
        }
    }

    public override void ReadDemoFrameIntoPlaybackPackets(FArchive archive)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = archive.Position;
        if (archive.NetworkVersion >= NetworkVersionHistory.HISTORY_MULTIPLE_LEVELS) archive.ReadInt32();
        currentTime = archive.ReadSingle();
        archive.Seek(start, SeekOrigin.Begin);
        base.ReadDemoFrameIntoPlaybackPackets(archive);
    }

    public override PacketState ReadPacket(FArchive archive)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = base.ReadPacket(archive);
        if (result == PacketState.Error) throw new InvalidDataException("Malformed replay packet.");
        return result;
    }

    public override bool ReceiveProperties(FBitArchive archive, NetFieldExportGroup group, uint channelIndex,
        [NotNullWhen(true)] out INetFieldExportGroup? exportGroup, bool enablePropertyChecksum = true, bool netDeltaUpdate = false)
    {
        exportGroup = null;
        if (ReplayTreasureRewardDecoder.IsRewardGroup(group) && Channels[channelIndex]?.Actor?.ActorNetGUID.Value is uint rewardActor)
        {
            if (!rewardData.ReadUpdate(rewardActor, group, archive, enablePropertyChecksum, currentTime)) rewardDecodeErrors++;
            return false;
        }
        if (IsChestGroup(group) && Channels[channelIndex]?.Actor?.ActorNetGUID.Value is uint actor)
        {
            if (TryReadChestUpdate(archive, group, enablePropertyChecksum, out var searched, out var pawn,
                out var hasPawnUpdate, out var fields))
            {
                searchFieldsDecoded += fields;
                chestData.Update(actor, group.PathName, searched, pawn, hasPawnUpdate, currentTime);
            }
            else decodeErrors++;
            // The completed search has already been decoded; no gameplay chest model is needed.
            return false;
        }
        var reportFields = GetReportFields(group.PathName);
        if (reportFields is null) return false;
        if (filteredGroups.Add(group)) KeepReportFields(group, reportFields);
        return base.ReceiveProperties(archive, group, channelIndex, out exportGroup, enablePropertyChecksum, netDeltaUpdate);
    }

    private static HashSet<string>? GetReportFields(string groupPath) => groupPath switch
    {
        "/Game/Athena/Athena_GameState.Athena_GameState_C" => GameFields,
        "/Script/FortniteGame.FortPlayerStateAthena" => PlayerFields,
        "/Game/Athena/PlayerPawn_Athena.PlayerPawn_Athena_C" => PawnFields,
        "CurrentPlaylistInfo" => PlaylistFields,
        _ => null
    };

    internal static void KeepReportFields(NetFieldExportGroup group) =>
        KeepReportFields(group, GetReportFields(group.PathName) ?? []);

    private static void KeepReportFields(NetFieldExportGroup group, HashSet<string> reportFields)
    {
        // Each field is length-delimited by the replay. Skip unrelated payloads before
        // decoding models such as cosmetic arrays, weapons, or player movement.
        foreach (var field in group.NetFieldExports)
            if (field?.Name is string name && !reportFields.Contains(name)) field.Incompatible = true;
    }

    internal static bool IsChestGroup(NetFieldExportGroup group) =>
        group.PathName.Contains("Chest", StringComparison.OrdinalIgnoreCase) &&
        !group.PathName.Contains("Ammo", StringComparison.OrdinalIgnoreCase) &&
        group.NetFieldExports.Any(field => field?.Name == "bAlreadySearched");

    internal static bool TryReadChestUpdate(FBitArchive archive, NetFieldExportGroup group, bool checksum,
        out bool? searched, out uint? pawn, out bool hasPawnUpdate, out int decodedFields)
    {
        searched = null;
        pawn = null;
        hasPawnUpdate = false;
        decodedFields = 0;
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
            if (name is not ("bAlreadySearched" or "SearchingPawn"))
            {
                archive.SkipBits((int)count);
                continue;
            }
            using var bits = new NetBitReader(archive.ReadBits((int)count), (int)count);
            if (name == "bAlreadySearched") searched = bits.ReadBit();
            else
            {
                pawn = bits.ReadIntPacked();
                hasPawnUpdate = true;
            }
            if (bits.IsError || !bits.AtEnd()) return false;
            decodedFields++;
        }
        return false;
    }

    protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup)
    {
        if (Channels[channelIndex]?.Actor?.ActorNetGUID.Value is uint actor)
        {
            if (exportGroup is PlayerPawn pawn && pawn.PlayerState is uint playerState && playerState != 0)
                pawnStates[actor] = playerState;
            else if (exportGroup is FortPlayerState state)
            {
                var id = state.bIsABot == true ? state.BotUniqueId : state.UniqueId ?? state.UniqueID ?? state.PlatformUniqueNetId;
                if (!string.IsNullOrWhiteSpace(id)) playerStates[actor] = id;
            }
        }
        // PlayerPawn is used only for actor attribution above; its gameplay and
        // cosmetic updates do not contribute to the chest report.
        if (exportGroup is GameState or PlaylistInfo or FortPlayerState)
            base.OnExportRead(channelIndex, exportGroup);
    }

    protected override void OnNetDeltaRead(uint channelIndex, NetDeltaUpdate update) { }

    private Archive CreateArchive(byte[] bytes) => new(bytes.AsMemory())
    {
        NetworkVersion = container.Header.NetworkVersion,
        EngineNetworkVersion = container.Header.EngineNetworkVersion,
        ReplayHeaderFlags = container.Header.Flags,
        ReplayVersion = container.Info.FileVersion,
        NetworkReplayVersion = new()
        {
            Major = container.Header.Major, Minor = container.Header.Minor,
            Patch = container.Header.Patch, Branch = container.Header.Branch, Changelist = container.Header.Changelist
        }
    };
}
