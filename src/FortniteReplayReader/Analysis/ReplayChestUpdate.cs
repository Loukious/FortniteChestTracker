namespace FortniteReplayReader.Analysis;

/// <summary>One fully decoded replicated update; sparse fields remain unspecified.</summary>
internal sealed record ReplayChestUpdate(bool? Searched, uint? SearchingPawn, bool HasPawnUpdate,
    bool? Destroyed, uint? SearchAnimationCount, int SearchFieldsDecoded);
