using FortniteReplayReader.Models;

namespace FortniteReplayReader.Analysis;

/// <summary>A recorded chest opening or attributed destruction, identified by the chest actor.</summary>
public sealed record ReplayChestOpening(uint ChestActorGuid, string ChestClass, uint? SearchingPawnGuid,
    uint? PlayerStateActorGuid, string? PlayerId, float? ReplayTimeSeconds)
{
    public bool IsDestruction { get; init; }
}

/// <summary>Counts are an observed minimum; local replays do not record every distant teammate interaction.</summary>
public sealed record ReplayChestAnalysis(FortniteReplay Replay, IReadOnlyList<ReplayChestOpening> Openings,
    IReadOnlyList<string> Diagnostics, int ChestActorsSeen, int SearchFieldsDecoded, bool HasDecodeErrors)
{
    public IReadOnlyList<ReplayTreasureReward> Rewards { get; init; } = [];
    public bool HasRewardDecodeErrors { get; init; }
}

internal sealed class ReplayChestAccumulator
{
    private sealed class ChestState(string chestClass)
    {
        public string Class { get; } = chestClass;
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
    public int ChestActorsSeen => chests.Count;
    public int UnsupportedDestroyedActors => chests.Count(pair => pair.Value.Destroyed && !pair.Value.Completed);

    internal void Update(uint actor, string chestClass, bool? searched, uint? pawn, bool hasPawnUpdate, float? time) =>
        Update(actor, chestClass, new(searched, pawn, hasPawnUpdate, null, null, 0), time);

    internal void Update(uint actor, string chestClass, ReplayChestUpdate update, float? time)
    {
        if (!chests.TryGetValue(actor, out var state)) chests[actor] = state = new(chestClass);
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
        IReadOnlyDictionary<uint, string> playerStates) => chests.Where(pair => pair.Value.Completed).Select(pair =>
    {
        var state = pair.Value;
        uint? playerState = state.OpeningPawn is uint pawn && pawnStates.TryGetValue(pawn, out var stateGuid) ? stateGuid : null;
        var id = playerState is uint guid && playerStates.TryGetValue(guid, out var value) ? value : null;
        return new ReplayChestOpening(pair.Key, state.Class, state.OpeningPawn, playerState, id, state.Time)
            { IsDestruction = state.IsDestruction };
    }).ToArray();
}
