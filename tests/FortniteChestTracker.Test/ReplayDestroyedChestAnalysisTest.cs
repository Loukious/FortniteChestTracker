using FortniteReplayReader.Analysis;
using FortniteReplayReader.Editing;
using Unreal.Core;
using Unreal.Core.Models;
using Xunit;

namespace FortniteReplayReader.Test;

public class ReplayDestroyedChestAnalysisTest
{
    private const string ChestClass = "/Game/Building/ActorBlueprints/Containers/Tiered_Chest_Athena.Tiered_Chest_Athena_C";
    private const uint DestroyedWire = 29, SearchedWire = 78, AnimationWire = 80, PawnWire = 81;

    [Fact]
    public void ExistingOpeningConstructorDefaultsToNormalSearch()
    {
        var opening = new ReplayChestOpening(100, ChestClass, 200, 300, "owner", 1);
        Assert.False(opening.IsDestruction);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ObservedDestructionCompletionDecodesByNameAndResolvesLateIdentity(bool checksum)
    {
        var data = new ReplayChestAccumulator();
        // This is the terminal field shape observed on the source of the owner's
        // destruction-origin reward: flags, counter=1, and explicit pawn=28756.
        Feed(data, 15505, Update(Property(PawnWire, ReplayBits.Packed(28756)),
            Property(AnimationWire, ReplayBits.UInt(1, 32)), Property(32, ReplayBits.UInt(1, 1)),
            Property(DestroyedWire, ReplayBits.UInt(1, 1))), 398.2416f, checksum);

        var unresolved = Assert.Single(Finish(data));
        Assert.True(unresolved.IsDestruction);
        Assert.Equal(28756u, unresolved.SearchingPawnGuid);
        Assert.Null(unresolved.PlayerId);
        var resolved = Assert.Single(data.Finish(new Dictionary<uint, uint> { [28756] = 1256 },
            new Dictionary<uint, string> { [1256] = "owner-id" }));
        Assert.Equal("owner-id", resolved.PlayerId);
        Assert.Equal(398.2416f, resolved.ReplayTimeSeconds);
    }

    [Fact]
    public void SamePlayerStartedInteractionCanCompleteDestructionWithFreshTerminalFields()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, false, 200, true, 1);
        CompleteDestruction(data, 100, 200, 2);

        Assert.Equal(200u, Assert.Single(Finish(data)).SearchingPawnGuid);
    }

    [Fact]
    public void CancelledOrDifferentEarlierSearchCannotReplaceTheExplicitTerminalPawn()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, false, 200, true, 1);
        data.Update(100, ChestClass, null, 0, true, 2);
        CompleteDestruction(data, 100, 200, 3);
        data.Update(101, ChestClass, false, 201, true, 1);
        CompleteDestruction(data, 101, 202, 3);

        var openings = Finish(data);
        Assert.Equal(200u, Assert.Single(openings, opening => opening.ChestActorGuid == 100).SearchingPawnGuid);
        Assert.Equal(202u, Assert.Single(openings, opening => opening.ChestActorGuid == 101).SearchingPawnGuid);
    }

    [Fact]
    public void DestructionCannotUseAnOldActivePawnOrAPawnThatArrivesLater()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, false, 200, true, 1);
        data.Update(100, ChestClass, new(null, null, false, true, 1, 0), 2);
        data.Update(100, ChestClass, null, 201, true, 3);

        Assert.Empty(Finish(data));
        Assert.Equal(1, data.UnsupportedDestroyedActors);
    }

    [Theory]
    [InlineData(null, 1u, 200u, true)]
    [InlineData(false, 1u, 200u, true)]
    [InlineData(true, null, 200u, true)]
    [InlineData(true, 0u, 200u, true)]
    [InlineData(true, 1u, null, false)]
    [InlineData(true, 1u, 0u, true)]
    [InlineData(true, 1u, 200u, false)]
    public void EveryDestructionCompletionFieldMustBeRecordedTogether(bool? destroyed, uint? count,
        uint? pawn, bool hasPawn)
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, new(null, pawn, hasPawn, destroyed, count, 0), 1);
        Assert.Empty(Finish(data));
    }

    [Theory]
    [InlineData(1u, 1u)]
    [InlineData(2u, 1u)]
    [InlineData(3u, 2u)]
    public void ARepeatedOrOlderAnimationCounterCannotCompleteDestruction(uint previous, uint terminal)
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, new(null, null, false, null, previous, 0), 1);
        data.Update(100, ChestClass, new(null, 200, true, true, terminal, 1), 2);
        Assert.Empty(Finish(data));
    }

    [Fact]
    public void ANewlyAdvancedCounterIsFreshEvenForTheSamePawn()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, new(null, 200, true, null, 1, 1), 1);
        data.Update(100, ChestClass, new(null, 200, true, true, 2, 1), 2);
        Assert.True(Assert.Single(Finish(data)).IsDestruction);
    }

    [Fact]
    public void DestructionAndRepeatedExportsNeverDuplicateOrReplaceAnEarlierNormalOpening()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, true, 200, true, 1);
        CompleteDestruction(data, 100, 201, 2);
        CompleteDestruction(data, 100, 202, 3);
        CompleteDestruction(data, 101, 200, 4);
        CompleteDestruction(data, 101, 201, 5);

        var openings = Finish(data);
        Assert.Equal(2, openings.Count);
        var normal = Assert.Single(openings, opening => opening.ChestActorGuid == 100);
        Assert.False(normal.IsDestruction);
        Assert.Equal(200u, normal.SearchingPawnGuid);
        Assert.Equal(1f, normal.ReplayTimeSeconds);
        var destruction = Assert.Single(openings, opening => opening.ChestActorGuid == 101);
        Assert.True(destruction.IsDestruction);
        Assert.Equal(200u, destruction.SearchingPawnGuid);
        Assert.Equal(4f, destruction.ReplayTimeSeconds);
    }

    [Fact]
    public void ASubsequentExplicitNormalSearchTakesPriorityWithoutAddingAnotherOpening()
    {
        var data = new ReplayChestAccumulator();
        CompleteDestruction(data, 100, 200, 1);
        data.Update(100, ChestClass, true, 201, true, 2);
        CompleteDestruction(data, 100, 202, 3);

        var opening = Assert.Single(Finish(data));
        Assert.False(opening.IsDestruction);
        Assert.Equal(201u, opening.SearchingPawnGuid);
        Assert.Equal(1f, opening.ReplayTimeSeconds);
    }

    [Fact]
    public void ASubsequentNormalFlagCanInheritTheRecordedCompletionIdentityAfterCleanup()
    {
        var data = new ReplayChestAccumulator();
        CompleteDestruction(data, 100, 200, 1);
        data.Update(100, ChestClass, null, 0, true, 2);
        data.Update(100, ChestClass, true, null, false, 3);

        var opening = Assert.Single(Finish(data));
        Assert.False(opening.IsDestruction);
        Assert.Equal(200u, opening.SearchingPawnGuid);
    }

    [Fact]
    public void DelayedNormalFlagCannotReplaceADestructionIdentityWithAnUnrelatedCachedPawn()
    {
        var data = new ReplayChestAccumulator();
        CompleteDestruction(data, 100, 200, 1);
        data.Update(100, ChestClass, null, 201, true, 2);
        data.Update(100, ChestClass, true, null, false, 3);

        var opening = Assert.Single(Finish(data));
        Assert.False(opening.IsDestruction);
        Assert.Equal(200u, opening.SearchingPawnGuid);
        Assert.Equal(1f, opening.ReplayTimeSeconds);
    }

    [Fact]
    public void SimultaneousNormalSearchAndDestructionCountsTheNormalSearchOnce()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, new(true, 200, true, true, 1, 2), 1);
        Assert.False(Assert.Single(Finish(data)).IsDestruction);
    }

    [Fact]
    public void ReopenedThenDestroyedChestCountsOneCompletionPerExplicitResetCycle()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, new(true, 200, true, null, 1, 2), 1);
        CompleteDestruction(data, 100, 201, 2);
        data.Update(100, ChestClass, new(false, null, false, null, 0, 1), 3);
        CompleteDestruction(data, 100, 202, 4);
        CompleteDestruction(data, 100, 203, 5);

        Assert.Collection(Finish(data),
            first =>
            {
                Assert.False(first.IsDestruction);
                Assert.Equal(0, first.ResetCycleIndex);
                Assert.Equal(200u, first.SearchingPawnGuid);
                Assert.Equal(1f, first.ReplayTimeSeconds);
            },
            second =>
            {
                Assert.True(second.IsDestruction);
                Assert.Equal(1, second.ResetCycleIndex);
                Assert.Equal(202u, second.SearchingPawnGuid);
                Assert.Equal(4f, second.ReplayTimeSeconds);
            });
        Assert.Equal(1, data.ChestActorsSeen);
        Assert.Equal(0, data.UnsupportedDestroyedActors);
    }

    [Fact]
    public void ResetAfterDestructionClearsCompletionAndOpenerForTheNextCycle()
    {
        var data = new ReplayChestAccumulator();
        CompleteDestruction(data, 100, 200, 1);
        data.Update(100, ChestClass, new(false, null, false, null, 0, 1), 2);
        data.Update(100, ChestClass, new(true, null, false, null, 1, 1), 3);

        var openings = Finish(data);
        Assert.Equal(2, openings.Count);
        Assert.True(openings[0].IsDestruction);
        Assert.Equal(200u, openings[0].SearchingPawnGuid);
        Assert.False(openings[1].IsDestruction);
        Assert.Null(openings[1].SearchingPawnGuid);
        Assert.Equal(1, openings[1].ResetCycleIndex);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResetFieldsDecodeTogetherBeforeASecondCompletion(bool checksum)
    {
        var data = new ReplayChestAccumulator();
        CompleteDestruction(data, 100, 200, 1);
        Feed(data, 100, Update(Property(SearchedWire, ReplayBits.UInt(0, 1)),
            Property(AnimationWire, ReplayBits.UInt(0, 32))), 2, checksum);
        Feed(data, 100, Update(Property(SearchedWire, ReplayBits.UInt(1, 1)),
            Property(AnimationWire, ReplayBits.UInt(1, 32))), 3, checksum);

        var openings = Finish(data);
        Assert.Equal(2, openings.Count);
        Assert.Equal(1, openings[1].ResetCycleIndex);
        Assert.Null(openings[1].SearchingPawnGuid);
    }

    [Fact]
    public void MalformedResetUpdateCannotApplyDecodedFalseAndZeroFields()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, new(true, 200, true, null, 1, 2), 1);
        var body = Update(Property(SearchedWire, ReplayBits.UInt(0, 1)),
            Property(AnimationWire, ReplayBits.UInt(0, 32)), Property(PawnWire, ReplayBits.UInt(1, 7)));
        using var reader = new NetBitReader(body.Bytes, body.Length);

        Assert.False(ReplayChestAnalysisReader.TryReadChestUpdate(reader, Group(), false, out var update));
        Assert.Null(update.Searched);
        Assert.Null(update.SearchAnimationCount);
        data.Update(100, ChestClass, new(true, 201, true, null, 1, 2), 3);

        var opening = Assert.Single(Finish(data));
        Assert.Equal(0, opening.ResetCycleIndex);
        Assert.Equal(200u, opening.SearchingPawnGuid);
    }

    [Theory]
    [InlineData(DestroyedWire, 2)]
    [InlineData(AnimationWire, 31)]
    [InlineData(AnimationWire, 33)]
    [InlineData(PawnWire, 7)]
    public void MalformedDestructionUpdatesAreAtomicAndCannotApplyPartialCompletion(uint malformedHandle, int bits)
    {
        var malformed = bits > 32 ? ReplayBits.Join([ReplayBits.UInt(1, 32), ReplayBits.UInt(0, bits - 32)]) : ReplayBits.UInt(1, bits);
        var body = Update(Property(SearchedWire, ReplayBits.UInt(1, 1)),
            Property(PawnWire, ReplayBits.Packed(200)), Property(DestroyedWire, ReplayBits.UInt(1, 1)),
            Property(AnimationWire, ReplayBits.UInt(1, 32)), Property(malformedHandle, malformed));
        using var reader = new NetBitReader(body.Bytes, body.Length);

        Assert.False(ReplayChestAnalysisReader.TryReadChestUpdate(reader, Group(), false, out var update));
        Assert.Null(update.Searched);
        Assert.Null(update.Destroyed);
        Assert.Null(update.SearchingPawn);
        Assert.Null(update.SearchAnimationCount);
        Assert.Equal(0, update.SearchFieldsDecoded);
        Assert.False(update.HasPawnUpdate);
    }

    [Fact]
    public void DestructionStatusWithoutSearchInteractionDoesNotMakeSearchCoverageAvailable()
    {
        var body = Update(Property(DestroyedWire, ReplayBits.UInt(1, 1)), Property(AnimationWire, ReplayBits.UInt(1, 32)));
        using var reader = new NetBitReader(body.Bytes, body.Length);
        Assert.True(ReplayChestAnalysisReader.TryReadChestUpdate(reader, Group(), false,
            out _, out _, out var hasPawn, out var fields));
        Assert.False(hasPawn);
        Assert.Equal(0, fields);
    }

    private static IReadOnlyList<ReplayChestOpening> Finish(ReplayChestAccumulator data) =>
        data.Finish(new Dictionary<uint, uint>(), new Dictionary<uint, string>());
    private static void CompleteDestruction(ReplayChestAccumulator data, uint actor, uint pawn, float time) =>
        data.Update(actor, ChestClass, new(null, pawn, true, true, 1, 1), time);
    private static void Feed(ReplayChestAccumulator data, uint actor, ReplayBits body, float time, bool checksum)
    {
        if (checksum) body = ReplayBits.Join([ReplayBits.UInt(0, 1), body]);
        using var reader = new NetBitReader(body.Bytes, body.Length);
        Assert.True(ReplayChestAnalysisReader.TryReadChestUpdate(reader, Group(), checksum, out var update));
        Assert.True(reader.AtEnd());
        Assert.Equal(1, update.SearchFieldsDecoded);
        data.Update(actor, ChestClass, update, time);
    }
    private static ReplayBits Update(params ReplayBits[] properties) => ReplayBits.Join([.. properties, ReplayBits.Packed(0)]);
    private static ReplayBits Property(uint handle, ReplayBits value) => ReplayBits.Join([ReplayBits.Packed(handle), ReplayBits.Packed((uint)value.Length), value]);
    private static NetFieldExportGroup Group()
    {
        var fields = new NetFieldExport?[82];
        foreach (var (handle, name) in new[] { (28, "bDestroyed"), (31, "bInstantDeath"), (77, "bAlreadySearched"), (79, "SearchAnimationCount"), (80, "SearchingPawn") })
            fields[handle] = new() { Handle = (uint)handle, Name = name };
        return new() { PathName = ChestClass, NetFieldExportsLength = (uint)fields.Length, NetFieldExports = fields };
    }
}
