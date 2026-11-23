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

public sealed record ReplayPlayerDisconnect(uint PlayerStateActorGuid, string? PlayerId, float? ReplayTimeSeconds);
public sealed record ReplayPlayerSurvival(uint PlayerStateActorGuid, string? PlayerId, bool IsAlive, float? ReplayTimeSeconds, string Evidence);

/// <summary>Counts are an observed minimum; local replays do not record every distant teammate interaction.</summary>
public sealed record ReplayChestAnalysis(FortniteReplay Replay, IReadOnlyList<ReplayChestOpening> Openings,
    IReadOnlyList<string> Diagnostics, int ChestActorsSeen, int SearchFieldsDecoded, bool HasDecodeErrors)
{
    public IReadOnlyList<ReplayTreasureReward> Rewards { get; init; } = [];
    public bool HasRewardDecodeErrors { get; init; }
    public IReadOnlyList<ReplayTreasureGrant> Grants { get; init; } = [];
    public bool HasGrantDecodeErrors { get; init; }
    public IReadOnlyList<ReplayPlayerDisconnect> Disconnects { get; init; } = [];
    public bool DisconnectMonitoringAvailable { get; init; }
    public IReadOnlyList<ReplayPlayerSurvival> Survival { get; init; } = [];
    public bool SurvivalMonitoringAvailable { get; init; }
}

internal sealed class ReplaySurvivalAccumulator
{
    private sealed class PlayerStatus
    {
        internal bool? Alive;
        internal bool Ended;
        internal bool Disconnected;
        internal uint Reboots;
        internal uint? Pawn;
    }
    private readonly Dictionary<uint, PlayerStatus> players = [];
    private readonly Dictionary<uint, uint> pawnPlayers = [];
    private readonly Dictionary<uint, bool> dyingPawns = [];
    private readonly List<(uint Actor, bool Alive, float? Time, string Evidence)> events = [];
    private bool matchEnded;
    private PlayerStatus Player(uint actor)
    {
        if (!players.TryGetValue(actor, out var player)) players[actor] = player = new();
        return player;
    }
    private void SetAlive(uint actor, bool alive, float? time, string evidence)
    {
        var player = Player(actor);
        if (player.Alive == alive) return;
        player.Alive = alive;
        events.Add((actor, alive, time, evidence));
    }
    internal void UpdatePawn(uint pawn, uint? playerState, bool? dying, float? time)
    {
        if (matchEnded) return;
        if (dying is bool value) dyingPawns[pawn] = value;
        if (playerState is > 0 && !pawnPlayers.ContainsKey(pawn))
        {
            pawnPlayers[pawn] = playerState.Value;
            Player(playerState.Value).Pawn = pawn;
        }
        if (!pawnPlayers.TryGetValue(pawn, out var actor)) return;
        var player = Player(actor);
        // An older corpse must not eliminate a player's newer rebooted pawn.
        if (player.Pawn != pawn || player.Ended || player.Disconnected) return;
        var isDying = dyingPawns.GetValueOrDefault(pawn);
        SetAlive(actor, !isDying, time, isDying ? "Pawn dying" : "Live pawn observed");
    }
    internal void UpdatePlayer(uint actor, bool? knocked, uint? reboots, int? place, bool? disconnected, float? time)
    {
        if (matchEnded) return;
        var player = Player(actor);
        if (disconnected is bool value) player.Disconnected = value;
        if (place == 0) player.Ended = false;
        if (reboots is uint count && count > player.Reboots)
        {
            player.Reboots = count;
            player.Ended = false;
            if (!player.Disconnected) SetAlive(actor, true, time, "Reboot recorded");
        }
        if (knocked == true && !player.Ended && !player.Disconnected)
            SetAlive(actor, true, time, "Knocked, not eliminated");
        // Placement marks the end of this player's participation, including a
        // winner's post-match interval. It is not a team-wide death signal.
        if (place is > 0)
        {
            player.Ended = true;
            SetAlive(actor, false, time, "Participation ended");
        }
        if (player.Disconnected) SetAlive(actor, false, time, "Disconnected");
    }
    internal void EndMatch(float? time)
    {
        if (matchEnded) return;
        matchEnded = true;
        foreach (var actor in players.Keys.ToArray()) SetAlive(actor, false, time, "Match ended");
    }
    internal IReadOnlyList<ReplayPlayerSurvival> Finish(IReadOnlyDictionary<uint, string> identities) =>
        events.Select(e => new ReplayPlayerSurvival(e.Actor, identities.GetValueOrDefault(e.Actor), e.Alive, e.Time, e.Evidence)).ToArray();
}

internal sealed class ReplayDisconnectAccumulator
{
    private readonly Dictionary<uint, bool> states = [];
    private readonly List<(uint Actor, float? Time)> events = [];
    internal void Update(uint actor, bool? disconnected, float? time)
    {
        if (disconnected is not bool value) return;
        if (states.TryGetValue(actor, out var previous) && previous == value) return;
        states[actor] = value;
        // A first true flag is a recorded disconnection, even when an earlier
        // connected flag was omitted. Never infer voluntary quitting or a cause.
        if (value) events.Add((actor, time));
    }
    internal IReadOnlyList<ReplayPlayerDisconnect> Finish(IReadOnlyDictionary<uint, string> players) =>
        events.Select(e => new ReplayPlayerDisconnect(e.Actor, players.GetValueOrDefault(e.Actor), e.Time)).ToArray();
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
