using FortniteReplayReader.Models;

namespace FortniteReplayReader.Analysis;

/// <summary>A recorded chest opening or attributed destruction in one chest reset cycle.</summary>
public sealed record ReplayChestOpening(uint ChestActorGuid, string ChestClass, uint? SearchingPawnGuid,
    uint? PlayerStateActorGuid, string? PlayerId, float? ReplayTimeSeconds)
{
    public bool IsDestruction { get; init; }
    /// <summary>Zero for the initial chest state; advances on explicitly recorded resets.</summary>
    public int ResetCycleIndex { get; init; }
}

/// <summary>Counts are an observed minimum; local replays do not record every distant teammate interaction.</summary>
public sealed record ReplayChestAnalysis(FortniteReplay Replay, IReadOnlyList<ReplayChestOpening> Openings,
    IReadOnlyList<string> Diagnostics, int ChestActorsSeen, int SearchFieldsDecoded, bool HasDecodeErrors)
{
    public IReadOnlyList<ReplayTreasureReward> Rewards { get; init; } = [];
    public bool HasRewardDecodeErrors { get; init; }
    public IReadOnlyList<ReplayTreasureGrant> Grants { get; init; } = [];
    public bool HasGrantDecodeErrors { get; init; }
}

internal sealed class ReplayChestAccumulator
{
    private sealed class ChestState(uint actorGuid, string chestClass)
    {
        public uint ActorGuid { get; } = actorGuid;
        public string Class { get; } = chestClass;
        public InteractionState Current { get; set; } = new(0);
        public List<InteractionState> PreviousCycles { get; } = [];
    }

    private sealed class InteractionState(int resetCycleIndex)
    {
        public int ResetCycleIndex { get; } = resetCycleIndex;
        public bool Completed { get; set; }
        public bool Searched { get; set; }
        public bool Destroyed { get; set; }
        public bool IsDestruction { get; set; }
        public uint? AnimationCount { get; set; }
        public uint? ActivePawn { get; set; }
        public uint? OpeningPawn { get; set; }
        public float? Time { get; set; }
    }

    private readonly Dictionary<uint, ChestState> chests = [];
    private readonly Dictionary<string, ChestState> stableChests = new(StringComparer.Ordinal);
    public int ChestActorsSeen => chests.Values.Distinct().Count();
    public int UnsupportedDestroyedActors => chests.Values.Distinct().Count(chest =>
        chest.PreviousCycles.Append(chest.Current).Any(state => state.Destroyed && !state.Completed));

    internal void Update(uint actor, string chestClass, bool? searched, uint? pawn, bool hasPawnUpdate, float? time,
        string? stableActorPath = null) =>
        Update(actor, chestClass, new(searched, pawn, hasPawnUpdate, null, null, 0), time, stableActorPath);

    internal void Update(uint actor, string chestClass, ReplayChestUpdate update, float? time,
        string? stableActorPath = null)
    {
        ChestState chest;
        if (!string.IsNullOrWhiteSpace(stableActorPath) && stableChests.TryGetValue(stableActorPath, out var placedChest))
        {
            // The reader supplies only verified full placed-instance paths. A new
            // GUID for that same instance is another export of its existing state.
            chests[actor] = chest = placedChest;
        }
        else
        {
            if (!chests.TryGetValue(actor, out chest!)) chests[actor] = chest = new(actor, chestClass);
            if (!string.IsNullOrWhiteSpace(stableActorPath)) stableChests[stableActorPath] = chest;
        }
        var state = chest.Current;
        if (update.Searched == false && update.SearchAnimationCount == 0)
        {
            // These two explicit fields occur together in recorded chest resets.
            // A false flag or counter update alone is insufficient evidence. Keep
            // the old interaction frozen so the next opener cannot fill its identity.
            if (state.Completed || state.Destroyed || state.AnimationCount is > 0)
            {
                chest.PreviousCycles.Add(state);
                chest.Current = state = new(state.ResetCycleIndex + 1);
            }
            else if (state.AnimationCount is null)
            {
                // The first reset marker also clears an unfinished old interaction.
                // Repeated zero-state exports must not clear a new searching pawn.
                state.ActivePawn = null;
            }
        }
        var pawn = update.SearchingPawn;
        var positivePawn = update.HasPawnUpdate && pawn is > 0;
        // A null pawn in the completed update clears the current interaction.
        // Preserve its preceding nonzero pawn for that completion only.
        uint? openingPawn = positivePawn ? pawn : state.ActivePawn;
        if (update.Searched == true && !state.Searched)
        {
            if (state.Completed && state.IsDestruction && !positivePawn)
                openingPawn = state.OpeningPawn;
            state.Searched = true;
            state.Completed = true;
            state.IsDestruction = false;
            state.Time ??= time;
            // A later explicit search takes priority over a destruction observation.
            // If its pawn was not repeated, retain the recorded completion identity
            // rather than assigning an unrelated intervening active pawn.
            state.OpeningPawn = openingPawn ?? state.OpeningPawn;
        }
        else if (!state.Completed && update.Destroyed == true &&
            update.SearchAnimationCount is > 0 &&
            (state.AnimationCount is null || update.SearchAnimationCount > state.AnimationCount) &&
            positivePawn)
        {
            // The destruction update must itself contain fresh completed-interaction
            // data. An old searching pawn, a close, or destruction alone is insufficient.
            state.Completed = true;
            state.IsDestruction = true;
            state.Time = time;
            state.OpeningPawn = pawn;
        }
        else if (state.Completed && !state.IsDestruction && state.OpeningPawn == null && positivePawn)
        {
            // Some replays split the completed-state and pawn reference over updates.
            state.OpeningPawn = pawn;
        }
        if (update.Destroyed == true) state.Destroyed = true;
        if (update.SearchAnimationCount is uint animationCount) state.AnimationCount = animationCount;
        if (update.HasPawnUpdate) state.ActivePawn = pawn > 0 ? pawn : null;
    }

    internal IReadOnlyList<ReplayChestOpening> Finish(IReadOnlyDictionary<uint, uint> pawnStates,
        IReadOnlyDictionary<uint, string> playerStates) => chests.Values.Distinct().SelectMany(chest =>
        chest.PreviousCycles.Append(chest.Current).Where(state => state.Completed).Select(state =>
    {
        uint? playerState = state.OpeningPawn is uint pawn && pawnStates.TryGetValue(pawn, out var stateGuid) ? stateGuid : null;
        var id = playerState is uint guid && playerStates.TryGetValue(guid, out var value) ? value : null;
        return new ReplayChestOpening(chest.ActorGuid, chest.Class, state.OpeningPawn, playerState, id, state.Time)
            { IsDestruction = state.IsDestruction, ResetCycleIndex = state.ResetCycleIndex };
    })).ToArray();
}
