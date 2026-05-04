namespace SpireMindMod;

internal static class ShopLegalActionBuilder
{
    public static List<Dictionary<string, object?>> Build(List<Dictionary<string, object?>> items, bool canProceed)
    {
        List<Dictionary<string, object?>> actions = new();
        foreach (Dictionary<string, object?> item in items)
        {
            if (ReadDictionaryBool(item, "is_purchase_legal_now") != true)
            {
                continue;
            }

            string kind = ReadDictionaryString(item, "kind") ?? string.Empty;
            string shopItemId = ReadDictionaryString(item, "shop_item_id") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(shopItemId))
            {
                continue;
            }

            string modelId = ReadDictionaryString(item, "model_id") ?? shopItemId;
            string name = ReadDictionaryString(item, "name") ?? modelId;
            int? cost = ReadDictionaryInt(item, "cost");
            string actionType = kind.Equals("service", StringComparison.OrdinalIgnoreCase)
                ? "remove_card_at_shop"
                : "buy_shop_item";

            actions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = SanitizeActionId($"{actionType}_{shopItemId}"),
                ["type"] = actionType,
                ["shop_item_id"] = shopItemId,
                ["kind"] = kind,
                ["model_id"] = modelId,
                ["cost"] = cost,
                ["slot_group"] = ReadDictionaryString(item, "slot_group"),
                ["slot_index"] = ReadDictionaryInt(item, "slot_index"),
                ["locator_id"] = ReadDictionaryString(item, "locator_id"),
                ["summary"] = actionType == "remove_card_at_shop"
                    ? $"Use shop card removal service for {cost?.ToString() ?? "unknown"} gold."
                    : $"Buy {name} for {cost?.ToString() ?? "unknown"} gold.",
                ["validation_note"] = "Executor must re-read the current merchant inventory and verify slot, model_id, cost, stock, and gold before applying."
            });
        }

        if (canProceed)
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = "proceed_shop",
                ["type"] = "proceed_shop",
                ["summary"] = "상점을 나가고 다음 화면으로 진행합니다.",
                ["validation_note"] = "현재 상점 화면의 진행 버튼입니다."
            });
        }

        return actions;
    }

    private static string SanitizeActionId(string actionId)
    {
        char[] chars = actionId.Select(character =>
            char.IsLetterOrDigit(character) || character == '_'
                ? character
                : '_').ToArray();
        return new string(chars);
    }

    private static string? ReadDictionaryString(Dictionary<string, object?> source, string key)
    {
        return source.TryGetValue(key, out object? value) ? value?.ToString() : null;
    }

    private static int? ReadDictionaryInt(Dictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out object? value) || value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadDictionaryBool(Dictionary<string, object?> source, string key)
    {
        return source.TryGetValue(key, out object? value) && value is bool boolean ? boolean : null;
    }
}
