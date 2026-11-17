using System.Reflection;
using System.Text;
using FortniteReplayReader.Analysis;
using FortniteReplayReader.Editing;
using FortniteReplayReader.Models;
using Unreal.Core;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;
using Xunit;

namespace FortniteReplayReader.Test;

public class ReplayTreasureRewardTest
{
    private const string ActorDirectory = "/DelilahTreasureHuntGameplay/Gameplay/Actors/";
    private const string ClassName = "BGA_DelilahTreasureHuntAnimatedChest";
    private const string RewardClass = ActorDirectory + ClassName + "." + ClassName + "_C";
    private const uint SuccessWire = 3, PawnWire = 6, SourceWire = 7, ArrayWire = 9, StringWire = 11;

    [Fact]
    public void ExistingChestAnalysisConstructorKeepsEmptyRewardDefaults()
    {
        var analysis = new ReplayChestAnalysis(new FortniteReplay(), [], [], 0, 0, false);
        Assert.Empty(analysis.Rewards);
        Assert.False(analysis.HasRewardDecodeErrors);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("_Rare", true)]
    [InlineData("_Sprite", true)]
    [InlineData("_Other", false)]
    [InlineData("_ClassNetCache", false)]
    public void OnlyKnownAnimatedRewardClassFamilyIsAccepted(string suffix, bool expected)
    {
        var name = ClassName + suffix;
        var group = Group(ActorDirectory + name + "." + name + "_C");
        Assert.Equal(expected, ReplayTreasureRewardDecoder.IsRewardGroup(group));
        Assert.False(ReplayTreasureRewardDecoder.IsRewardGroup(Group("/DifferentPlugin/" + name + "." + name + "_C")));
        Assert.False(ReplayTreasureRewardDecoder.IsRewardGroup(Group(ActorDirectory + name + ".AnotherClass_C")));
    }

    [Theory]
    [InlineData("AgJk0QIZAAAARGVsaWxhaFByaXplOmRwaWRfaXRlbTE1AAAA", "DelilahPrize:dpid_item15")]
    [InlineData("AgJk0QIZAAAARGVsaWxhaFByaXplOmRwaWRfaXRlbTE3AAAA", "DelilahPrize:dpid_item17")]
    public void OriginalObservedThirtySixBytePrizeFieldsDecodeCompletely(string base64, string expected)
    {
        // Only the public prize field bytes are included; no private replay fixture.
        var group = SparseGroup(RewardClass, (44, "SourceContainer"), (45, "OpeningPawn"),
            (46, "bSuccess"), (48, "PrizeTemplateId"), (49, "PrizeTemplateId"));
        var value = new ReplayBits(Convert.FromBase64String(base64), 288);
        var data = new ReplayTreasureRewardAccumulator();
        Feed(data, 100, Update(Property(47, ReplayBits.UInt(1, 1)), Property(46, ReplayBits.Packed(200)),
            Property(45, ReplayBits.Packed(50)), Property(49, value)), group, time: 7);

        var reward = Assert.Single(data.Finish(new Dictionary<uint, uint> { [200] = 300 },
            new Dictionary<uint, string> { [300] = "teammate-id" }));
        Assert.Equal(expected, reward.PrizeTemplateId);
        Assert.Equal(0, reward.PrizeIndex);
        Assert.Equal(100u, reward.RewardActorGuid);
        Assert.Equal(50u, reward.SourceChestActorGuid);
        Assert.Equal(200u, reward.OpeningPawnGuid);
        Assert.Equal(300u, reward.PlayerStateActorGuid);
        Assert.Equal("teammate-id", reward.PlayerId);
        Assert.Equal(7f, reward.ReplayTimeSeconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReorderedNamedFieldsAndUnalignedUnknownDataDoNotDependOnFixedHandles(bool checksum)
    {
        var body = Update(Property(1, ReplayBits.UInt(0b10111, 5)),
            Property(ArrayWire, Array(1, (0, "FuturePrize:unrecognized"))), Property(SuccessWire, ReplayBits.UInt(1, 1)));
        if (checksum) body = ReplayBits.Join([ReplayBits.UInt(0, 1), body]);
        var data = new ReplayTreasureRewardAccumulator();
        Feed(data, 100, body, checksum: checksum);

        var reward = Assert.Single(Finish(data));
        Assert.Equal("FuturePrize:unrecognized", reward.PrizeTemplateId);
        Assert.Null(reward.PlayerId);
        Assert.Null(reward.OpeningPawnGuid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void FailedOrUnspecifiedSuccessDoesNotProduceAReward(bool? success)
    {
        var values = new List<ReplayBits> { Property(ArrayWire, Array(1, (0, "DelilahPrize:dpid_item15"))) };
        if (success.HasValue) values.Add(Property(SuccessWire, ReplayBits.UInt(success.Value ? 1u : 0u, 1)));
        var data = new ReplayTreasureRewardAccumulator();
        Feed(data, 100, Update(values.ToArray()));
        Assert.Empty(Finish(data));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void SuccessfulRollWithoutANonemptyPrizeDoesNotProduceAnAward(string prize)
    {
        var data = new ReplayTreasureRewardAccumulator();
        Feed(data, 100, Update(Property(SuccessWire, ReplayBits.UInt(1, 1))));
        Assert.Empty(Finish(data));
        Feed(data, 100, Update(Property(ArrayWire, Array(1, (0, prize)))));
        Assert.Empty(Finish(data));
    }

    [Fact]
    public void SplitUpdatesResolveLatePlayerIdentityAndUseFirstSuccessfulTimestamp()
    {
        var data = new ReplayTreasureRewardAccumulator();
        Feed(data, 100, Update(Property(ArrayWire, Array(1, (0, "DelilahPrize:dpid_item17")))), time: 1);
        Feed(data, 100, Update(Property(SuccessWire, ReplayBits.UInt(1, 1))), time: 2);
        Feed(data, 100, Update(Property(PawnWire, ReplayBits.Packed(200)), Property(SourceWire, ReplayBits.Packed(50))), time: 3);
        Feed(data, 100, Update(Property(SuccessWire, ReplayBits.UInt(1, 1))), time: 4);

        var unidentified = Assert.Single(Finish(data));
        Assert.Null(unidentified.PlayerId);
        Assert.Equal(2f, unidentified.ReplayTimeSeconds);
        var identified = Assert.Single(data.Finish(new Dictionary<uint, uint> { [200] = 300 },
            new Dictionary<uint, string> { [300] = "late-player" }));
        Assert.Equal("late-player", identified.PlayerId);
        Assert.Equal(2f, identified.ReplayTimeSeconds);
    }

    [Fact]
    public void DuplicateExportsDeduplicatePerActorAndArrayIndexWhileCleanupKeepsExplicitAttribution()
    {
        var data = new ReplayTreasureRewardAccumulator();
        var body = Update(Property(SuccessWire, ReplayBits.UInt(1, 1)), Property(PawnWire, ReplayBits.Packed(200)),
            Property(SourceWire, ReplayBits.Packed(50)), Property(ArrayWire, Array(2, (0, "same-prize"), (1, "same-prize"))));
        Feed(data, 100, body, time: 1);
        Feed(data, 100, body, time: 2);
        Feed(data, 100, Update(Property(PawnWire, ReplayBits.Packed(0)), Property(SourceWire, ReplayBits.Packed(0))));

        var rewards = Finish(data);
        Assert.Equal(2, rewards.Count);
        Assert.Equal([0, 1], rewards.Select(reward => reward.PrizeIndex));
        Assert.All(rewards, reward =>
        {
            Assert.Equal(200u, reward.OpeningPawnGuid);
            Assert.Equal(50u, reward.SourceChestActorGuid);
            Assert.Equal(1f, reward.ReplayTimeSeconds);
        });
    }

    [Fact]
    public void IdenticalPrizeIdsFromManyDifferentActorsAreAllKept()
    {
        var data = new ReplayTreasureRewardAccumulator();
        var body = Update(Property(SuccessWire, ReplayBits.UInt(1, 1)),
            Property(ArrayWire, Array(1, (0, "DelilahPrize:dpid_item17"))));
        for (uint actor = 1; actor <= 100; actor++) Feed(data, actor, body);
        Assert.Equal(100, Finish(data).Count);
        Assert.Equal(100, Finish(data).Select(reward => reward.RewardActorGuid).Distinct().Count());
    }

    [Fact]
    public void SparseArrayGrowthShrinkAndExplicitEmptyValuesPreserveOnlyCurrentSlots()
    {
        var data = new ReplayTreasureRewardAccumulator();
        Feed(data, 100, Update(Property(SuccessWire, ReplayBits.UInt(1, 1)),
            Property(ArrayWire, Array(3, (0, "first"), (1, "second"), (2, "removed")))));
        Feed(data, 100, Update(Property(ArrayWire, Array(4, (3, "added")))));
        Assert.Equal(["first", "second", "removed", "added"], Finish(data).Select(reward => reward.PrizeTemplateId));
        Feed(data, 100, Update(Property(ArrayWire, Array(2, (1, "replacement")))));
        Assert.Equal(["first", "replacement"], Finish(data).Select(reward => reward.PrizeTemplateId));
        Feed(data, 100, Update(Property(ArrayWire, Array(2, (0, "")))));
        var last = Assert.Single(Finish(data));
        Assert.Equal(1, last.PrizeIndex);
        Assert.Equal("replacement", last.PrizeTemplateId);
        Feed(data, 100, Update(Property(ArrayWire, Array(0))));
        Assert.Empty(Finish(data));
    }

    [Theory]
    [InlineData("truncated-outer")]
    [InlineData("invalid-outer-handle")]
    [InlineData("missing-outer-terminator")]
    [InlineData("boolean-extra-bit")]
    [InlineData("truncated-reference")]
    [InlineData("huge-array-length")]
    [InlineData("out-of-range-index")]
    [InlineData("wrong-inner-field-name")]
    [InlineData("invalid-inner-handle")]
    [InlineData("truncated-inner-payload")]
    [InlineData("missing-string-null")]
    [InlineData("impossible-string-length")]
    [InlineData("missing-inner-terminator")]
    [InlineData("missing-array-terminator")]
    [InlineData("duplicate-element-index")]
    public void MalformedRewardDataIsRejectedWithoutApplyingPartialState(string kind)
    {
        var malformed = Malformed(kind);
        var data = new ReplayTreasureRewardAccumulator();
        Feed(data, 100, Update(Property(SuccessWire, ReplayBits.UInt(1, 1)),
            Property(ArrayWire, Array(1, (0, "prior-valid-prize")))));
        // A failure flag preceding corrupt data must not erase an already decoded reward.
        var values = ReplayBits.Join([Property(SuccessWire, ReplayBits.UInt(0, 1)), malformed]);
        using var reader = new NetBitReader(values.Bytes, values.Length);
        Assert.False(data.ReadUpdate(100, Group(), reader, false, 10));
        Assert.Equal("prior-valid-prize", Assert.Single(Finish(data)).PrizeTemplateId);
    }

    [Fact]
    public void MalformedRewardUpdatesDoNotTaintChestCountsOrChestCoverageFlag()
    {
        using var container = EmptyReplay();
        var reader = new ReplayChestAnalysisReader(container);
        SetChannel(reader, 1, 100);
        using var invalid = new NetBitReader(new byte[] { 2 }, 8);
        reader.ReceiveProperties(invalid, Group(), 1, out _, false);
        var chest = SparseGroup("Tiered_Chest_Athena_C", (0, "bAlreadySearched"), (1, "SearchingPawn"));
        var body = Update(Property(1, ReplayBits.UInt(1, 1)), Property(2, ReplayBits.Packed(200)));
        using var valid = new NetBitReader(body.Bytes, body.Length);
        reader.ReceiveProperties(valid, chest, 1, out _, false);

        var analysis = reader.Scan();
        Assert.True(analysis.HasRewardDecodeErrors);
        Assert.False(analysis.HasDecodeErrors);
        Assert.Single(analysis.Openings);
        Assert.Equal(2, analysis.SearchFieldsDecoded);
        Assert.Empty(analysis.Rewards);
        Assert.Contains(analysis.Diagnostics, line => line.Contains("treasure reward updates could not be decoded"));
    }

    [Fact]
    public void MalformedChestUpdatesDoNotTaintValidRewardResults()
    {
        using var container = EmptyReplay();
        var reader = new ReplayChestAnalysisReader(container);
        SetChannel(reader, 1, 100);
        var body = Update(Property(SuccessWire, ReplayBits.UInt(1, 1)),
            Property(ArrayWire, Array(1, (0, "DelilahPrize:dpid_item15"))));
        using var valid = new NetBitReader(body.Bytes, body.Length);
        reader.ReceiveProperties(valid, Group(), 1, out _, false);
        using var invalid = new NetBitReader(new byte[] { 2 }, 8);
        reader.ReceiveProperties(invalid, SparseGroup("Tiered_Chest_Athena_C", (0, "bAlreadySearched")), 1, out _, false);

        var analysis = reader.Scan();
        Assert.True(analysis.HasDecodeErrors);
        Assert.False(analysis.HasRewardDecodeErrors);
        Assert.Equal("DelilahPrize:dpid_item15", Assert.Single(analysis.Rewards).PrizeTemplateId);
    }

    [Fact]
    public void Utf16PrizeIdsArePreservedWithoutAssumingKnownCatalogNames()
    {
        const string prize = "FuturePrize:é";
        var bytes = Encoding.Unicode.GetBytes(prize + '\0');
        var text = ReplayBits.Join([ReplayBits.UInt(unchecked((uint)-(prize.Length + 1)), 32), new ReplayBits(bytes, bytes.Length * 8)]);
        var array = ReplayBits.Join([ReplayBits.Packed(1), ReplayBits.Packed(1), Property(StringWire, text), ReplayBits.Packed(0), ReplayBits.Packed(0)]);
        var data = new ReplayTreasureRewardAccumulator();
        Feed(data, 100, Update(Property(SuccessWire, ReplayBits.UInt(1, 1)), Property(ArrayWire, array)));
        Assert.Equal(prize, Assert.Single(Finish(data)).PrizeTemplateId);
    }

    private static ReplayBits Malformed(string kind)
    {
        var validArray = Array(1, (0, "prize"));
        var prefix = ReplayBits.Join([ReplayBits.Packed(1), ReplayBits.Packed(1)]);
        var element = Property(StringWire, String("prize"));
        return kind switch
        {
            "truncated-outer" => ReplayBits.Join([ReplayBits.Packed(ArrayWire), ReplayBits.Packed(100), ReplayBits.UInt(0, 1)]),
            "invalid-outer-handle" => Update(Property(200, ReplayBits.UInt(0, 1))),
            "missing-outer-terminator" => Property(ArrayWire, validArray),
            "boolean-extra-bit" => Update(Property(SuccessWire, ReplayBits.UInt(1, 2))),
            "truncated-reference" => Update(Property(PawnWire, ReplayBits.UInt(1, 7))),
            "huge-array-length" => Update(Property(ArrayWire, ReplayBits.Join([ReplayBits.Packed(uint.MaxValue), ReplayBits.Packed(0)]))),
            "out-of-range-index" => Update(Property(ArrayWire, ReplayBits.Join([ReplayBits.Packed(1), ReplayBits.Packed(2), element, ReplayBits.Packed(0), ReplayBits.Packed(0)]))),
            "wrong-inner-field-name" => Update(Property(ArrayWire, ReplayBits.Join([prefix, Property(1, String("prize")), ReplayBits.Packed(0), ReplayBits.Packed(0)]))),
            "invalid-inner-handle" => Update(Property(ArrayWire, ReplayBits.Join([prefix, Property(100, String("prize")), ReplayBits.Packed(0), ReplayBits.Packed(0)]))),
            "truncated-inner-payload" => Update(Property(ArrayWire, ReplayBits.Join([prefix, ReplayBits.Packed(StringWire), ReplayBits.Packed(1000), ReplayBits.UInt(0, 1)]))),
            "missing-string-null" => Update(Property(ArrayWire, ReplayBits.Join([prefix, Property(StringWire, ReplayBits.Join([ReplayBits.UInt(1, 32), ReplayBits.UInt(65, 8)])), ReplayBits.Packed(0), ReplayBits.Packed(0)]))),
            "impossible-string-length" => Update(Property(ArrayWire, ReplayBits.Join([prefix, Property(StringWire, ReplayBits.UInt(0x80000000, 32)), ReplayBits.Packed(0), ReplayBits.Packed(0)]))),
            "missing-inner-terminator" => Update(Property(ArrayWire, ReplayBits.Join([prefix, element]))),
            "missing-array-terminator" => Update(Property(ArrayWire, validArray.Slice(0, validArray.Length - 8))),
            "duplicate-element-index" => Update(Property(ArrayWire, Array(1, (0, "one"), (0, "two")))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    private static IReadOnlyList<ReplayTreasureReward> Finish(ReplayTreasureRewardAccumulator data) =>
        data.Finish(new Dictionary<uint, uint>(), new Dictionary<uint, string>());

    private static void Feed(ReplayTreasureRewardAccumulator data, uint actor, ReplayBits bits,
        NetFieldExportGroup? group = null, bool checksum = false, float? time = null)
    {
        using var reader = new NetBitReader(bits.Bytes, bits.Length);
        Assert.True(data.ReadUpdate(actor, group ?? Group(), reader, checksum, time));
        Assert.True(reader.AtEnd());
        Assert.False(reader.IsError);
    }

    private static ReplayBits Update(params ReplayBits[] properties) => ReplayBits.Join([.. properties, ReplayBits.Packed(0)]);
    private static ReplayBits Property(uint wireHandle, ReplayBits value) =>
        ReplayBits.Join([ReplayBits.Packed(wireHandle), ReplayBits.Packed((uint)value.Length), value]);
    private static ReplayBits Array(int length, params (int Index, string Value)[] values) =>
        ReplayBits.Join([ReplayBits.Packed((uint)length), .. values.Select(value => ReplayBits.Join([
            ReplayBits.Packed((uint)value.Index + 1), Property(StringWire, String(value.Value)), ReplayBits.Packed(0)])), ReplayBits.Packed(0)]);
    private static ReplayBits String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + '\0');
        return ReplayBits.Join([ReplayBits.UInt((uint)bytes.Length, 32), new ReplayBits(bytes, bytes.Length * 8)]);
    }

    private static NetFieldExportGroup Group(string path = RewardClass) => SparseGroup(path,
        (0, "Health"), (2, "bSuccess"), (5, "OpeningPawn"), (6, "SourceContainer"), (8, "PrizeTemplateId"), (10, "PrizeTemplateId"));
    private static NetFieldExportGroup SparseGroup(string path, params (int Handle, string Name)[] fields)
    {
        var exports = new NetFieldExport?[fields.Max(field => field.Handle) + 1];
        foreach (var field in fields) exports[field.Handle] = new() { Handle = (uint)field.Handle, Name = field.Name };
        return new() { PathName = path, NetFieldExportsLength = (uint)exports.Length, NetFieldExports = exports };
    }

    private static void SetChannel(ReplayChestAnalysisReader reader, uint index, uint actor)
    {
        var channels = (UChannel?[])typeof(Unreal.Core.ReplayReader<FortniteReplay>)
            .GetField("Channels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        channels[index] = new() { Actor = new() { ActorNetGUID = new() { Value = actor } } };
    }

    internal static ReplayContainer EmptyReplay()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        void Text(BinaryWriter target, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            target.Write(bytes.Length + 1); target.Write(bytes); target.Write((byte)0);
        }
        writer.Write(0x1CA2E27Fu); writer.Write(7); writer.Write(0);
        writer.Write(0u); writer.Write(0u); writer.Write(59594394u); Text(writer, "Synthetic empty replay");
        writer.Write(0); writer.Write(new DateTime(2026, 11, 1).ToBinary()); writer.Write(0); writer.Write(0); writer.Write(0);
        using var header = new MemoryStream();
        using (var h = new BinaryWriter(header, Encoding.UTF8, true))
        {
            h.Write(0x2CF5A13Du); h.Write(19); h.Write(0); h.Write(0u); h.Write(45u); h.Write(0u);
            h.Write(new byte[16]); h.Write((ushort)6); h.Write((ushort)0); h.Write((ushort)0);
            h.Write(59594394u); Text(h, "++Fortnite+Release-43.00");
            h.Write(0u); h.Write(0u); h.Write(0u); h.Write(0); h.Write(0xABu); h.Write(0);
            h.Write(0f); h.Write(0f); h.Write(0f); h.Write(0f); Text(h, "Windows"); h.Write((byte)0); h.Write((byte)0);
        }
        writer.Write((uint)ReplayChunkType.Header); writer.Write((int)header.Length); writer.Write(header.ToArray());
        return ReplayContainer.ReadForAnalysis(stream.ToArray());
    }
}
