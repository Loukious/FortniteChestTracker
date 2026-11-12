using FortniteReplayReader.Models;

namespace FortniteReplayReader.Analysis;

/// <summary>A completed chest search observed in the replay, identified by the chest actor.</summary>
public sealed record ReplayChestOpening(uint ChestActorGuid, string ChestClass, uint? SearchingPawnGuid,
    uint? PlayerStateActorGuid, string? PlayerId, float? ReplayTimeSeconds);

/// <summary>Counts are an observed minimum; local replays do not record every distant teammate interaction.</summary>
public sealed record ReplayChestAnalysis(FortniteReplay Replay, IReadOnlyList<ReplayChestOpening> Openings,
    IReadOnlyList<string> Diagnostics, int ChestActorsSeen, int SearchFieldsDecoded, bool HasDecodeErrors);

internal sealed class ReplayChestAccumulator
{
    private sealed class ChestState(string chestClass)
    {
        public string Class { get; } = chestClass;
        public bool Completed { get; set; }
        public uint? ActivePawn { get; set; }
        public uint? OpeningPawn { get; set; }
        public float? Time { get; set; }
    }

    private readonly Dictionary<uint, ChestState> chests = [];
    public int ChestActorsSeen => chests.Count;

    internal void Update(uint actor, string chestClass, bool? searched, uint? pawn, bool hasPawnUpdate, float? time)
    {
        if (!chests.TryGetValue(actor, out var state)) chests[actor] = state = new(chestClass);
        // A null pawn in the completed update clears the current interaction.
        // Preserve its preceding nonzero pawn for that completion only.
        uint? openingPawn = hasPawnUpdate && pawn > 0 ? pawn : state.ActivePawn;
        if (searched == true && !state.Completed)
        {
            state.Completed = true;
            state.Time = time;
            state.OpeningPawn = openingPawn;
        }
        else if (state.Completed && state.OpeningPawn == null && hasPawnUpdate && pawn > 0)
        {
            // Some replays split the completed-state and pawn reference over updates.
            state.OpeningPawn = pawn;
        }
        if (hasPawnUpdate) state.ActivePawn = pawn > 0 ? pawn : null;
    }

    internal IReadOnlyList<ReplayChestOpening> Finish(IReadOnlyDictionary<uint, uint> pawnStates,
        IReadOnlyDictionary<uint, string> playerStates) => chests.Where(pair => pair.Value.Completed).Select(pair =>
    {
        var state = pair.Value;
        uint? playerState = state.OpeningPawn is uint pawn && pawnStates.TryGetValue(pawn, out var stateGuid) ? stateGuid : null;
        var id = playerState is uint guid && playerStates.TryGetValue(guid, out var value) ? value : null;
        return new ReplayChestOpening(pair.Key, state.Class, state.OpeningPawn, playerState, id, state.Time);
    }).ToArray();
}
