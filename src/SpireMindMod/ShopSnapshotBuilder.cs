namespace SpireMindMod;

internal static class ShopSnapshotBuilder
{
    public static Dictionary<string, object?> Build(ShopSnapshotBuildInput input)
    {
        return new Dictionary<string, object?>
        {
            ["schema_version"] = "combat_state.v1",
            ["phase"] = "shop",
            ["state_id"] = "shop_pending",
            ["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["run"] = input.Run,
            ["player"] = input.Player,
            ["piles"] = input.Piles,
            ["enemies"] = input.Enemies,
            ["shop"] = input.Shop,
            ["legal_actions"] = ShopLegalActionBuilder.Build(input.Items, input.CanProceed),
            ["relics"] = input.Relics,
            ["debug"] = input.Debug
        };
    }
}

internal sealed record ShopSnapshotBuildInput(
    Dictionary<string, object?> Run,
    Dictionary<string, object?> Player,
    Dictionary<string, object?> Piles,
    List<Dictionary<string, object?>> Enemies,
    Dictionary<string, object?> Shop,
    List<Dictionary<string, object?>> Items,
    bool CanProceed,
    object? Relics,
    object? Debug);
