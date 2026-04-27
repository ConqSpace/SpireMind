using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpireMindMod;

internal static class CombatStateExporter
{
    private const int ExportIntervalMs = 250;
    private const int MaxSearchDepth = 5;
    private const int MaxVisitedObjects = 600;
    private static readonly SpireMindLogger Logger = new("SpireMind.R2.Export");
    private static readonly Stopwatch ExportTimer = Stopwatch.StartNew();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true
    };

    private static long lastExportAtMs;
    private static string lastStateFingerprint = string.Empty;
    private static bool hasLoggedOutputPath;
    private static object? recentPlayer;
    private static object? recentCombatState;
    private static object? recentPlayerCombatState;
    private static readonly List<object> RecentCardPiles = new();
    private static readonly List<string> LastObservedTypes = new();

    public static void Observe(object? instance)
    {
        if (instance is null)
        {
            return;
        }

        RememberObservedRoot(instance);
        TryExport(instance);
    }

    private static void TryExport(object combatRoot)
    {

        long nowMs = ExportTimer.ElapsedMilliseconds;
        if (nowMs - lastExportAtMs < ExportIntervalMs)
        {
            return;
        }

        lastExportAtMs = nowMs;

        try
        {
            Dictionary<string, object?> state = BuildState(combatRoot);
            string stateFingerprint = ComputeStateFingerprint(state);
            state["state_id"] = $"combat_{stateFingerprint[..16]}";
            state["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string json = JsonSerializer.Serialize(state, JsonOptions);
            if (stateFingerprint == lastStateFingerprint)
            {
                CombatActionRuntimeContext.UpdateFromExport(combatRoot, json);
                CombatStateBridgePoster.PostedStateSnapshot? postedState = CombatStateBridgePoster.GetLatestPostedState();
                if (postedState is null || postedState.StateId != state["state_id"]?.ToString())
                {
                    CombatStateBridgePoster.TryPost(json);
                }

                CombatActionExecutor.Tick();
                return;
            }

            lastStateFingerprint = stateFingerprint;
            string outputPath = GetOutputPath();
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, json);
            CombatActionRuntimeContext.UpdateFromExport(combatRoot, json);
            CombatStateBridgePoster.TryPost(json);
            CombatActionExecutor.Tick();

            if (!hasLoggedOutputPath)
            {
                hasLoggedOutputPath = true;
                Logger.Info($"combat_state.v1 출력 경로: {outputPath}");
            }
        }
        catch (Exception exception)
        {
            Logger.Warning($"전투 상태 추출에 실패했습니다. 게임 진행은 멈추지 않습니다. {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void RememberObservedRoot(object instance)
    {
        string typeName = instance.GetType().FullName ?? instance.GetType().Name;
        RememberObservedType(typeName);

        if (typeName.EndsWith(".Player", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains(".Entities.Players.Player", StringComparison.OrdinalIgnoreCase))
        {
            recentPlayer = instance;
        }

        if (typeName.Contains("CombatState", StringComparison.OrdinalIgnoreCase)
            && !typeName.Contains("PlayerCombatState", StringComparison.OrdinalIgnoreCase))
        {
            recentCombatState = instance;
        }

        if (typeName.Contains("PlayerCombatState", StringComparison.OrdinalIgnoreCase))
        {
            recentPlayerCombatState = instance;
        }

        if (typeName.Contains("CardPile", StringComparison.OrdinalIgnoreCase))
        {
            RememberCardPile(instance);
        }

        ObjectGraph shallowGraph = ObjectGraph.Collect(instance, 2, 80);
        recentPlayer ??= FindFirst(shallowGraph, "Player");
        recentCombatState ??= FindFirstCombatState(shallowGraph);
        recentPlayerCombatState ??= FindFirst(shallowGraph, "PlayerCombatState");
    }

    private static void RememberObservedType(string typeName)
    {
        LastObservedTypes.Remove(typeName);
        LastObservedTypes.Add(typeName);
        if (LastObservedTypes.Count > 12)
        {
            LastObservedTypes.RemoveAt(0);
        }
    }

    private static void RememberCardPile(object cardPile)
    {
        RecentCardPiles.RemoveAll(item => ReferenceEquals(item, cardPile));
        RecentCardPiles.Add(cardPile);
        if (RecentCardPiles.Count > 12)
        {
            RecentCardPiles.RemoveAt(0);
        }
    }

    private static Dictionary<string, object?> BuildState(object currentRoot)
    {
        List<object> roots = GetExportRoots(currentRoot);
        ObjectGraph graph = ObjectGraph.Collect(roots, MaxSearchDepth, MaxVisitedObjects);
        object? combatRoot = recentCombatState ?? currentRoot;
        object? player = recentPlayerCombatState
            ?? recentPlayer
            ?? FindFirst(graph, "PlayerCombatState", "Player");
        object? enemiesSource = FindMemberValue(combatRoot, "enemies", "monsters", "creatures")
            ?? FindFirstEnumerable(graph, "enemies", "monsters");
        object? relicsSource = FindMemberValue(combatRoot, "relics")
            ?? FindMemberValue(player, "relics");

        Dictionary<string, object?> playerState = BuildPlayer(player);
        Dictionary<string, object?> piles = BuildPiles(combatRoot, player, graph);
        List<Dictionary<string, object?>> enemies = BuildEnemies(enemiesSource, graph);

        return new Dictionary<string, object?>
        {
            ["schema_version"] = "combat_state.v1",
            ["phase"] = "combat_turn",
            ["state_id"] = "combat_pending",
            ["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["run"] = BuildRun(combatRoot, graph),
            ["player"] = playerState,
            ["piles"] = piles,
            ["enemies"] = enemies,
            ["legal_actions"] = BuildLegalActions(piles, enemies, playerState),
            ["relics"] = BuildRelics(relicsSource, graph),
            ["debug"] = BuildDebug(currentRoot)
        };
    }

    private static List<object> GetExportRoots(object currentRoot)
    {
        List<object> roots = new();
        AddRoot(roots, currentRoot);
        AddRoot(roots, recentCombatState);
        AddRoot(roots, recentPlayerCombatState);
        AddRoot(roots, recentPlayer);
        foreach (object cardPile in RecentCardPiles)
        {
            AddRoot(roots, cardPile);
        }

        return roots;
    }

    private static void AddRoot(List<object> roots, object? root)
    {
        if (root is null || roots.Any(existing => ReferenceEquals(existing, root)))
        {
            return;
        }

        roots.Add(root);
    }

    private static Dictionary<string, object?> BuildDebug(object currentRoot)
    {
        return new Dictionary<string, object?>
        {
            ["root_type"] = currentRoot.GetType().FullName ?? currentRoot.GetType().Name,
            ["last_observed_types"] = LastObservedTypes.ToArray()
        };
    }

    private static string ComputeStateFingerprint(Dictionary<string, object?> state)
    {
        Dictionary<string, object?> comparableState = state
            .Where(pair => pair.Key is not ("state_id" or "exported_at_ms" or "debug"))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        string json = JsonSerializer.Serialize(comparableState, JsonOptions);
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static Dictionary<string, object?> BuildRun(object combatRoot, ObjectGraph graph)
    {
        object? run = FindFirst(graph, "run", "RunState", "currentRun");
        object? character = FindMemberValue(combatRoot, "character", "playerCharacter")
            ?? FindMemberValue(run, "character", "playerCharacter");
        object? seed = FindMemberValue(combatRoot, "seed", "runSeed")
            ?? FindMemberValue(run, "seed", "runSeed");

        return new Dictionary<string, object?>
        {
            ["game"] = "Slay the Spire 2",
            ["character"] = character?.ToString(),
            ["act"] = ReadInt(combatRoot, "act", "currentAct")
                ?? ReadInt(run, "act", "currentAct"),
            ["floor"] = ReadInt(combatRoot, "floor", "currentFloor", "floorNum")
                ?? ReadInt(run, "floor", "currentFloor", "floorNum"),
            ["ascension"] = ReadInt(combatRoot, "ascension", "ascensionLevel")
                ?? ReadInt(run, "ascension", "ascensionLevel"),
            ["seed"] = seed?.ToString(),
            ["mode"] = ReadString(combatRoot, "mode", "gameMode")
                ?? ReadString(run, "mode", "gameMode")
        };
    }

    private static Dictionary<string, object?> BuildPlayer(object? player)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "player_0",
            ["hp"] = ReadInt(player, "hp", "currentHp", "currentHealth", "health"),
            ["max_hp"] = ReadInt(player, "maxHp", "maxHealth"),
            ["block"] = ReadInt(player, "block", "currentBlock", "shield"),
            ["energy"] = ReadInt(player, "energy", "currentEnergy"),
            ["max_energy"] = ReadInt(player, "maxEnergy", "energyMax"),
            ["buffs"] = Array.Empty<object>(),
            ["debuffs"] = Array.Empty<object>(),
            ["powers_unknown"] = BuildPowersUnknown(player)
        };
    }

    private static Dictionary<string, object?> BuildPiles(object combatRoot, object? player, ObjectGraph graph)
    {
        return new Dictionary<string, object?>
        {
            ["hand"] = BuildCards("hand", FindPile(combatRoot, player, graph, "hand", "cardsInHand")),
            ["draw_pile"] = BuildCards("draw", FindPile(combatRoot, player, graph, "drawPile", "draw_pile", "draw")),
            ["discard_pile"] = BuildCards("discard", FindPile(combatRoot, player, graph, "discardPile", "discard_pile", "discard")),
            ["exhaust_pile"] = BuildCards("exhaust", FindPile(combatRoot, player, graph, "exhaustPile", "exhaust_pile", "exhaust"))
        };
    }

    private static object? FindPile(object combatRoot, object? player, ObjectGraph graph, params string[] names)
    {
        return FindMemberValue(player, names)
            ?? FindMemberValue(combatRoot, names)
            ?? FindFirstEnumerable(graph, names)
            ?? FindRecentCardPile(names);
    }

    private static object? FindRecentCardPile(params string[] names)
    {
        return RecentCardPiles.LastOrDefault(cardPile =>
            ContainsAny(GetReadableName(cardPile), names)
            || ContainsAny(cardPile.GetType().FullName, names)
            || names.Any(name => FindMemberValue(cardPile, "name", "id", "pileType", "type")?.ToString()?.Contains(name, StringComparison.OrdinalIgnoreCase) == true));
    }

    private static List<Dictionary<string, object?>> BuildCards(string pileName, object? source)
    {
        List<Dictionary<string, object?>> cards = new();
        int index = 0;

        foreach (object card in EnumerateCards(source))
        {
            string fallbackName = GetReadableName(card);
            cards.Add(new Dictionary<string, object?>
            {
                ["instance_id"] = $"{pileName}_{index}",
                ["card_id"] = ReadString(card, "id", "cardId", "key") ?? fallbackName,
                ["name"] = ReadString(card, "name", "displayName", "title") ?? fallbackName,
                ["type"] = ReadString(card, "type", "cardType"),
                ["cost"] = ReadInt(card, "cost", "currentCost", "energyCost"),
                ["base_cost"] = ReadInt(card, "baseCost", "baseEnergyCost"),
                ["upgraded"] = ReadBool(card, "upgraded", "isUpgraded"),
                ["playable"] = ReadBool(card, "playable", "canPlay", "isPlayable")
            });
            index++;
        }

        return cards;
    }

    private static List<Dictionary<string, object?>> BuildLegalActions(
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

        foreach (Dictionary<string, object?> card in hand)
        {
            bool? playable = ReadDictionaryBool(card, "playable");
            if (playable == false)
            {
                continue;
            }

            int? energyCost = ReadDictionaryInt(card, "cost");
            if (playerEnergy is not null && energyCost is not null && energyCost.Value > playerEnergy.Value)
            {
                continue;
            }

            string cardInstanceId = ReadDictionaryString(card, "instance_id") ?? "hand_unknown";
            string cardName = ReadDictionaryString(card, "name")
                ?? ReadDictionaryString(card, "card_id")
                ?? cardInstanceId;

            if (IsAttackCard(card))
            {
                foreach (Dictionary<string, object?> enemy in liveEnemies)
                {
                    string? targetId = ReadDictionaryString(enemy, "id");
                    if (string.IsNullOrWhiteSpace(targetId))
                    {
                        continue;
                    }

                    string enemyName = ReadDictionaryString(enemy, "name") ?? targetId;
                    actions.Add(CreatePlayCardAction(
                        $"play_{cardInstanceId}_{targetId}",
                        cardInstanceId,
                        targetId,
                        energyCost,
                        $"Play {cardName} on {enemyName}.",
                        BuildValidationNote(card, enemy, playerEnergy, energyCost, playable, true)));
                }
            }
            else
            {
                actions.Add(CreatePlayCardAction(
                    $"play_{cardInstanceId}_no_target",
                    cardInstanceId,
                    null,
                    energyCost,
                    $"Play {cardName} with no target.",
                    BuildValidationNote(card, null, playerEnergy, energyCost, playable, false)));
            }
        }

        actions.Add(new Dictionary<string, object?>
        {
            ["action_id"] = "end_turn",
            ["type"] = "end_turn",
            ["card_instance_id"] = null,
            ["target_id"] = null,
            ["energy_cost"] = null,
            ["summary"] = "End the current turn.",
            ["validation_note"] = "Always generated by exporter; no game action is executed during export."
        });

        return actions;
    }

    private static Dictionary<string, object?> CreatePlayCardAction(
        string actionId,
        string cardInstanceId,
        string? targetId,
        int? energyCost,
        string summary,
        string validationNote)
    {
        return new Dictionary<string, object?>
        {
            ["action_id"] = SanitizeActionId(actionId),
            ["type"] = "play_card",
            ["card_instance_id"] = cardInstanceId,
            ["target_id"] = targetId,
            ["energy_cost"] = energyCost,
            ["summary"] = summary,
            ["validation_note"] = validationNote
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

    private static bool IsSkillOrPowerCard(Dictionary<string, object?> card)
    {
        string? cardType = ReadDictionaryString(card, "type");
        return !string.IsNullOrWhiteSpace(cardType)
            && (cardType.Contains("skill", StringComparison.OrdinalIgnoreCase)
                || cardType.Contains("power", StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizeActionId(string actionId)
    {
        char[] chars = actionId.Select(character =>
            char.IsLetterOrDigit(character) || character == '_'
                ? character
                : '_').ToArray();
        return new string(chars);
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

    private static IEnumerable<object> EnumerateCards(object? source)
    {
        foreach (object item in EnumerateObjects(source))
        {
            if (IsCardPileLike(item))
            {
                object? cardsSource = FindCardPileContents(item);
                foreach (object card in EnumerateObjects(cardsSource))
                {
                    if (!IsCardPileLike(card))
                    {
                        yield return card;
                    }
                }
            }
            else
            {
                yield return item;
            }
        }
    }

    private static object? FindCardPileContents(object cardPile)
    {
        return FindMemberValue(
            cardPile,
            "Cards",
            "cards",
            "Items",
            "items",
            "_cards",
            "contents",
            "Contents",
            "list",
            "List",
            "values",
            "Values");
    }

    private static bool IsCardPileLike(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return typeName.Contains("CardPile", StringComparison.OrdinalIgnoreCase);
    }

    private static List<Dictionary<string, object?>> BuildEnemies(object? enemiesSource, ObjectGraph graph)
    {
        IEnumerable<object> enemies = EnumerateObjects(enemiesSource).ToList();
        if (!enemies.Any())
        {
            enemies = graph.Nodes
                .Select(node => node.Value)
                .Where(value => value is not null && IsEnemyLike(value.GetType()))
                .Cast<object>()
                .Distinct(ReferenceEqualityComparer.Instance);
        }

        List<Dictionary<string, object?>> result = new();
        int index = 0;
        foreach (object enemy in enemies)
        {
            result.Add(new Dictionary<string, object?>
            {
                ["id"] = ReadString(enemy, "id", "enemyId", "monsterId") ?? $"enemy_{index}",
                ["name"] = ReadString(enemy, "name", "displayName") ?? GetReadableName(enemy),
                ["hp"] = ReadInt(enemy, "hp", "currentHp", "currentHealth", "health"),
                ["max_hp"] = ReadInt(enemy, "maxHp", "maxHealth"),
                ["block"] = ReadInt(enemy, "block", "currentBlock", "shield"),
                ["buffs"] = Array.Empty<object>(),
                ["debuffs"] = Array.Empty<object>(),
                ["powers_unknown"] = BuildPowersUnknown(enemy),
                ["intent"] = BuildIntent(enemy)
            });
            index++;
        }

        return result;
    }

    private static List<Dictionary<string, object?>> BuildPowersUnknown(object? owner)
    {
        object? powersSource = FindMemberValue(
            owner,
            "powers",
            "powerList",
            "statusEffects",
            "statusEffectList",
            "effects",
            "buffs",
            "debuffs");

        List<Dictionary<string, object?>> powers = new();
        foreach (object power in EnumerateObjects(powersSource))
        {
            string fallbackName = GetReadableName(power);
            powers.Add(new Dictionary<string, object?>
            {
                ["id"] = ReadString(power, "id", "powerId", "key") ?? fallbackName,
                ["name"] = ReadString(power, "name", "displayName", "title") ?? fallbackName,
                ["amount"] = ReadInt(power, "amount", "stacks", "stack", "value", "counter", "count"),
                ["type_name"] = power.GetType().FullName ?? power.GetType().Name
            });
        }

        return powers;
    }

    private static Dictionary<string, object?> BuildIntent(object enemy)
    {
        object? intent = FindMemberValue(enemy, "intent", "currentIntent", "move", "currentMove", "nextMove");
        int? damage = ReadInt(intent, "damage", "Damage", "baseDamage", "BaseDamage", "damageCalc", "DamageCalc", "attackDamage")
            ?? ReadInt(enemy, "intentDamage", "moveDamage", "damage", "Damage");
        int? hits = ReadInt(intent, "hits", "Hits", "repeats", "Repeats", "times", "attackCount")
            ?? ReadInt(enemy, "intentHits", "moveHits", "hits", "repeats");
        int? totalDamage = ReadInt(intent, "totalDamage", "TotalDamage")
            ?? ReadInt(enemy, "intentTotalDamage", "moveTotalDamage", "totalDamage");

        if (totalDamage is null && damage is not null && hits is not null)
        {
            totalDamage = damage.Value * hits.Value;
        }

        return new Dictionary<string, object?>
        {
            ["raw_intent"] = intent is null ? null : GetReadableName(intent),
            ["damage"] = damage,
            ["hits"] = hits,
            ["total_damage"] = totalDamage,
            ["damage_is_adjusted"] = null,
            ["damage_source"] = damage is null && hits is null && totalDamage is null
                ? "unavailable"
                : "reflected_candidate_fields"
        };
    }

    private static List<Dictionary<string, object?>> BuildRelics(object? relicsSource, ObjectGraph graph)
    {
        IEnumerable<object> relics = EnumerateObjects(relicsSource).ToList();
        if (!relics.Any())
        {
            relics = graph.Nodes
                .Select(node => node.Value)
                .Where(value => value is not null && value.GetType().Name.Contains("Relic", StringComparison.OrdinalIgnoreCase))
                .Cast<object>()
                .Distinct(ReferenceEqualityComparer.Instance);
        }

        List<Dictionary<string, object?>> result = new();
        foreach (object relic in relics)
        {
            string fallbackName = GetReadableName(relic);
            result.Add(new Dictionary<string, object?>
            {
                ["id"] = ReadString(relic, "id", "relicId", "key") ?? fallbackName,
                ["name"] = ReadString(relic, "name", "displayName", "title") ?? fallbackName
            });
        }

        return result;
    }

    private static string GetOutputPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "SlayTheSpire2", "SpireMind", "combat_state.json");
    }

    private static object? FindFirst(ObjectGraph graph, params string[] hints)
    {
        return graph.Nodes
            .Where(node => ContainsAny(node.Path, hints) || ContainsAny(node.Value?.GetType().Name, hints))
            .Select(node => node.Value)
            .FirstOrDefault(value => value is not null && !IsScalar(value.GetType()));
    }

    private static object? FindFirstCombatState(ObjectGraph graph)
    {
        return graph.Nodes
            .Select(node => node.Value)
            .FirstOrDefault(value =>
            {
                if (value is null || IsScalar(value.GetType()))
                {
                    return false;
                }

                string typeName = value.GetType().FullName ?? value.GetType().Name;
                return typeName.Contains("CombatState", StringComparison.OrdinalIgnoreCase)
                    && !typeName.Contains("PlayerCombatState", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static object? FindFirstEnumerable(ObjectGraph graph, params string[] hints)
    {
        return graph.Nodes
            .Where(node => ContainsAny(node.Path, hints))
            .Select(node => node.Value)
            .FirstOrDefault(value => value is not null && value is IEnumerable && value is not string);
    }

    private static object? FindMemberValue(object? source, params string[] names)
    {
        if (source is null)
        {
            return null;
        }

        foreach (MemberInfo member in GetReadableMembers(source.GetType()))
        {
            if (!names.Any(name => member.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            object? value = ReadMember(source, member);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static int? ReadInt(object? source, params string[] names)
    {
        object? value = FindMemberValue(source, names);
        if (value is null)
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

    private static bool? ReadBool(object? source, params string[] names)
    {
        object? value = FindMemberValue(source, names);
        if (value is bool boolean)
        {
            return boolean;
        }

        return null;
    }

    private static string? ReadString(object? source, params string[] names)
    {
        object? value = FindMemberValue(source, names);
        return value switch
        {
            null => null,
            string text => text,
            _ => value.ToString()
        };
    }

    private static IEnumerable<object> EnumerateObjects(object? source)
    {
        if (source is null || source is string)
        {
            yield break;
        }

        if (source is IEnumerable enumerable)
        {
            foreach (object? item in enumerable)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }
        }
        else
        {
            yield return source;
        }
    }

    private static IEnumerable<MemberInfo> GetReadableMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (FieldInfo field in type.GetFields(flags))
        {
            yield return field;
        }

        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            if (property.GetIndexParameters().Length == 0)
            {
                yield return property;
            }
        }
    }

    private static object? ReadMember(object source, MemberInfo member)
    {
        try
        {
            return member switch
            {
                FieldInfo field => field.GetValue(source),
                PropertyInfo property when property.GetMethod is not null => property.GetValue(source),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool ContainsAny(string? text, params string[] hints)
    {
        return !string.IsNullOrWhiteSpace(text)
            && hints.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEnemyLike(Type type)
    {
        string name = type.FullName ?? type.Name;
        return name.Contains("Enemy", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Monster", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsScalar(Type type)
    {
        return type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset);
    }

    private static string GetReadableName(object value)
    {
        string? text = ReadString(value, "name", "displayName", "id", "key");
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return value.GetType().Name;
    }

    private sealed class ObjectGraph
    {
        private ObjectGraph(List<ObjectNode> nodes)
        {
            Nodes = nodes;
        }

        public List<ObjectNode> Nodes { get; }

        public static ObjectGraph Collect(object root, int maxDepth, int maxObjects)
        {
            return Collect(new[] { root }, maxDepth, maxObjects);
        }

        public static ObjectGraph Collect(IEnumerable<object> roots, int maxDepth, int maxObjects)
        {
            List<ObjectNode> nodes = new();
            Queue<ObjectNode> queue = new();
            HashSet<object> visited = new(ReferenceEqualityComparer.Instance);

            int rootIndex = 0;
            foreach (object root in roots)
            {
                if (visited.Contains(root))
                {
                    continue;
                }

                queue.Enqueue(new ObjectNode($"root[{rootIndex}]", root, 0));
                visited.Add(root);
                rootIndex++;
            }

            while (queue.Count > 0 && nodes.Count < maxObjects)
            {
                ObjectNode current = queue.Dequeue();
                nodes.Add(current);

                if (current.Value is null || current.Depth >= maxDepth || IsScalar(current.Value.GetType()))
                {
                    continue;
                }

                foreach (MemberInfo member in GetReadableMembers(current.Value.GetType()))
                {
                    object? value = ReadMember(current.Value, member);
                    if (value is null || IsScalar(value.GetType()))
                    {
                        continue;
                    }

                    string path = $"{current.Path}.{member.Name}";
                    if (value is IEnumerable enumerable and not string)
                    {
                        queue.Enqueue(new ObjectNode(path, value, current.Depth + 1));
                        int index = 0;
                        foreach (object? item in enumerable)
                        {
                            if (item is null || IsScalar(item.GetType()) || visited.Contains(item))
                            {
                                continue;
                            }

                            visited.Add(item);
                            queue.Enqueue(new ObjectNode($"{path}[{index}]", item, current.Depth + 1));
                            index++;
                        }
                    }
                    else if (!visited.Contains(value))
                    {
                        visited.Add(value);
                        queue.Enqueue(new ObjectNode(path, value, current.Depth + 1));
                    }
                }
            }

            return new ObjectGraph(nodes);
        }
    }

    private sealed record ObjectNode(string Path, object? Value, int Depth);
}
