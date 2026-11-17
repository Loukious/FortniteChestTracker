namespace FortniteReplayReader.Analysis;

internal sealed class ReplayTreasureGrantAccumulator
{
    private sealed class SpecState(int sequence)
    {
        internal int Sequence { get; } = sequence;
        internal uint? AbilityGuid { get; set; }
        internal HashSet<uint> ExportedAbilityGuids { get; } = [];
    }
    private sealed class ComponentState
    {
        internal Dictionary<int, SpecState> Specs { get; } = [];
        internal Dictionary<int, int> Sequences { get; } = [];
        internal HashSet<int> AppliedReplicationKeys { get; } = [];
    }
    private sealed record Receipt(uint Component, uint RecipientActor, int Spec, int Sequence, uint AbilityGuid, float? Time);

    private readonly Dictionary<(uint Component, uint Recipient), ComponentState> components = [];
    private readonly List<Receipt> receipts = [];
    internal int UnattributedGrants { get; private set; }

    internal void Apply(uint component, uint recipientActor, ReplayAbilityArrayUpdate update, float? time)
    {
        var scope = (component, recipientActor);
        if (!components.TryGetValue(scope, out var state)) components[scope] = state = new();
        // A repeated delta may include delete/re-add operations. Applying the same
        // replication key twice must not manufacture another receipt sequence.
        if (!state.AppliedReplicationKeys.Add(update.ReplicationKey)) return;
        foreach (var id in update.DeletedIds) state.Specs.Remove(id);
        foreach (var changed in update.ChangedSpecs)
        {
            if (!state.Specs.TryGetValue(changed.Id, out var spec))
            {
                var sequence = state.Sequences.GetValueOrDefault(changed.Id) + 1;
                state.Sequences[changed.Id] = sequence;
                state.Specs[changed.Id] = spec = new(sequence);
            }
            if (changed.AbilityGuid is not uint ability) continue;
            spec.AbilityGuid = ability;
            if (ability == 0 || !spec.ExportedAbilityGuids.Add(ability)) continue;
            // A provisional reference can be a nonhandler ability. Preserve
            // later distinct references until exported object names establish
            // whether this spec sequence actually contains a treasure handler.
            receipts.Add(new(component, recipientActor, changed.Id, spec.Sequence, ability, time));
        }
    }

    internal IReadOnlyList<ReplayTreasureGrant> Finish(IReadOnlyDictionary<uint, uint> pawnStates,
        IReadOnlySet<uint> knownPlayerStates, IReadOnlyDictionary<uint, string> playerIds,
        IReadOnlyDictionary<uint, string> abilityPaths)
    {
        var result = new List<ReplayTreasureGrant>();
        var confirmedSpecs = new HashSet<(uint Component, uint Recipient, int Spec, int Sequence)>();
        UnattributedGrants = 0;
        foreach (var receipt in receipts)
        {
            if (!abilityPaths.TryGetValue(receipt.AbilityGuid, out var path) ||
                !ReplayTreasureGrantDecoder.TryGetHandlerClass(path, out var handler)) continue;
            var playerState = knownPlayerStates.Contains(receipt.RecipientActor) ? receipt.RecipientActor :
                pawnStates.GetValueOrDefault(receipt.RecipientActor);
            if (playerState == 0 || !knownPlayerStates.Contains(playerState))
            {
                UnattributedGrants++;
                continue;
            }
            if (!confirmedSpecs.Add((receipt.Component, playerState, receipt.Spec, receipt.Sequence))) continue;
            playerIds.TryGetValue(playerState, out var playerId);
            result.Add(new(receipt.Component, playerState, playerId, receipt.Spec, receipt.Sequence, handler, receipt.Time));
        }
        return result;
    }
}
