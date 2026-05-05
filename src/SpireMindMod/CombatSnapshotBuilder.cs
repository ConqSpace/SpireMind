namespace SpireMindMod;

internal static class CombatSnapshotBuilder
{
    public static Dictionary<string, object?> Build(CombatSnapshotBuildInput input)
    {
        return new Dictionary<string, object?>
        {
            ["schema_version"] = "combat_state.v1",
            ["phase"] = "combat_turn",
            ["state_id"] = "combat_pending",
            ["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["run"] = input.Run,
            ["player"] = input.Player,
            ["piles"] = input.Piles,
            ["enemies"] = input.Enemies,
            ["legal_actions"] = CombatLegalActionBuilder.Build(input.Piles, input.Enemies, input.Player),
            ["relics"] = input.Relics,
            ["debug"] = input.Debug
        };
    }
}

internal sealed record CombatSnapshotBuildInput(
    Dictionary<string, object?> Run,
    Dictionary<string, object?> Player,
    Dictionary<string, object?> Piles,
    List<Dictionary<string, object?>> Enemies,
    object? Relics,
    object? Debug);
