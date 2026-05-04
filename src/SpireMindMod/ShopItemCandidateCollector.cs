namespace SpireMindMod;

internal static class ShopItemCandidateCollector
{
    public static List<Dictionary<string, object?>> Collect(ShopItemCandidateCollectorContext context)
    {
        List<Dictionary<string, object?>> items = new();
        HashSet<string> seenKeys = new(StringComparer.OrdinalIgnoreCase);

        AddInventoryItems(context, items, seenKeys);
        if (items.Count > 0)
        {
            // MerchantInventory가 있으면 실제 구매 슬롯의 기준으로 삼는다.
            // 화면 노드 보조 탐색을 함께 사용하면 같은 유물과 포션이 중복 후보로 잡힌다.
            return items;
        }

        AddGridCardItems(context, items, seenKeys);
        AddMerchantCardItems(context, items, seenKeys);
        AddItemCandidates(context, items, seenKeys, "relic", "Relic");
        AddItemCandidates(context, items, seenKeys, "potion", "Potion");
        AddServiceCandidates(context, items, seenKeys);

        return items;
    }

    private static void AddInventoryItems(
        ShopItemCandidateCollectorContext context,
        List<Dictionary<string, object?>> items,
        HashSet<string> seenKeys)
    {
        if (context.RuntimeInventory is null)
        {
            return;
        }

        AddInventoryCardEntries(context, items, seenKeys, "CharacterCardEntries", "character_card");
        AddInventoryCardEntries(context, items, seenKeys, "ColorlessCardEntries", "colorless_card");
        AddInventoryModelEntries(context, items, seenKeys, "RelicEntries", "relic", "Model", "Relic");
        AddInventoryModelEntries(context, items, seenKeys, "PotionEntries", "potion", "Model", "Potion");
        AddInventoryRemovalEntry(context, items, seenKeys);
    }

    private static void AddInventoryCardEntries(
        ShopItemCandidateCollectorContext context,
        List<Dictionary<string, object?>> items,
        HashSet<string> seenKeys,
        string entriesMemberName,
        string slotGroup)
    {
        object? entries = context.FindMemberValue(context.RuntimeInventory, entriesMemberName, "_" + entriesMemberName, ToCamelCase(entriesMemberName));
        List<object> entryList = context.EnumerateObjects(entries).ToList();
        for (int index = 0; index < entryList.Count; index++)
        {
            object entry = entryList[index];
            object? creationResult = context.FindMemberValue(entry, "CreationResult", "creationResult", "_creationResult");
            object? card = context.FindMemberValue(creationResult, "Card", "card", "_card");
            if (card is null)
            {
                continue;
            }

            Dictionary<string, object?> cardState = context.BuildCards("shop", new[] { card }).FirstOrDefault()
                ?? new Dictionary<string, object?>();
            string cardId = ReadDictionaryString(cardState, "card_id")
                ?? context.ReadString(card, "Id", "id", "_id")
                ?? "unknown_card";
            string name = ReadDictionaryString(cardState, "name")
                ?? context.ReadCardName(card)
                ?? cardId;
            string dedupeKey = $"card:{slotGroup}:{cardId}:{index}";
            if (!seenKeys.Add(dedupeKey))
            {
                continue;
            }

            items.Add(BuildInventoryItem(
                context,
                itemType: "card",
                slotGroup: slotGroup,
                slotIndex: index,
                name: name,
                modelId: cardId,
                entry: entry,
                nestedKey: "card",
                nestedValue: cardState));
        }
    }

    private static void AddInventoryModelEntries(
        ShopItemCandidateCollectorContext context,
        List<Dictionary<string, object?>> items,
        HashSet<string> seenKeys,
        string entriesMemberName,
        string itemType,
        string modelMemberName,
        string fallbackTypeName)
    {
        object? entries = context.FindMemberValue(context.RuntimeInventory, entriesMemberName, "_" + entriesMemberName, ToCamelCase(entriesMemberName));
        List<object> entryList = context.EnumerateObjects(entries).ToList();
        for (int index = 0; index < entryList.Count; index++)
        {
            object entry = entryList[index];
            object? model = context.FindMemberValue(entry, modelMemberName, ToCamelCase(modelMemberName), "_" + ToCamelCase(modelMemberName));
            Dictionary<string, object?>? summary = context.BuildItemSummary(model);
            if (summary is null)
            {
                continue;
            }

            string modelId = ReadDictionaryString(summary, "id") ?? $"unknown_{itemType}";
            string name = ReadDictionaryString(summary, "name") ?? modelId;
            string dedupeKey = $"{itemType}:{index}:{modelId}:{name}";
            if (!seenKeys.Add(dedupeKey))
            {
                continue;
            }

            items.Add(BuildInventoryItem(
                context,
                itemType: itemType,
                slotGroup: itemType,
                slotIndex: index,
                name: name,
                modelId: modelId,
                entry: entry,
                nestedKey: itemType,
                nestedValue: summary,
                fallbackTypeName: fallbackTypeName));
        }
    }

    private static void AddInventoryRemovalEntry(
        ShopItemCandidateCollectorContext context,
        List<Dictionary<string, object?>> items,
        HashSet<string> seenKeys)
    {
        object? entry = context.FindMemberValue(context.RuntimeInventory, "CardRemovalEntry", "cardRemovalEntry", "_cardRemovalEntry");
        if (entry is null || !seenKeys.Add("service:CARD_REMOVAL:0"))
        {
            return;
        }

        items.Add(BuildInventoryItem(
            context,
            itemType: "service",
            slotGroup: "service",
            slotIndex: 0,
            name: "Card Removal",
            modelId: "CARD_REMOVAL",
            entry: entry,
            nestedKey: null,
            nestedValue: null,
            fallbackTypeName: "CardRemoval"));
    }

    private static Dictionary<string, object?> BuildInventoryItem(
        ShopItemCandidateCollectorContext context,
        string itemType,
        string slotGroup,
        int slotIndex,
        string name,
        string modelId,
        object entry,
        string? nestedKey,
        object? nestedValue,
        string? fallbackTypeName = null)
    {
        int? price = context.ReadPrice(entry);
        Dictionary<string, object?> item = new()
        {
            ["shop_item_id"] = SanitizeActionId($"{slotGroup}_{slotIndex}_{modelId}"),
            ["shop_item_index"] = 0,
            ["item_type"] = itemType,
            ["kind"] = itemType,
            ["name"] = name,
            ["model_id"] = modelId,
            ["price"] = price,
            ["cost"] = price,
            ["sold_out"] = context.ReadBool(entry, "SoldOut", "soldOut", "_soldOut", "IsSoldOut", "isSoldOut", "_isSoldOut"),
            ["is_stocked"] = context.ReadBool(entry, "IsStocked", "isStocked", "_isStocked") ?? context.ReadBool(entry, "SoldOut", "soldOut", "_soldOut") != true,
            ["is_affordable"] = context.ReadBool(entry, "EnoughGold", "enoughGold", "_enoughGold", "CanAfford", "canAfford", "_canAfford"),
            ["is_on_sale"] = context.ReadBool(entry, "IsOnSale", "isOnSale", "_isOnSale"),
            ["slot_group"] = slotGroup,
            ["slot_index"] = slotIndex,
            ["locator_id"] = $"{slotGroup}:{slotIndex}",
            ["entry_type"] = entry.GetType().FullName ?? entry.GetType().Name,
            ["model_type"] = fallbackTypeName
        };

        if (!string.IsNullOrWhiteSpace(nestedKey) && nestedValue is not null)
        {
            item[nestedKey] = nestedValue;
        }

        return item;
    }

    private static void AddGridCardItems(
        ShopItemCandidateCollectorContext context,
        List<Dictionary<string, object?>> items,
        HashSet<string> seenKeys)
    {
        List<object> holders = context.FindGridCardHolders(context.ShopScreen);
        for (int index = 0; index < holders.Count; index++)
        {
            object holder = holders[index];
            if (context.IsLiveVisibleControlOrNull(holder) == false)
            {
                continue;
            }

            object? card = context.ExtractCardFromHolder(holder);
            if (card is null)
            {
                continue;
            }

            Dictionary<string, object?> cardState = context.BuildCards("shop", new[] { card }).FirstOrDefault()
                ?? new Dictionary<string, object?>();
            string name = ReadDictionaryString(cardState, "name")
                ?? context.ReadCardName(card)
                ?? context.GetReadableName(card)
                ?? "unknown_card";
            string dedupeKey = $"card:{ReadDictionaryString(cardState, "card_id") ?? name}:{index}";
            if (!seenKeys.Add(dedupeKey))
            {
                continue;
            }

            items.Add(new Dictionary<string, object?>
            {
                ["shop_item_id"] = SanitizeActionId($"shop_card_{index}_{name}"),
                ["shop_item_index"] = items.Count,
                ["item_type"] = "card",
                ["name"] = name,
                ["price"] = context.ReadPrice(holder, card),
                ["sold_out"] = context.ReadBool(holder, "IsSoldOut", "isSoldOut", "_isSoldOut"),
                ["card"] = cardState,
                ["holder_type"] = holder.GetType().FullName ?? holder.GetType().Name,
                ["visible"] = context.ReadBool(holder, "Visible", "visible"),
                ["visible_in_tree"] = context.TryInvokeBoolMethod(holder, "IsVisibleInTree")
            });
        }
    }

    private static void AddMerchantCardItems(
        ShopItemCandidateCollectorContext context,
        List<Dictionary<string, object?>> items,
        HashSet<string> seenKeys)
    {
        List<object> cardNodes = context.EnumerateNodeDescendants(context.ShopScreen)
            .Where(candidate => ContainsAny(candidate.GetType().FullName ?? candidate.GetType().Name, "NMerchantCard"))
            .Where(candidate => context.IsLiveVisibleControlOrNull(candidate) != false)
            .Distinct(ReferenceEqualityComparer.Instance)
            .ToList();

        for (int index = 0; index < cardNodes.Count; index++)
        {
            object cardNode = cardNodes[index];
            object? card = context.FindMemberValue(cardNode, "Card", "card", "_card", "Model", "model", "_model", "CardModel", "cardModel", "_cardModel");
            if (card is null)
            {
                continue;
            }

            Dictionary<string, object?> cardState = context.BuildCards("shop", new[] { card }).FirstOrDefault()
                ?? new Dictionary<string, object?>();
            string name = ReadDictionaryString(cardState, "name")
                ?? context.ReadCardName(card)
                ?? context.GetReadableName(card)
                ?? "unknown_card";
            string dedupeKey = $"merchant_card:{ReadDictionaryString(cardState, "card_id") ?? name}:{index}";
            if (!seenKeys.Add(dedupeKey))
            {
                continue;
            }

            items.Add(new Dictionary<string, object?>
            {
                ["shop_item_id"] = SanitizeActionId($"shop_card_{items.Count}_{name}"),
                ["shop_item_index"] = items.Count,
                ["item_type"] = "card",
                ["name"] = name,
                ["price"] = context.ReadPrice(cardNode, card),
                ["sold_out"] = context.ReadBool(cardNode, "IsSoldOut", "isSoldOut", "_isSoldOut", "SoldOut", "soldOut"),
                ["card"] = cardState,
                ["node_type"] = cardNode.GetType().FullName ?? cardNode.GetType().Name,
                ["model_type"] = card.GetType().FullName ?? card.GetType().Name,
                ["visible"] = context.ReadBool(cardNode, "Visible", "visible"),
                ["visible_in_tree"] = context.TryInvokeBoolMethod(cardNode, "IsVisibleInTree")
            });
        }
    }

    private static void AddItemCandidates(
        ShopItemCandidateCollectorContext context,
        List<Dictionary<string, object?>> items,
        HashSet<string> seenKeys,
        string itemType,
        string typeHint)
    {
        IEnumerable<object> candidates = context.EnumerateNodeDescendants(context.ShopScreen)
            .Where(candidate => IsShopItemCandidate(candidate, typeHint))
            .Distinct(ReferenceEqualityComparer.Instance);

        foreach (object candidate in candidates)
        {
            object itemModel = ResolveShopItemModel(context, candidate, typeHint) ?? candidate;
            Dictionary<string, object?>? itemSummary = context.BuildItemSummary(itemModel);
            if (itemSummary is null)
            {
                continue;
            }

            string name = ReadDictionaryString(itemSummary, "name")
                ?? context.GetReadableName(itemModel)
                ?? $"unknown_{itemType}";
            string id = ReadDictionaryString(itemSummary, "id") ?? name;
            string dedupeKey = $"{itemType}:{id}:{name}";
            if (!seenKeys.Add(dedupeKey))
            {
                continue;
            }

            items.Add(new Dictionary<string, object?>
            {
                ["shop_item_id"] = SanitizeActionId($"shop_{itemType}_{items.Count}_{name}"),
                ["shop_item_index"] = items.Count,
                ["item_type"] = itemType,
                ["name"] = name,
                ["price"] = context.ReadPrice(candidate, itemModel),
                ["sold_out"] = context.ReadBool(candidate, "IsSoldOut", "isSoldOut", "_isSoldOut", "SoldOut", "soldOut"),
                [itemType] = itemSummary,
                ["node_type"] = candidate.GetType().FullName ?? candidate.GetType().Name,
                ["model_type"] = itemModel.GetType().FullName ?? itemModel.GetType().Name,
                ["visible"] = context.ReadBool(candidate, "Visible", "visible"),
                ["visible_in_tree"] = context.TryInvokeBoolMethod(candidate, "IsVisibleInTree")
            });
        }
    }

    private static void AddServiceCandidates(
        ShopItemCandidateCollectorContext context,
        List<Dictionary<string, object?>> items,
        HashSet<string> seenKeys)
    {
        IEnumerable<object> candidates = context.EnumerateNodeDescendants(context.ShopScreen)
            .Concat(context.GraphValues.Where(value => value is not null).Cast<object>())
            .Where(candidate => ContainsAny(candidate.GetType().FullName ?? candidate.GetType().Name, "Remove", "Purge", "Service"))
            .Distinct(ReferenceEqualityComparer.Instance);

        foreach (object candidate in candidates)
        {
            string typeName = candidate.GetType().FullName ?? candidate.GetType().Name;
            string name = context.ReadString(candidate, "name", "_name", "displayName", "_displayName", "title", "_title")
                ?? context.GetReadableName(candidate)
                ?? "unknown_service";
            string dedupeKey = $"service:{typeName}:{name}";
            if (!seenKeys.Add(dedupeKey))
            {
                continue;
            }

            items.Add(new Dictionary<string, object?>
            {
                ["shop_item_id"] = SanitizeActionId($"shop_service_{items.Count}_{name}"),
                ["shop_item_index"] = items.Count,
                ["item_type"] = "service",
                ["name"] = name,
                ["price"] = context.ReadPrice(candidate),
                ["node_type"] = typeName,
                ["visible"] = context.ReadBool(candidate, "Visible", "visible"),
                ["visible_in_tree"] = context.TryInvokeBoolMethod(candidate, "IsVisibleInTree")
            });
        }
    }

    private static bool IsShopItemCandidate(object candidate, string typeHint)
    {
        string typeName = candidate.GetType().FullName ?? candidate.GetType().Name;
        if (ContainsAny(typeName, "List", "Dictionary", "Collection", "Manager", "Reward", "Action", "Iterator", "GrabBag"))
        {
            return false;
        }

        return ContainsAny(typeName, $"NMerchant{typeHint}", $"Merchant{typeHint}", $"Shop{typeHint}");
    }

    private static object? ResolveShopItemModel(ShopItemCandidateCollectorContext context, object candidate, string typeHint)
    {
        string lowerHint = typeHint.ToLowerInvariant();
        object? model = context.FindMemberValue(
            candidate,
            typeHint,
            lowerHint,
            "_" + lowerHint,
            "Model",
            "model",
            "_model",
            "Item",
            "item",
            "_item");
        if (model is null)
        {
            return null;
        }

        string modelTypeName = model.GetType().FullName ?? model.GetType().Name;
        return ContainsAny(modelTypeName, typeHint) ? model : null;
    }

    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return char.ToLowerInvariant(value[0]) + value[1..];
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
}

internal sealed record ShopItemCandidateCollectorContext(
    object ShopScreen,
    object? RuntimeInventory,
    IEnumerable<object?> GraphValues,
    ShopMemberValueReader FindMemberValue,
    Func<object?, IEnumerable<object>> EnumerateObjects,
    Func<object?, IEnumerable<object>> EnumerateNodeDescendants,
    Func<object, bool?> IsLiveVisibleControlOrNull,
    Func<object, string, bool?> TryInvokeBoolMethod,
    Func<object, List<object>> FindGridCardHolders,
    Func<object, object?> ExtractCardFromHolder,
    Func<string, object?, List<Dictionary<string, object?>>> BuildCards,
    Func<object?, Dictionary<string, object?>?> BuildItemSummary,
    ShopCardNameReader ReadCardName,
    Func<object?, string?> GetReadableName,
    ShopStringReader ReadString,
    ShopPriceReader ReadPrice,
    ShopBoolReader ReadBool);

internal delegate object? ShopMemberValueReader(object? source, params string[] names);

internal delegate string? ShopCardNameReader(params object?[] sources);

internal delegate string? ShopStringReader(object? source, params string[] names);

internal delegate int? ShopPriceReader(params object?[] sources);

internal delegate bool? ShopBoolReader(object? source, params string[] names);
