namespace SpireMindMod;

internal static class CombatLegalActionBuilder
{
    public static List<Dictionary<string, object?>> Build(
        Dictionary<string, object?> piles,
        List<Dictionary<string, object?>> enemies,
        Dictionary<string, object?> player)
    {
        List<Dictionary<string, object?>> actions = new();
        List<Dictionary<string, object?>> hand = ReadDictionaryList(piles, "hand");
        List<Dictionary<string, object?>> liveEnemies = enemies
            .Where(IsEnemyTargetCandidate)
            .ToList();
        int? playerEnergy = ReadDictionaryInt(player, "energy");
        List<Dictionary<string, object?>> potionSlots = ReadDictionaryList(player, "potion_slots");

        foreach (Dictionary<string, object?> card in hand)
        {
            bool? playable = ReadDictionaryBool(card, "playable");
            if (playable == false)
            {
                continue;
            }

            if (IsNeverPlayableCard(card))
            {
                continue;
            }

            if (RequiresUnsupportedAdapterFlow(card))
            {
                continue;
            }

            int? energyCost = ReadDictionaryInt(card, "cost");
            if (playerEnergy is not null && energyCost is not null && energyCost.Value > playerEnergy.Value)
            {
                continue;
            }

            string cardInstanceId = ReadDictionaryString(card, "instance_id") ?? "hand_unknown";
            int? combatCardId = ReadDictionaryInt(card, "combat_card_id");
            string cardName = ReadDictionaryString(card, "name")
                ?? ReadDictionaryString(card, "card_id")
                ?? cardInstanceId;

            string? targetType = ReadDictionaryString(card, "target_type");
            bool? runtimeCanPlayNoTarget = ReadDictionaryBool(card, "runtime_can_play_no_target");
            if (CardRequiresEnemyTarget(card, targetType))
            {
                foreach (Dictionary<string, object?> enemy in liveEnemies)
                {
                    string? targetId = ReadDictionaryString(enemy, "id");
                    if (string.IsNullOrWhiteSpace(targetId))
                    {
                        continue;
                    }

                    string enemyName = ReadDictionaryString(enemy, "name") ?? targetId;
                    int? targetCombatId = ReadDictionaryInt(enemy, "combat_id");
                    actions.Add(CreatePlayCardAction(
                        $"play_{cardInstanceId}_{targetId}",
                        card,
                        cardInstanceId,
                        combatCardId,
                        true,
                        targetId,
                        targetCombatId,
                        enemy,
                        energyCost,
                        playerEnergy,
                        playable,
                        $"Play {cardName} on {enemyName}.",
                        BuildValidationNote(card, enemy, playerEnergy, energyCost, playable, true)));
                }
            }
            else
            {
                if (runtimeCanPlayNoTarget == false)
                {
                    continue;
                }

                string suffix = ContainsAny(targetType, "AllEnemies")
                    ? "all_enemies"
                    : "no_target";
                actions.Add(CreatePlayCardAction(
                    $"play_{cardInstanceId}_{suffix}",
                    card,
                    cardInstanceId,
                    combatCardId,
                    false,
                    null,
                    null,
                    null,
                    energyCost,
                    playerEnergy,
                    playable,
                    ContainsAny(targetType, "AllEnemies")
                        ? $"Play {cardName} against all enemies."
                        : $"Play {cardName} with no target.",
                    BuildValidationNote(card, null, playerEnergy, energyCost, playable, false)));
            }
        }

        foreach (Dictionary<string, object?> potionSlot in potionSlots)
        {
            if (ReadDictionaryBool(potionSlot, "empty") == true)
            {
                continue;
            }

            Dictionary<string, object?>? potion = ReadDictionaryObject(potionSlot, "potion");
            if (potion is null || ReadDictionaryBool(potion, "is_usable_now") == false)
            {
                continue;
            }

            int? potionSlotIndex = ReadDictionaryInt(potionSlot, "slot_index");
            if (potionSlotIndex is null)
            {
                continue;
            }

            string potionId = ReadDictionaryString(potion, "potion_id")
                ?? ReadDictionaryString(potion, "id")
                ?? $"potion_slot_{potionSlotIndex.Value}";
            string potionName = ReadDictionaryString(potion, "name") ?? potionId;
            string? targetType = ReadDictionaryString(potion, "target_type");
            string targetKind = ReadDictionaryString(potion, "target_kind") ?? ResolvePotionTargetKind(targetType);
            bool requiresTarget = ReadDictionaryBool(potion, "requires_target") ?? PotionRequiresTarget(targetType);

            if (targetKind == "enemy")
            {
                foreach (Dictionary<string, object?> enemy in liveEnemies)
                {
                    string? targetId = ReadDictionaryString(enemy, "id");
                    if (string.IsNullOrWhiteSpace(targetId))
                    {
                        continue;
                    }

                    string enemyName = ReadDictionaryString(enemy, "name") ?? targetId;
                    int? targetCombatId = ReadDictionaryInt(enemy, "combat_id");
                    actions.Add(CreateUsePotionAction(
                        $"use_potion_{potionSlotIndex.Value}_{potionId}_{targetId}",
                        potionSlotIndex.Value,
                        potionId,
                        targetType,
                        targetKind,
                        true,
                        targetId,
                        targetCombatId,
                        enemy,
                        $"Use {potionName} on {enemyName}."));
                }
            }
            else if (targetKind is "self" or "player" or "ally")
            {
                string targetId = ReadDictionaryString(player, "id") ?? "player_0";
                int? targetCombatId = ReadDictionaryInt(player, "combat_id");
                string targetName = ReadDictionaryString(player, "name") ?? "player";
                actions.Add(CreateUsePotionAction(
                    $"use_potion_{potionSlotIndex.Value}_{potionId}_{targetId}",
                    potionSlotIndex.Value,
                    potionId,
                    targetType,
                    targetKind,
                    true,
                    targetId,
                    targetCombatId,
                    player,
                    $"Use {potionName} on {targetName}."));
            }
            else if (targetKind == "targeted_no_creature")
            {
                continue;
            }
            else
            {
                actions.Add(CreateUsePotionAction(
                    $"use_potion_{potionSlotIndex.Value}_{potionId}_no_target",
                    potionSlotIndex.Value,
                    potionId,
                    targetType,
                    targetKind,
                    false,
                    null,
                    null,
                    null,
                    $"Use {potionName}."));
            }
        }

        actions.Add(new Dictionary<string, object?>
        {
            ["action_id"] = "end_turn",
            ["type"] = "end_turn",
            ["card_instance_id"] = null,
            ["target_id"] = null,
            ["energy_cost"] = null,
            ["requires_target"] = false,
            ["execution"] = new Dictionary<string, object?>
            {
                ["schema"] = "combat_action_execution.v1",
                ["checks"] = new Dictionary<string, object?>
                {
                    ["runtime_state_rechecked_before_execution"] = true
                }
            },
            ["summary"] = "End the current turn.",
            ["validation_note"] = "Always generated by exporter; no game action is executed during export."
        });

        return actions;
    }

    private static Dictionary<string, object?> CreatePlayCardAction(
        string actionId,
        Dictionary<string, object?> card,
        string cardInstanceId,
        int? combatCardId,
        bool requiresTarget,
        string? targetId,
        int? targetCombatId,
        Dictionary<string, object?>? enemy,
        int? energyCost,
        int? playerEnergy,
        bool? playable,
        string summary,
        string validationNote)
    {
        return new Dictionary<string, object?>
        {
            ["action_id"] = SanitizeActionId(actionId),
            ["type"] = "play_card",
            ["card_instance_id"] = cardInstanceId,
            ["combat_card_id"] = combatCardId,
            ["card_id"] = ReadDictionaryString(card, "card_id"),
            ["name"] = ReadDictionaryString(card, "name"),
            ["upgraded"] = ReadDictionaryBool(card, "upgraded"),
            ["target_id"] = targetId,
            ["target_combat_id"] = targetCombatId,
            ["energy_cost"] = energyCost,
            ["requires_target"] = requiresTarget,
            ["execution"] = BuildPlayCardExecutionMetadata(
                card,
                cardInstanceId,
                combatCardId,
                requiresTarget,
                targetId,
                targetCombatId,
                enemy,
                energyCost,
                playerEnergy,
                playable),
            ["summary"] = summary,
            ["validation_note"] = validationNote
        };
    }

    private static Dictionary<string, object?> CreateUsePotionAction(
        string actionId,
        int potionSlotIndex,
        string potionId,
        string? targetType,
        string targetKind,
        bool requiresTarget,
        string? targetId,
        int? targetCombatId,
        Dictionary<string, object?>? enemy,
        string summary)
    {
        return new Dictionary<string, object?>
        {
            ["action_id"] = SanitizeActionId(actionId),
            ["type"] = "use_potion",
            ["potion_slot_index"] = potionSlotIndex,
            ["potion_id"] = potionId,
            ["target_type"] = targetType,
            ["target_kind"] = targetKind,
            ["requires_target"] = requiresTarget,
            ["target_id"] = targetId,
            ["target_combat_id"] = targetCombatId,
            ["execution"] = BuildUsePotionExecutionMetadata(
                potionSlotIndex,
                potionId,
                targetType,
                targetKind,
                requiresTarget,
                targetId,
                targetCombatId,
                enemy),
            ["summary"] = summary,
            ["validation_note"] = "Runtime validation checks the same potion slot, potion id, target requirement, and target liveness before calling PotionModel.EnqueueManualUse."
        };
    }

    private static Dictionary<string, object?> BuildPlayCardExecutionMetadata(
        Dictionary<string, object?> card,
        string cardInstanceId,
        int? combatCardId,
        bool requiresTarget,
        string? targetId,
        int? targetCombatId,
        Dictionary<string, object?>? enemy,
        int? energyCost,
        int? playerEnergy,
        bool? playable)
    {
        return new Dictionary<string, object?>
        {
            ["schema"] = "combat_action_execution.v1",
            ["card"] = new Dictionary<string, object?>
            {
                ["instance_id"] = cardInstanceId,
                ["combat_card_id"] = combatCardId,
                ["card_id"] = ReadDictionaryString(card, "card_id"),
                ["name"] = ReadDictionaryString(card, "name"),
                ["type"] = ReadDictionaryString(card, "type"),
                ["cost"] = energyCost,
                ["playable"] = playable
            },
            ["target"] = enemy is null
                ? null
                : new Dictionary<string, object?>
                {
                    ["id"] = targetId,
                    ["combat_id"] = targetCombatId,
                    ["name"] = ReadDictionaryString(enemy, "name"),
                    ["hp"] = ReadDictionaryInt(enemy, "hp"),
                    ["block"] = ReadDictionaryInt(enemy, "block")
                },
            ["checks"] = new Dictionary<string, object?>
            {
                ["requires_target"] = requiresTarget,
                ["has_target"] = !string.IsNullOrWhiteSpace(targetId),
                ["energy_cost"] = energyCost,
                ["player_energy_at_export"] = playerEnergy,
                ["affordable_at_export"] = playerEnergy is null || energyCost is null || energyCost.Value <= playerEnergy.Value,
                ["runtime_state_rechecked_before_execution"] = true
            }
        };
    }

    private static Dictionary<string, object?> BuildUsePotionExecutionMetadata(
        int potionSlotIndex,
        string potionId,
        string? targetType,
        string targetKind,
        bool requiresTarget,
        string? targetId,
        int? targetCombatId,
        Dictionary<string, object?>? enemy)
    {
        return new Dictionary<string, object?>
        {
            ["schema"] = "combat_action_execution.v1",
            ["potion"] = new Dictionary<string, object?>
            {
                ["slot_index"] = potionSlotIndex,
                ["potion_id"] = potionId,
                ["target_type"] = targetType,
                ["target_kind"] = targetKind
            },
            ["target"] = enemy is null
                ? null
                : new Dictionary<string, object?>
                {
                    ["id"] = targetId,
                    ["combat_id"] = targetCombatId,
                    ["name"] = ReadDictionaryString(enemy, "name"),
                    ["hp"] = ReadDictionaryInt(enemy, "hp"),
                    ["block"] = ReadDictionaryInt(enemy, "block")
                },
            ["checks"] = new Dictionary<string, object?>
            {
                ["requires_target"] = requiresTarget,
                ["has_target"] = !string.IsNullOrWhiteSpace(targetId),
                ["runtime_state_rechecked_before_execution"] = true
            }
        };
    }

    private static string BuildValidationNote(
        Dictionary<string, object?> card,
        Dictionary<string, object?>? enemy,
        int? playerEnergy,
        int? energyCost,
        bool? playable,
        bool requiresTarget)
    {
        List<string> notes = new() { "Heuristic candidate generated from exported combat_state.v1 fields." };

        if (playable is null)
        {
            notes.Add("Card playable flag was unavailable; runtime validation is still required.");
        }

        if (energyCost is null)
        {
            notes.Add("Card cost was unavailable; energy validation was not finalized.");
        }
        else if (playerEnergy is null)
        {
            notes.Add("Player energy was unavailable; energy validation was not finalized.");
        }

        if (requiresTarget && enemy is not null && ReadDictionaryInt(enemy, "hp") is null)
        {
            notes.Add("Enemy hp was unavailable; target is inferred from the current enemy list.");
        }

        if (!requiresTarget)
        {
            string? cardType = ReadDictionaryString(card, "type");
            if (string.IsNullOrWhiteSpace(cardType))
            {
                notes.Add("Card type was unavailable; no-target play is conservative and must be rechecked before execution.");
            }
            else if (IsSkillOrPowerCard(card))
            {
                notes.Add("Skill/power target requirement is not fully known; no-target play is a safe candidate only.");
            }
            else
            {
                notes.Add("Non-attack card target requirement is not fully known; no-target play is a safe candidate only.");
            }
        }

        return string.Join(" ", notes);
    }

    private static bool IsEnemyTargetCandidate(Dictionary<string, object?> enemy)
    {
        int? hp = ReadDictionaryInt(enemy, "hp");
        return hp is null || hp.Value > 0;
    }

    private static bool IsAttackCard(Dictionary<string, object?> card)
    {
        string? cardType = ReadDictionaryString(card, "type");
        return !string.IsNullOrWhiteSpace(cardType)
            && cardType.Contains("attack", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNeverPlayableCard(Dictionary<string, object?> card)
    {
        bool? playable = ReadDictionaryBool(card, "playable");
        if (playable == true)
        {
            return false;
        }

        string? cardType = ReadDictionaryString(card, "type");
        if (!string.IsNullOrWhiteSpace(cardType)
            && (cardType.Contains("status", StringComparison.OrdinalIgnoreCase)
                || cardType.Contains("curse", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string? cardId = ReadDictionaryString(card, "card_id");
        return ContainsAny(cardId, "CARD.DAZED", "CARD.WOUND", "CARD.BURN", "CARD.VOID", "CARD.SLIMED", "CURSE");
    }

    private static bool RequiresUnsupportedAdapterFlow(Dictionary<string, object?> card)
    {
        return false;
    }

    private static bool CardRequiresEnemyTarget(Dictionary<string, object?> card, string? targetType)
    {
        if (ContainsAny(targetType, "AllEnemies", "None", "Self", "Player"))
        {
            return false;
        }

        if (ContainsAny(targetType, "AnyEnemy", "Enemy", "Monster", "Creature", "Target"))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(targetType) && IsAttackCard(card);
    }

    private static bool IsSkillOrPowerCard(Dictionary<string, object?> card)
    {
        string? cardType = ReadDictionaryString(card, "type");
        return !string.IsNullOrWhiteSpace(cardType)
            && (cardType.Contains("skill", StringComparison.OrdinalIgnoreCase)
                || cardType.Contains("power", StringComparison.OrdinalIgnoreCase));
    }

    private static bool PotionRequiresTarget(string? targetType)
    {
        if (string.IsNullOrWhiteSpace(targetType))
        {
            return false;
        }

        return ResolvePotionTargetKind(targetType) is "self" or "enemy" or "player" or "ally" or "targeted_no_creature";
    }

    private static string ResolvePotionTargetKind(string? targetType)
    {
        if (string.IsNullOrWhiteSpace(targetType))
        {
            return "none";
        }

        if (ContainsAny(targetType, "AllEnemies"))
        {
            return "all_enemies";
        }

        if (ContainsAny(targetType, "AnyEnemy", "Enemy", "Monster"))
        {
            return "enemy";
        }

        if (ContainsAny(targetType, "AnyPlayer", "Player"))
        {
            return "player";
        }

        if (ContainsAny(targetType, "AnyAlly", "Ally"))
        {
            return "ally";
        }

        if (ContainsAny(targetType, "Self"))
        {
            return "self";
        }

        if (ContainsAny(targetType, "TargetedNoCreature"))
        {
            return "targeted_no_creature";
        }

        if (ContainsAny(targetType, "None"))
        {
            return "none";
        }

        return ContainsAny(targetType, "Target", "Creature") ? "enemy" : "none";
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

    private static Dictionary<string, object?>? ReadDictionaryObject(Dictionary<string, object?> source, string key)
    {
        return source.TryGetValue(key, out object? value)
            ? value as Dictionary<string, object?>
            : null;
    }

    private static List<Dictionary<string, object?>> ReadDictionaryList(Dictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out object? value))
        {
            return new List<Dictionary<string, object?>>();
        }

        return value as List<Dictionary<string, object?>> ?? new List<Dictionary<string, object?>>();
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
