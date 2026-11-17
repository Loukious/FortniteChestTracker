using FortniteReplayReader.Analysis;
using Xunit;

namespace FortniteReplayReader.Test;

public class ReplayChestIdentityTest
{
    [Theory]
    [InlineData("Tiered_Chest_Athena_C_UAID_E04F43E65CE8C2F902_5e46178587d4e57a_2001470511")]
    [InlineData("Tiered_Chest_Athena5_6e02a0a859c897db_11")]
    public void VerifiedPlacedInstancesHaveStableIdentity(string path) =>
        Assert.True(ReplayChestAnalysisReader.IsStableChestActorPath(18989, path, false));

    [Theory]
    [InlineData(18988u, "Tiered_Chest_Athena_C_UAID_E04F43E65CE8C2F902_123", false)]
    [InlineData(18989u, "Tiered_Chest_Athena_C_UAID_E04F43E65CE8C2F902_123", true)]
    [InlineData(1u, "Tiered_Chest_Athena_C_UAID_E04F43E65CE8C2F902_123", false)]
    [InlineData(18989u, "Tiered_Chest_Athena_C", false)]
    [InlineData(18989u, "Tiered_Chest_Athena47", false)]
    [InlineData(18989u, "Tiered_Chest_Athena_C_UAID_notAUniqueIdentifier", false)]
    [InlineData(18989u, "Tiered_Chest_Athena_C_UAID_1234", false)]
    [InlineData(18989u, "Tiered_Chest_Athena_C_notAValidHash_11", false)]
    public void DynamicDefaultOrUnverifiedNamesKeepTheirOwnGuid(uint actor, string path, bool hasArchetype) =>
        Assert.False(ReplayChestAnalysisReader.IsStableChestActorPath(actor, path, hasArchetype));
}
