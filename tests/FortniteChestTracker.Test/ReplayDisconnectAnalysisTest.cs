using FortniteReplayReader.Analysis;
using Unreal.Core.Models;
using Xunit;

namespace FortniteReplayReader.Test;

public class ReplayDisconnectAnalysisTest
{
    [Fact]
    public void FirstTrueFlagIsRecordedAndRepeatedFlagsDoNotDuplicateIt()
    {
        var data = new ReplayDisconnectAccumulator();
        data.Update(100, null, 1);
        data.Update(100, true, 2);
        data.Update(100, true, 3);
        var result = Assert.Single(data.Finish(new Dictionary<uint, string> { [100] = "teammate" }));
        Assert.Equal("teammate", result.PlayerId);
        Assert.Equal(2, result.ReplayTimeSeconds);
    }

    [Fact]
    public void DisconnectAfterReconnectionIsASeparateRecordedEvent()
    {
        var data = new ReplayDisconnectAccumulator();
        data.Update(100, false, 1);
        data.Update(100, true, 10);
        data.Update(100, false, 20);
        data.Update(100, true, 30);
        Assert.Equal([10f, 30f], data.Finish(new Dictionary<uint, string> { [100] = "teammate" }).Select(d => d.ReplayTimeSeconds!.Value));
    }

    [Fact]
    public void LatePlayerIdentityCanResolveAnEarlierEventWithoutInventingTiming()
    {
        var data = new ReplayDisconnectAccumulator();
        data.Update(100, true, null);
        var unknown = Assert.Single(data.Finish(new Dictionary<uint, string>()));
        Assert.Null(unknown.PlayerId);
        var resolved = Assert.Single(data.Finish(new Dictionary<uint, string> { [100] = "owner" }));
        Assert.Equal("owner", resolved.PlayerId);
        Assert.Null(resolved.ReplayTimeSeconds);
    }

    [Fact]
    public void DisconnectionFieldIsRetainedWithRegionWhileUnrelatedFieldsAreSkipped()
    {
        var group = new NetFieldExportGroup { PathName = "/Script/FortniteGame.FortPlayerStateAthena",
            NetFieldExports = [new() { Name = "bIsDisconnected" }, new() { Name = "KeepPlayingTogetherMatchmakingRegion" }, new() { Name = "SoftParts" }] };
        ReplayChestAnalysisReader.KeepReportFields(group);
        Assert.False(group.NetFieldExports[0].Incompatible);
        Assert.False(group.NetFieldExports[1].Incompatible);
        Assert.True(group.NetFieldExports[2].Incompatible);
    }
}
