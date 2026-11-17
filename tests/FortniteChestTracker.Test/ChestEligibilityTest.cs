using FortniteChestTracker.Core;
using Xunit;

namespace FortniteChestTracker.Core.Test;

public class ChestEligibilityTest
{
    [Theory]
    [InlineData("Playlist_DefaultSolo")]
    [InlineData("Playlist_DefaultDuo")]
    [InlineData("Playlist_DefaultTrio")]
    [InlineData("Playlist_Trios")]
    [InlineData("/Game/Athena/Playlists/Playlist_Trios.Playlist_Trios")]
    [InlineData("Playlist_DefaultSquad")]
    [InlineData("playlist_defaultsquad")]
    [InlineData("/Game/Athena/Playlists/Playlist_DefaultSquad.Playlist_DefaultSquad")]
    public void StandardPublicBrBuildPlaylistsAreEligible(string playlist)
    {
        Assert.Equal("Battle Royale · Build", ChestEligibility.GetMode(playlist));
    }

    [Theory]
    [InlineData("Playlist_NoBuildBR_Solo")]
    [InlineData("Playlist_NoBuildBR_Duo")]
    [InlineData("Playlist_NoBuildBR_Trio")]
    [InlineData("Playlist_NoBuildBR_Squad")]
    [InlineData("/Game/Athena/Playlists/NoBuild/Playlist_NoBuildBR_Squad.Playlist_NoBuildBR_Squad")]
    public void StandardZeroBuildPlaylistsAreEligible(string playlist)
    {
        Assert.Equal("Battle Royale · Zero Build", ChestEligibility.GetMode(playlist));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Playlist_VK_Play")]
    [InlineData("Playlist_Creative")]
    [InlineData("Playlist_PiperBootSquads")]
    [InlineData("Playlist_RE_SourSpawnSquad")]
    [InlineData("Playlist_BlastBerrySquad")]
    [InlineData("Playlist_OGSquads")]
    [InlineData("Playlist_NoBuildBR_Squad_Ranked")]
    [InlineData("Playlist_ShowdownTournament_NoBuildBR_Squad")]
    [InlineData("Playlist_DefaultSquad_Copy")]
    public void UnsupportedOrUnknownModesAreSkipped(string? playlist)
    {
        Assert.Null(ChestEligibility.GetMode(playlist));
    }

    [Fact]
    public void TournamentAndLargeTeamMetadataOverrideOtherwiseEligiblePlaylist()
    {
        Assert.Null(ChestEligibility.GetMode("Playlist_DefaultSquad", isTournament: true));
        Assert.Null(ChestEligibility.GetMode("Playlist_NoBuildBR_Squad", isLargeTeam: true));
    }

    [Theory]
    [InlineData("SubGame=Campaign")]
    [InlineData("VerseURI=/Fortnite.com/GameFeatures/CreativeRoot")]
    [InlineData("VerseURI=/Fortnite.com/GameFeatures/VKPlayMinimumRoot+/fortnite.com/GameFeatures/FN1GFS")]
    public void CreativeOrOtherSubgameHeaderExcludesEvenAthenaRecordings(string header)
    {
        Assert.True(ChestEligibility.IsOtherSubgame(["SubGame=Athena", header]));
    }

    [Fact]
    public void StandardBrHeaderStillRequiresPlaylistEligibility()
    {
        Assert.False(ChestEligibility.IsOtherSubgame(["SubGame=Athena", "VerseURI=/Fortnite.com/GameFeatures/BRRoot"]));
        Assert.False(ChestEligibility.IsOtherSubgame(null));
    }

    [Fact]
    public void UnspecifiedGameStartIsInterpretedAsUtcAndConvertedToLocalDate()
    {
        var gameStart = new DateTime(2026, 10, 31, 23, 30, 0, DateTimeKind.Unspecified);
        var outerTimestamp = new DateTime(2026, 11, 1, 5, 0, 0);
        var actual = ChestEligibility.LocalMatchDate(gameStart, outerTimestamp);
        var expected = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(gameStart, DateTimeKind.Utc), TimeZoneInfo.Local);

        Assert.Equal(expected, actual);
        Assert.Equal(DateOnly.FromDateTime(expected), DateOnly.FromDateTime(actual));
    }

    [Fact]
    public void OuterTimestampAlreadyRepresentsLocalTimeWhenGameStartIsAbsent()
    {
        var localTimestamp = new DateTime(2026, 11, 1, 0, 15, 0, DateTimeKind.Unspecified);
        Assert.Equal(localTimestamp, ChestEligibility.LocalMatchDate(null, localTimestamp));
    }

    [Fact]
    public void ExplicitUtcOuterTimestampIsConvertedWhenGameStartIsAbsent()
    {
        var utcTimestamp = new DateTime(2026, 11, 1, 0, 15, 0, DateTimeKind.Utc);
        Assert.Equal(utcTimestamp.ToLocalTime(), ChestEligibility.LocalMatchDate(null, utcTimestamp));
    }
}
