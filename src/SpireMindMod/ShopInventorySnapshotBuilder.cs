namespace SpireMindMod;

internal static class ShopInventorySnapshotBuilder
{
    public static void NormalizeItems(
        List<Dictionary<string, object?>> items,
        int? gold,
        Dictionary<string, object?> playerState)
    {
        bool? hasOpenPotionSlots = ReadDictionaryBool(playerState, "has_open_potion_slots");
        Dictionary<string, int> nextSlotByKind = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < items.Count; index++)
        {
            Dictionary<string, object?> item = items[index];
            string kind = ReadDictionaryString(item, "kind")
                ?? ReadDictionaryString(item, "item_type")
                ?? "unknown";
            string slotGroup = ReadDictionaryString(item, "slot_group") ?? BuildSlotGroup(kind);
            int slotIndex = ReadDictionaryInt(item, "slot_index")
                ?? (nextSlotByKind.TryGetValue(slotGroup, out int nextIndex) ? nextIndex : 0);
            nextSlotByKind[slotGroup] = Math.Max(
                nextSlotByKind.TryGetValue(slotGroup, out int currentNext) ? currentNext : 0,
                slotIndex + 1);

            int? cost = ReadDictionaryInt(item, "cost") ?? ReadDictionaryInt(item, "price");
            bool isStocked = ReadDictionaryBool(item, "is_stocked") ?? ReadDictionaryBool(item, "sold_out") != true;
            bool isAffordable = cost is not null && gold is not null && gold.Value >= cost.Value;
            string modelId = BuildModelId(item, kind);

            item["kind"] = kind;
            item["model_id"] = modelId;
            item["cost"] = cost;
            item["is_stocked"] = isStocked;
            item["is_affordable"] = isAffordable;
            bool potionSlotAllowsPurchase = !kind.Equals("potion", StringComparison.OrdinalIgnoreCase)
                || hasOpenPotionSlots != false;
            item["is_purchase_legal_now"] = isStocked && isAffordable && potionSlotAllowsPurchase && kind != "unknown";
            if (kind.Equals("potion", StringComparison.OrdinalIgnoreCase) && hasOpenPotionSlots == false)
            {
                item["purchase_blocked_reason"] = "potion_slots_full";
            }

            item["slot_group"] = slotGroup;
            item["slot_index"] = slotIndex;
            item["locator_id"] = $"{slotGroup}:{slotIndex}";
            item["shop_item_index"] = index;
            item["shop_item_id"] = SanitizeActionId($"{slotGroup}_{slotIndex}_{modelId}");
        }
    }

    public static bool IsAvailableRemovalService(Dictionary<string, object?> item)
    {
        string kind = ReadDictionaryString(item, "kind") ?? ReadDictionaryString(item, "item_type") ?? string.Empty;
        if (!kind.Equals("service", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string name = ReadDictionaryString(item, "name") ?? string.Empty;
        string nodeType = ReadDictionaryString(item, "node_type") ?? string.Empty;
        return ReadDictionaryBool(item, "sold_out") != true
            && (ContainsAny(name, "Remove", "Removal", "Purge", "제거")
                || ContainsAny(nodeType, "Remove", "Removal", "Purge"));
    }

    private static string BuildSlotGroup(string kind)
    {
        if (kind.Equals("card", StringComparison.OrdinalIgnoreCase))
        {
            return "card";
        }

        if (kind.Equals("relic", StringComparison.OrdinalIgnoreCase))
        {
            return "relic";
        }

        if (kind.Equals("potion", StringComparison.OrdinalIgnoreCase))
        {
            return "potion";
        }

        if (kind.Equals("service", StringComparison.OrdinalIgnoreCase))
        {
            return "service";
        }

        return "unknown";
    }

    private static string BuildModelId(Dictionary<string, object?> item, string kind)
    {
        if (kind.Equals("card", StringComparison.OrdinalIgnoreCase)
            && item.TryGetValue("card", out object? cardValue)
            && cardValue is Dictionary<string, object?> card)
        {
            return ReadDictionaryString(card, "card_id")
                ?? ReadDictionaryString(card, "instance_id")
                ?? ReadDictionaryString(item, "name")
                ?? "unknown_card";
        }

        if (item.TryGetValue(kind, out object? nestedValue)
            && nestedValue is Dictionary<string, object?> nested)
        {
            return ReadDictionaryString(nested, "id")
                ?? ReadDictionaryString(nested, "name")
                ?? ReadDictionaryString(item, "name")
                ?? $"unknown_{kind}";
        }

        return ReadDictionaryString(item, "name") ?? $"unknown_{kind}";
    }

    private static bool ContainsAny(string? text, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
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
