namespace FortniteChestTracker.Core;

/// <summary>Reward categories from the installed event prize table, not cosmetic ownership.</summary>
internal static class ChestRewardCatalog
{
    private static readonly Dictionary<string, (string Category, string Tier)> Prizes = Build();

    internal static (string Category, string Tier) Get(string prizeTemplateId) =>
        Prizes.GetValueOrDefault(prizeTemplateId, ("Treasure (unrecognized reward)", "Unknown"));

    private static Dictionary<string, (string Category, string Tier)> Build()
    {
        var prizes = new Dictionary<string, (string Category, string Tier)>(StringComparer.OrdinalIgnoreCase);
        Add("Gold outfit", "Supreme", [1, 14, 19, 24, 29]);
        Add("Pickaxe", "Major", [2]);
        Add("Back bling", "Major", [15, 20, 25, 30]);
        Add("Jam track", "Major", [3, 16, 21, 26, 31]);
        Add("Loading screen", "Major", [4, 17, 22, 27, 32]);
        Add("Gold car decal", "Major", [5, 18, 23, 28, 33]);
        Add("Gizmo", "Standard", [9, 10, 11, 12, 13]);
        prizes.Add("9f73f94d-2e61-4523-97d7-d61235d3365e", ("Pirate Shop Access", "Supreme"));
        return prizes;

        void Add(string category, string tier, int[] items)
        {
            foreach (var item in items) prizes.Add($"DelilahPrize:dpid_item{item}", (category, tier));
        }
    }
}
