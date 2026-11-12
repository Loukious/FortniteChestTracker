namespace FortniteReplayReader.Editing;

public enum ReplayCosmeticSlot
{
    Character,
    Backpack,
    Pickaxe,
    Glider,
    Contrail,
    LoadingScreen,
    MusicPack,
    Emote,
    Wrap
}

public sealed record ReplayCharacterPart(int Type, string Path);
public sealed record ReplayEmoteAnimation(string MontagePath, int? Gender = null, int? BodyType = null);

/// <summary>A resolved local cosmetic asset and the parts needed to render it.</summary>
public sealed record ReplayCosmeticSelection(
    ReplayCosmeticSlot Slot,
    string AssetId,
    string AssetPath,
    IReadOnlyList<ReplayCharacterPart>? CharacterParts = null,
    string? HeroTypePath = null,
    string? DisplayName = null,
    int Index = 0,
    int? CharacterGender = null,
    int? CharacterBodyType = null,
    string? PickaxeWeaponDefinitionPath = null,
    string? PickaxeWeaponActorClassPath = null,
    IReadOnlyList<ReplayEmoteAnimation>? EmoteAnimations = null);

public sealed record ReplayCosmeticEditResult(
    string Player,
    IReadOnlyList<ReplayCosmeticSelection> Cosmetics,
    int ChangedProperties,
    int ChangedPackets,
    int ChangedCheckpoints,
    int AddedGuidDefinitions);
