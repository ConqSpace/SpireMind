using System.Text.Json;

namespace SpireMindMod;

internal static class CombatActionRuntimeContext
{
    private static readonly object SyncRoot = new();
    private static object? latestCombatRoot;
    private static string latestStateId = string.Empty;
    private static string latestCombatStateJson = string.Empty;
    private static List<LegalActionSnapshot> latestLegalActions = new();

    public static void UpdateFromExport(object combatRoot, string combatStateJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(combatStateJson);
            JsonElement root = document.RootElement;
            string stateId = ReadString(root, "state_id") ?? string.Empty;
            List<LegalActionSnapshot> legalActions = ReadLegalActions(root);

            lock (SyncRoot)
            {
                latestCombatRoot = combatRoot;
                latestStateId = stateId;
                latestCombatStateJson = combatStateJson;
                latestLegalActions = legalActions;
            }
        }
        catch
        {
            // 실행 context 갱신 실패는 다음 export에서 회복한다.
        }
    }

    public static CombatActionContextSnapshot GetSnapshot()
    {
        lock (SyncRoot)
        {
            return new CombatActionContextSnapshot(
                latestCombatRoot,
                latestStateId,
                latestCombatStateJson,
                latestLegalActions.ToList());
        }
    }

    private static List<LegalActionSnapshot> ReadLegalActions(JsonElement root)
    {
        List<LegalActionSnapshot> actions = new();
        if (!root.TryGetProperty("legal_actions", out JsonElement legalActions)
            || legalActions.ValueKind != JsonValueKind.Array)
        {
            return actions;
        }

        foreach (JsonElement action in legalActions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? actionId = ReadString(action, "action_id");
            string? actionType = ReadString(action, "type");
            if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(actionType))
            {
                continue;
            }

            actions.Add(new LegalActionSnapshot(
                actionId,
                actionType,
                ReadString(action, "card_instance_id"),
                ReadInt(action, "combat_card_id"),
                ReadString(action, "target_id"),
                ReadInt(action, "target_combat_id"),
                ReadString(action, "reward_id"),
                ReadInt(action, "card_reward_index")));
        }

        return actions;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null => null,
            _ => property.ToString()
        };
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int value))
        {
            return value;
        }

        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), out int stringValue))
        {
            return stringValue;
        }

        return null;
    }
}

internal sealed record CombatActionContextSnapshot(
    object? CombatRoot,
    string StateId,
    string CombatStateJson,
    IReadOnlyList<LegalActionSnapshot> LegalActions)
{
    public LegalActionSnapshot? FindAction(string actionId)
    {
        return LegalActions.FirstOrDefault(action => action.ActionId == actionId);
    }
}

internal sealed record LegalActionSnapshot(
    string ActionId,
    string ActionType,
    string? CardInstanceId,
    int? CombatCardId,
    string? TargetId,
    int? TargetCombatId,
    string? RewardId,
    int? CardRewardIndex);
