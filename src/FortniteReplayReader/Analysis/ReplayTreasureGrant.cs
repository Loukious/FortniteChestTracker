namespace FortniteReplayReader.Analysis;

/// <summary>A treasure handler ability received by the owner of an ability system component.</summary>
/// <remarks>This is recipient evidence; it does not identify the source chest or its opener.</remarks>
public sealed record ReplayTreasureGrant(uint AbilitySystemActorGuid, uint PlayerStateActorGuid, string? PlayerId,
    int AbilitySpecId, int GrantSequence, string HandlerClass, float? ReplayTimeSeconds);
