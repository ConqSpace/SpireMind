using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    private const int MaxSafeJsonDepth = 12;
    private const int MaxSafeJsonListItems = 200;
    private static readonly bool EnableUnsafeUiHandDebug = false;
    private static readonly string[] PowerSourceMemberNames =
    {
        "powers",
        "powerList",
        "statusEffects",
        "statusEffectList",
        "effects",
        "buffs",
        "debuffs"
    };

    private static readonly string[] BuffPowerKeywords =
    {
        "strength",
        "dexterity",
        "artifact",
        "barricade",
        "buffer",
        "focus",
        "regen",
        "regeneration",
        "ritual",
        "metallicize",
        "plated",
        "thorns",
        "intangible",
        "invincible"
    };

    private static readonly string[] DebuffPowerKeywords =
    {
        "vulnerable",
        "weak",
        "frail",
        "poison",
        "lockon",
        "lock_on",
        "constricted",
        "entangled",
        "hex",
        "confused",
        "shackled",
        "no_draw",
        "nodraw",
        "drawdown"
    };

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
    private static readonly object ExportGate = new();
    private static object? pendingExportRoot;
    private static long pendingExportRequestedAtMs;
    private static int pendingExportCount;
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

    internal static object? GetLatestRuntimePlayer()
    {
        return recentPlayer;
    }

    internal static CombatExportProbe ForceExportFromCombatManager(string reason)
    {
        try
        {
            object? combatManager = GetCombatManagerInstance();
            bool isInProgress = ReadBool(combatManager, "IsInProgress") == true;
            object? combatState = ReadCombatManagerDebugState(combatManager);
            if (combatState is null)
            {
                return new CombatExportProbe(false, false, false, false, false, null, null, 0, 0, "combat_state_null");
            }

            RememberObservedRoot(combatState);
            Dictionary<string, object?>? state = ExportNow(combatState, force: true, tickAfterExport: false);
            return BuildExportProbe(state, isInProgress, "forced:" + reason);
        }
        catch (Exception exception)
        {
            Logger.Warning($"강제 전투 상태 export 중 예외가 발생했습니다. 게임 진행은 멈추지 않습니다. {exception.GetType().Name}: {exception.Message}");
            return new CombatExportProbe(false, false, false, false, false, null, null, 0, 0, exception.GetType().Name);
        }
    }

    internal static CombatExportProbe ReadLatestStateFileProbe(string reason)
    {
        string outputPath = GetOutputPath();
        try
        {
            if (!File.Exists(outputPath))
            {
                return new CombatExportProbe(false, false, false, false, false, null, null, 0, 0, "state_file_missing");
            }

            string json = File.ReadAllText(outputPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new CombatExportProbe(false, false, false, false, false, null, null, 0, 0, "state_file_empty");
            }

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            int? hp = ReadJsonInt(root, "player", "hp");
            int? energy = ReadJsonInt(root, "player", "energy");
            int handCount = ReadJsonArrayCount(root, "piles", "hand");
            int enemyCount = ReadJsonArrayCount(root, "enemies");
            bool hasPlayer = hp is not null && energy is not null;
            bool hasHand = handCount >= 1;
            bool hasEnemies = enemyCount >= 1;
            return new CombatExportProbe(false, true, hasPlayer, hasHand, hasEnemies, hp, energy, handCount, enemyCount, "file:" + reason);
        }
        catch (Exception exception)
        {
            Logger.Warning($"최신 combat_state.json 준비 판정 중 예외가 발생했습니다. 게임 진행은 멈추지 않습니다. path={outputPath}, {exception.GetType().Name}: {exception.Message}");
            return new CombatExportProbe(false, false, false, false, false, null, null, 0, 0, "state_file_" + exception.GetType().Name);
        }
    }

    internal static bool HasPendingExport
    {
        get
        {
            lock (ExportGate)
            {
                return pendingExportRoot is not null;
            }
        }
    }

    internal static int PendingExportCount
    {
        get
        {
            lock (ExportGate)
            {
                return pendingExportCount;
            }
        }
    }

    internal static long PendingExportAgeMs
    {
        get
        {
            lock (ExportGate)
            {
                return pendingExportRoot is null ? 0 : ExportTimer.ElapsedMilliseconds - pendingExportRequestedAtMs;
            }
        }
    }

    internal static void FlushPendingExportIfReady()
    {
        object? root;
        long nowMs = ExportTimer.ElapsedMilliseconds;
        lock (ExportGate)
        {
            if (pendingExportRoot is null || nowMs - lastExportAtMs < ExportIntervalMs)
            {
                return;
            }

            root = pendingExportRoot;
            lastExportAtMs = nowMs;
        }

        try
        {
            ExportNow(root, force: false, tickAfterExport: false);
        }
        catch (Exception exception)
        {
            Logger.Warning($"보류된 전투 상태 export 처리 중 예외가 발생했습니다. 다음 틱에서 다시 시도합니다. {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void TryExport(object combatRoot)
    {

        long nowMs = ExportTimer.ElapsedMilliseconds;
        if (nowMs - lastExportAtMs < ExportIntervalMs)
        {
            RememberPendingExport(combatRoot, nowMs);
            return;
        }

        lastExportAtMs = nowMs;

        try
        {
            Dictionary<string, object?> state = BuildState(combatRoot);
            string stateFingerprint = ComputeStateFingerprint(state);
            state["state_id"] = $"combat_{stateFingerprint[..16]}";
            state["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Dictionary<string, object?> safeState = NormalizeStateForJson(state, "combat_state");
            string json = JsonSerializer.Serialize(safeState, JsonOptions);
            if (stateFingerprint == lastStateFingerprint)
            {
                CombatActionRuntimeContext.UpdateFromExport(combatRoot, json);
                CombatStateBridgePoster.PostedStateSnapshot? postedState = CombatStateBridgePoster.GetLatestPostedState();
                if (postedState is null || postedState.StateId != safeState["state_id"]?.ToString())
                {
                    CombatStateBridgePoster.TryPost(json);
                }

                ClearPendingExport();
                CombatActionExecutor.TickMainThread();
                return;
            }

            lastStateFingerprint = stateFingerprint;
            string outputPath = GetOutputPath();
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, json);
            CombatActionRuntimeContext.UpdateFromExport(combatRoot, json);
            CombatStateBridgePoster.TryPost(json);
            ClearPendingExport();
            CombatActionExecutor.TickMainThread();

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

    private static void RememberPendingExport(object combatRoot, long nowMs)
    {
        lock (ExportGate)
        {
            pendingExportRoot = combatRoot;
            pendingExportRequestedAtMs = nowMs;
            pendingExportCount++;
        }
    }

    private static void ClearPendingExport()
    {
        lock (ExportGate)
        {
            pendingExportRoot = null;
            pendingExportRequestedAtMs = 0;
            pendingExportCount = 0;
        }
    }

    private static Dictionary<string, object?>? ExportNow(object combatRoot, bool force, bool tickAfterExport = true)
    {
        Dictionary<string, object?> state = BuildState(combatRoot);
        string stateFingerprint = ComputeStateFingerprint(state);
        state["state_id"] = $"combat_{stateFingerprint[..16]}";
        state["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Dictionary<string, object?> safeState = NormalizeStateForJson(state, "combat_state");
        string json = JsonSerializer.Serialize(safeState, JsonOptions);
        if (!force && stateFingerprint == lastStateFingerprint)
        {
            CombatActionRuntimeContext.UpdateFromExport(combatRoot, json);
            CombatStateBridgePoster.PostedStateSnapshot? postedState = CombatStateBridgePoster.GetLatestPostedState();
            if (postedState is null || postedState.StateId != safeState["state_id"]?.ToString())
            {
                CombatStateBridgePoster.TryPost(json);
            }

            ClearPendingExport();
            if (tickAfterExport)
            {
                CombatActionExecutor.TickMainThread();
            }

            return safeState;
        }

        lastStateFingerprint = stateFingerprint;
        string outputPath = GetOutputPath();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, json);
        CombatActionRuntimeContext.UpdateFromExport(combatRoot, json);
        CombatStateBridgePoster.TryPost(json);
        ClearPendingExport();
        if (tickAfterExport)
        {
            CombatActionExecutor.TickMainThread();
        }

        if (!hasLoggedOutputPath)
        {
            hasLoggedOutputPath = true;
            Logger.Info($"combat_state.v1 출력 경로: {outputPath}");
        }

        return safeState;
    }

    private static CombatExportProbe BuildExportProbe(Dictionary<string, object?>? state, bool isInProgress, string reason)
    {
        if (state is null)
        {
            return new CombatExportProbe(isInProgress, false, false, false, false, null, null, 0, 0, reason);
        }

        Dictionary<string, object?> player = ReadDictionary(state, "player");
        Dictionary<string, object?> piles = ReadDictionary(state, "piles");
        int? hp = ReadDictionaryInt(player, "hp");
        int? energy = ReadDictionaryInt(player, "energy");
        int handCount = ReadDictionaryList(piles, "hand").Count;
        int enemyCount = ReadDictionaryList(state, "enemies").Count;
        bool hasPlayer = hp is not null && energy is not null;
        bool hasHand = handCount > 0;
        bool hasEnemies = enemyCount > 0;
        return new CombatExportProbe(isInProgress, true, hasPlayer, hasHand, hasEnemies, hp, energy, handCount, enemyCount, reason);
    }

    private static object? GetCombatManagerInstance()
    {
        return GetStaticPropertyValue("MegaCrit.Sts2.Core.Combat.CombatManager", "Instance");
    }

    private static object? ReadCombatManagerDebugState(object? combatManager)
    {
        return TryInvokeMethod(combatManager, "DebugOnlyGetState");
    }

    private static void RememberObservedRoot(object instance)
    {
        string typeName = instance.GetType().FullName ?? instance.GetType().Name;
        RememberObservedType(typeName);

        if (IsRuntimePlayerType(typeName))
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
        recentPlayer ??= FindFirstRuntimePlayer(shallowGraph);
        recentCombatState ??= FindFirstCombatState(shallowGraph);
        recentPlayerCombatState ??= FindFirst(shallowGraph, "PlayerCombatState");
    }

    private static bool IsRuntimePlayerType(string typeName)
    {
        return (typeName.EndsWith(".Player", StringComparison.OrdinalIgnoreCase)
                || typeName.Contains(".Entities.Players.Player", StringComparison.OrdinalIgnoreCase))
            && !typeName.Contains("PlayerCombatState", StringComparison.OrdinalIgnoreCase);
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
        object? managerCombatState = ReadCombatManagerDebugState(GetCombatManagerInstance());
        if (managerCombatState is not null)
        {
            RememberObservedRoot(managerCombatState);
        }

        List<object> roots = GetExportRoots(currentRoot, managerCombatState);
        ObjectGraph graph = ObjectGraph.Collect(roots, MaxSearchDepth, MaxVisitedObjects);
        object? combatRoot = managerCombatState ?? recentCombatState ?? currentRoot;
        object? player = ResolveRuntimePlayer(combatRoot, graph)
            ?? recentPlayer
            ?? recentPlayerCombatState
            ?? FindFirst(graph, "Player", "PlayerCombatState");
        object? enemiesSource = FindMemberValue(combatRoot, "enemies", "monsters", "creatures")
            ?? FindFirstEnumerable(graph, "enemies", "monsters");
        object? relicsSource = FindMemberValue(combatRoot, "relics")
            ?? FindMemberValue(player, "relics");

        CardMovementObserver.ObserveContext(combatRoot, player);

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
            ["debug"] = BuildDebug(currentRoot, combatRoot, player, enemiesSource, graph)
        };
    }

    private static List<object> GetExportRoots(object currentRoot, object? managerCombatState)
    {
        List<object> roots = new();
        AddRoot(roots, managerCombatState);
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

    private static object? ResolveRuntimePlayer(object? combatRoot, ObjectGraph graph)
    {
        object? players = FindMemberValue(combatRoot, "Players", "players", "_players");
        object? directPlayer = EnumerateObjects(players)
            .FirstOrDefault(value => IsRuntimePlayerType(value.GetType().FullName ?? value.GetType().Name));
        if (directPlayer is not null)
        {
            recentPlayer = directPlayer;
            object? playerCombatState = FindMemberValue(directPlayer, "PlayerCombatState", "playerCombatState", "_playerCombatState");
            if (playerCombatState is not null)
            {
                recentPlayerCombatState = playerCombatState;
            }

            return directPlayer;
        }

        return FindFirstRuntimePlayer(graph);
    }

    private static Dictionary<string, object?> BuildDebug(
        object currentRoot,
        object combatRoot,
        object? player,
        object? enemiesSource,
        ObjectGraph graph)
    {
        object? handSource = FindPile(combatRoot, player, graph, "hand", "cardsInHand");
        List<object> modelHandCards = EnumerateCards(handSource).ToList();
        List<object> observedHandCards = CardMovementObserver.GetObservedHandCards().ToList();
        List<object> mergedHandCards = MergeHandCardObjects(modelHandCards, observedHandCards);
        object? firstCard = modelHandCards.FirstOrDefault();
        object? firstEnemy = EnumerateObjects(enemiesSource).FirstOrDefault()
            ?? graph.Nodes
                .Select(node => node.Value)
                .FirstOrDefault(value => value is not null && IsEnemyLike(value.GetType()));
        object? firstEnemyIntent = FindIntentCandidate(firstEnemy);

        return new Dictionary<string, object?>
        {
            ["root_type"] = currentRoot.GetType().FullName ?? currentRoot.GetType().Name,
            ["last_observed_types"] = LastObservedTypes.ToArray(),
            ["first_card_members"] = BuildMemberDebug(firstCard),
            ["first_card_child_members"] = BuildChildMemberDebug(firstCard, "cardStats", "_cardStats", "cardInfo", "_cardInfo", "baseCard", "_baseCard"),
            ["first_card_dynamic_vars"] = BuildDynamicVarsDebug(firstCard),
            ["first_enemy_members"] = BuildMemberDebug(firstEnemy),
            ["first_enemy_intent_members"] = BuildMemberDebug(firstEnemyIntent),
            ["first_enemy_intent_child_members"] = BuildChildMemberDebug(firstEnemyIntent, "move", "_move", "Move", "nextMove", "_nextMove", "NextMove", "damageCalc", "_damageCalc", "repeatCalc", "_repeatCalc"),
            ["model_hand"] = BuildHandMergeDebug(modelHandCards),
            ["observed_hand"] = BuildHandMergeDebug(observedHandCards),
            ["merged_hand"] = BuildHandMergeDebug(mergedHandCards),
            ["pile_candidates"] = BuildPileDebug(combatRoot, player, graph),
            ["intent_like_graph_nodes"] = BuildIntentGraphDebug(graph),
            ["room_like_graph_nodes"] = BuildRoomGraphDebug(graph)
        };
    }

    private static List<Dictionary<string, object?>> BuildPileDebug(object combatRoot, object? player, ObjectGraph graph)
    {
        List<Dictionary<string, object?>> result = new();
        object? modelHandSource = FindPile(combatRoot, player, graph, "hand", "cardsInHand");
        List<object> modelHandCards = EnumerateCards(modelHandSource).Take(12).ToList();
        result.Add(BuildPileCandidateDebug("model_hand", modelHandSource, modelHandCards));
        result.Add(BuildUiHandDebug(graph));

        foreach ((string label, string[] names) in new[]
        {
            ("draw", new[] { "drawPile", "draw_pile", "draw" }),
            ("discard", new[] { "discardPile", "discard_pile", "discard" }),
            ("exhaust", new[] { "exhaustPile", "exhaust_pile", "exhaust" })
        })
        {
            object? source = FindPile(combatRoot, player, graph, names);
            List<object> cards = EnumerateCards(source).Take(12).ToList();
            result.Add(BuildPileCandidateDebug(label, source, cards));
        }

        return result;
    }

    private static Dictionary<string, object?> BuildPileCandidateDebug(string label, object? source, List<object> cards)
    {
        return new Dictionary<string, object?>
        {
            ["pile"] = label,
            ["source_type"] = source?.GetType().FullName ?? source?.GetType().Name,
            ["count_sample"] = cards.Count,
            ["names_sample"] = cards
                .Take(8)
                .Select(card => ReadCardName(card, FindMemberValue(card, "Model", "model", "_model", "cardModel", "_cardModel")))
                .ToArray()
        };
    }

    private static Dictionary<string, object?> BuildHandMergeDebug(List<object> cards)
    {
        return new Dictionary<string, object?>
        {
            ["count"] = cards.Count,
            ["names"] = cards
                .Take(10)
                .Select(card => ReadCardName(card, FindMemberValue(card, "Model", "model", "_model", "cardModel", "_cardModel")))
                .ToArray()
        };
    }

    private static Dictionary<string, object?> BuildUiHandDebug(ObjectGraph graph)
    {
        if (!EnableUnsafeUiHandDebug)
        {
            return new Dictionary<string, object?>
            {
                ["pile"] = "ui_hand",
                ["source_type"] = null,
                ["count_sample"] = 0,
                ["names_sample"] = Array.Empty<string?>(),
                ["disabled_reason"] = "전투 진입 크래시 방지를 위해 UI 노드 직접 접근을 기본 비활성화했습니다."
            };
        }

        try
        {
            List<object> uiHandCards = FindUiHandCards(graph).Take(12).ToList();
            return BuildPileCandidateDebug("ui_hand", uiHandCards, uiHandCards);
        }
        catch (Exception exception)
        {
            return new Dictionary<string, object?>
            {
                ["pile"] = "ui_hand",
                ["source_type"] = null,
                ["count_sample"] = 0,
                ["names_sample"] = Array.Empty<string?>(),
                ["error"] = $"{exception.GetType().Name}: {exception.Message}"
            };
        }
    }

    private static List<Dictionary<string, object?>> BuildIntentGraphDebug(ObjectGraph graph)
    {
        return graph.Nodes
            .Where(node => node.Value is not null && IsIntentOrMoveLike(node.Value))
            .Take(12)
            .Select(node => new Dictionary<string, object?>
            {
                ["path"] = node.Path,
                ["type_name"] = node.Value!.GetType().FullName ?? node.Value.GetType().Name,
                ["members"] = BuildMemberDebug(node.Value)
            })
            .ToList();
    }

    private static bool IsIntentOrMoveLike(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return ContainsAny(typeName, "Intent", "Move")
            && !ContainsAny(typeName, "Tween", "Animation", "StateMachine", "Input", "Command", "History");
    }

    private static List<Dictionary<string, object?>> BuildRoomGraphDebug(ObjectGraph graph)
    {
        return graph.Nodes
            .Where(node => node.Value is not null && IsRoomOrCombatNodeLike(node.Value))
            .Take(8)
            .Select(node => new Dictionary<string, object?>
            {
                ["path"] = node.Path,
                ["type_name"] = node.Value!.GetType().FullName ?? node.Value.GetType().Name,
                ["members"] = BuildMemberDebug(node.Value)
            })
            .ToList();
    }

    private static bool IsRoomOrCombatNodeLike(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return ContainsAny(typeName, "NCombatRoom", "CombatRoom", "IntentContainer", "CreatureNode");
    }

    private static object? FindIntentCandidate(object? enemy)
    {
        return FindMemberValue(
            enemy,
            "intent",
            "_intent",
            "Intent",
            "currentIntent",
            "_currentIntent",
            "move",
            "_move",
            "Move",
            "currentMove",
            "_currentMove",
            "nextMove",
            "_nextMove",
            "NextMove",
            "cardIntent",
            "_cardIntent",
            "CardIntent");
    }

    private static List<Dictionary<string, object?>> BuildChildMemberDebug(object? source, params string[] childNames)
    {
        List<Dictionary<string, object?>> result = new();
        foreach (string childName in childNames)
        {
            object? child = FindMemberValue(source, childName);
            if (child is null)
            {
                continue;
            }

            result.Add(new Dictionary<string, object?>
            {
                ["name"] = childName,
                ["type_name"] = child.GetType().FullName ?? child.GetType().Name,
                ["members"] = BuildMemberDebug(child)
            });
        }

        return result;
    }

    private static List<Dictionary<string, object?>> BuildDynamicVarsDebug(object? card)
    {
        return EnumerateDynamicVars(card)
            .Take(12)
            .Select(dynamicVar => new Dictionary<string, object?>
            {
                ["type_name"] = dynamicVar.GetType().FullName ?? dynamicVar.GetType().Name,
                ["summary"] = FormatDebugValue(dynamicVar),
                ["members"] = BuildMemberDebug(dynamicVar)
            })
            .ToList();
    }

    private static List<Dictionary<string, object?>> BuildMemberDebug(object? source)
    {
        List<Dictionary<string, object?>> members = new();
        if (source is null)
        {
            return members;
        }

        foreach (MemberInfo member in GetReadableMembers(source.GetType()).Take(80))
        {
            object? value = ReadMember(source, member);
            members.Add(new Dictionary<string, object?>
            {
                ["name"] = member.Name,
                ["member_kind"] = member.MemberType.ToString(),
                ["value_type"] = value is null ? null : value.GetType().FullName ?? value.GetType().Name,
                ["value"] = FormatDebugValue(value)
            });
        }

        return members;
    }

    private static object? FormatDebugValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        Type type = value.GetType();
        if (IsScalar(type))
        {
            return value;
        }

        if (value is IEnumerable enumerable and not string)
        {
            int count = 0;
            foreach (object? _ in enumerable)
            {
                count++;
                if (count > 20)
                {
                    break;
                }
            }

            return $"Enumerable<{type.FullName ?? type.Name}> count_sample={count}";
        }

        string? text = ReadObjectString(value);
        return string.IsNullOrWhiteSpace(text)
            ? type.FullName ?? type.Name
            : text;
    }

    private static string ComputeStateFingerprint(Dictionary<string, object?> state)
    {
        Dictionary<string, object?> comparableState = state
            .Where(pair => pair.Key is not ("state_id" or "exported_at_ms" or "debug"))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        Dictionary<string, object?> safeComparableState = NormalizeStateForJson(comparableState, "combat_state_fingerprint");
        string json = JsonSerializer.Serialize(safeComparableState, JsonOptions);
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static Dictionary<string, object?> NormalizeStateForJson(Dictionary<string, object?> state, string path)
    {
        object? normalized = NormalizeJsonValue(state, path, 0);
        if (normalized is Dictionary<string, object?> dictionary)
        {
            return dictionary;
        }

        Logger.Warning($"{path} 정규화 결과가 JSON 객체가 아닙니다. 빈 상태로 대체합니다.");
        return new Dictionary<string, object?>();
    }

    private static object? NormalizeJsonValue(object? value, string path, int depth)
    {
        if (value is null)
        {
            return null;
        }

        if (depth > MaxSafeJsonDepth)
        {
            Logger.Warning($"{path} 값이 JSON 정규화 깊이 제한을 넘었습니다. 문자열로 대체합니다.");
            return "[max_depth]";
        }

        if (value is string or bool)
        {
            return value;
        }

        if (value is char character)
        {
            return character.ToString();
        }

        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            return value;
        }

        if (value is Enum)
        {
            return value.ToString();
        }

        if (value is DateTime dateTime)
        {
            return dateTime.ToString("O");
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            return dateTimeOffset.ToString("O");
        }

        if (value is Delegate)
        {
            Logger.Warning($"{path} 필드에 Delegate가 있어 문자열 설명으로 대체했습니다. type={value.GetType().FullName}");
            return $"[unsupported_delegate:{value.GetType().FullName ?? value.GetType().Name}]";
        }

        if (value is IntPtr or UIntPtr)
        {
            Logger.Warning($"{path} 필드에 포인터 값이 있어 문자열 설명으로 대체했습니다. type={value.GetType().FullName}");
            return $"[unsupported_pointer:{value.GetType().FullName ?? value.GetType().Name}]";
        }

        if (value is IDictionary dictionary)
        {
            Dictionary<string, object?> result = new(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                string key = entry.Key?.ToString() ?? "null";
                result[key] = NormalizeJsonValue(entry.Value, $"{path}.{key}", depth + 1);
            }

            return result;
        }

        if (value is IEnumerable enumerable and not string)
        {
            List<object?> result = new();
            int index = 0;
            foreach (object? item in enumerable)
            {
                if (index >= MaxSafeJsonListItems)
                {
                    Logger.Warning($"{path} 목록이 {MaxSafeJsonListItems}개를 넘어 나머지를 잘랐습니다.");
                    break;
                }

                result.Add(NormalizeJsonValue(item, $"{path}[{index}]", depth + 1));
                index++;
            }

            return result;
        }

        // 원시 게임 객체와 Godot Object 계열은 JsonSerializer에 넘기지 않는다.
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        Logger.Warning($"{path} 필드에 JSON 안전 타입이 아닌 객체가 있어 문자열 설명으로 대체했습니다. type={typeName}");
        return $"[unsupported_object:{typeName}]";
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
        object? playerCombatState = player is not null && IsPlayerCombatStateType(player.GetType())
            ? player
            : FindMemberValue(player, "PlayerCombatState", "playerCombatState", "_playerCombatState")
                ?? recentPlayerCombatState;
        object? runtimeCreature = FindMemberValue(player, "Creature", "creature", "_creature")
            ?? FindMemberValue(recentPlayer, "Creature", "creature", "_creature");
        PowerGroups powerGroups = BuildPowerGroups(player, playerCombatState, recentPlayer, runtimeCreature);

        return new Dictionary<string, object?>
        {
            ["id"] = "player_0",
            ["hp"] = ReadFirstInt(new[] { player, recentPlayer, runtimeCreature }, "hp", "_hp", "currentHp", "_currentHp", "currentHealth", "_currentHealth", "health", "_health"),
            ["max_hp"] = ReadFirstInt(new[] { player, recentPlayer, runtimeCreature }, "maxHp", "_maxHp", "maxHealth", "_maxHealth"),
            ["block"] = ReadFirstInt(new[] { player, recentPlayer, runtimeCreature }, "block", "_block", "currentBlock", "_currentBlock", "shield", "_shield"),
            ["energy"] = ReadFirstInt(new[] { playerCombatState, player, recentPlayer }, "energy", "_energy", "currentEnergy", "_currentEnergy"),
            ["max_energy"] = ReadFirstInt(new[] { playerCombatState, player, recentPlayer }, "maxEnergy", "_maxEnergy", "energyMax", "_energyMax"),
            ["gold"] = ReadFirstInt(new[] { player, recentPlayer }, "gold", "_gold", "currentGold", "_currentGold"),
            ["buffs"] = powerGroups.Buffs,
            ["debuffs"] = powerGroups.Debuffs,
            ["powers_unknown"] = powerGroups.Unknown
        };
    }

    private static Dictionary<string, object?> BuildPiles(object combatRoot, object? player, ObjectGraph graph)
    {
        return new Dictionary<string, object?>
        {
            ["hand"] = BuildCards("hand", SelectMergedHandSource(combatRoot, player, graph)),
            ["draw_pile"] = BuildCards("draw", FindPile(combatRoot, player, graph, "drawPile", "draw_pile", "draw")),
            ["discard_pile"] = BuildCards("discard", FindPile(combatRoot, player, graph, "discardPile", "discard_pile", "discard")),
            ["exhaust_pile"] = BuildCards("exhaust", FindPile(combatRoot, player, graph, "exhaustPile", "exhaust_pile", "exhaust"))
        };
    }

    private static List<object> SelectMergedHandSource(object combatRoot, object? player, ObjectGraph graph)
    {
        List<object> modelHandCards = EnumerateCards(SelectHandSource(combatRoot, player, graph)).ToList();
        List<object> observedHandCards = CardMovementObserver.GetObservedHandCards().ToList();
        return MergeHandCardObjects(modelHandCards, observedHandCards);
    }

    private static List<object> MergeHandCardObjects(List<object> modelHandCards, List<object> observedHandCards)
    {
        if (modelHandCards.Count >= observedHandCards.Count)
        {
            return modelHandCards;
        }

        List<object> merged = new(modelHandCards);
        HashSet<object> seenReferences = new(modelHandCards, ReferenceEqualityComparer.Instance);
        HashSet<string> seenRuntimeIds = modelHandCards
            .Select(BuildCardRuntimeMergeKey)
            .ToHashSet(StringComparer.Ordinal);

        foreach (object observedCard in observedHandCards)
        {
            string runtimeId = BuildCardRuntimeMergeKey(observedCard);
            if (!seenReferences.Add(observedCard) || !seenRuntimeIds.Add(runtimeId))
            {
                continue;
            }

            merged.Add(observedCard);
        }

        return merged;
    }

    private static object? SelectHandSource(object combatRoot, object? player, ObjectGraph graph)
    {
        // 전투 진입 중 Godot UI 노드에 접근하면 준비 전 생명주기와 충돌할 수 있습니다.
        // 안정성을 우선해 실제 추출은 모델 더미만 사용하고, UI 손패는 별도 안전 경로가 확인될 때 다시 켭니다.
        return FindPile(combatRoot, player, graph, "hand", "cardsInHand");
    }

    private static IEnumerable<object> FindUiHandCards(ObjectGraph graph)
    {
        foreach (object holder in FindUiHandHolders(graph))
        {
            object? card = FindMemberValue(holder, "CardModel", "cardModel")
                ?? FindMemberValue(FindMemberValue(holder, "CardNode", "cardNode"), "Model", "model", "_model");
            if (card is not null)
            {
                yield return card;
            }
        }
    }

    private static IEnumerable<object> FindUiHandHolders(ObjectGraph graph)
    {
        HashSet<object> yielded = new(ReferenceEqualityComparer.Instance);

        foreach (object hand in FindUiHandNodes(graph))
        {
            foreach (object holder in EnumerateObjects(FindMemberValue(hand, "ActiveHolders", "Holders")))
            {
                if (IsHandCardHolderLike(holder) && yielded.Add(holder))
                {
                    yield return holder;
                }
            }

            object? selectedContainer = FindMemberValue(hand, "_selectedHandCardContainer", "SelectedHandCardContainer", "selectedHandCardContainer");
            foreach (object holder in EnumerateObjects(FindMemberValue(selectedContainer, "Holders")))
            {
                if (IsHandCardHolderLike(holder) && yielded.Add(holder))
                {
                    yield return holder;
                }
            }
        }
    }

    private static IEnumerable<object> FindUiHandNodes(ObjectGraph graph)
    {
        object? staticInstance = GetStaticPropertyValue("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand", "Instance");
        if (staticInstance is not null)
        {
            yield return staticInstance;
        }

        foreach (object? handNode in graph.Nodes
            .Select(node => node.Value)
            .Where(value => value is not null && IsPlayerHandNodeLike(value.GetType())))
        {
            if (handNode is not null)
            {
                yield return handNode;
            }
        }
    }

    private static object? GetStaticPropertyValue(string typeName, string propertyName)
    {
        Type? type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(typeName, throwOnError: false))
            .FirstOrDefault(candidate => candidate is not null);
        if (type is null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            return type.GetProperty(propertyName, flags)?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPlayerHandNodeLike(Type type)
    {
        string typeName = type.FullName ?? type.Name;
        return typeName.Contains("NPlayerHand", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHandCardHolderLike(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return typeName.Contains("HandCardHolder", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("NCardHolder", StringComparison.OrdinalIgnoreCase);
    }

    private static object? FindPile(object combatRoot, object? player, ObjectGraph graph, params string[] names)
    {
        object? playerCombatState = ResolvePlayerCombatState(player, graph);
        return FindMemberValue(playerCombatState, names)
            ?? FindMemberValue(player, names)
            ?? FindMemberValue(combatRoot, names)
            ?? FindFirstEnumerable(graph, names)
            ?? FindRecentCardPile(names);
    }

    private static object? ResolvePlayerCombatState(object? player, ObjectGraph graph)
    {
        if (player is not null && IsPlayerCombatStateType(player.GetType()))
        {
            return player;
        }

        object? directCombatState = FindMemberValue(
            player,
            "PlayerCombatState",
            "playerCombatState",
            "_playerCombatState");
        if (directCombatState is not null)
        {
            return directCombatState;
        }

        directCombatState = FindMemberValue(
            recentPlayer,
            "PlayerCombatState",
            "playerCombatState",
            "_playerCombatState");
        if (directCombatState is not null)
        {
            return directCombatState;
        }

        if (recentPlayerCombatState is not null)
        {
            return recentPlayerCombatState;
        }

        return graph.Nodes
            .Select(node => node.Value)
            .FirstOrDefault(value => value is not null && IsPlayerCombatStateType(value.GetType()));
    }

    private static bool IsPlayerCombatStateType(Type type)
    {
        string typeName = type.FullName ?? type.Name;
        return typeName.Contains("PlayerCombatState", StringComparison.OrdinalIgnoreCase);
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
            object? cardStats = FindMemberValue(card, "cardStats", "_cardStats", "stats", "_stats");
            object? cardInfo = FindMemberValue(card, "cardInfo", "_cardInfo", "info", "_info", "baseCard", "_baseCard");
            object? cardModel = FindMemberValue(card, "Model", "model", "_model", "cardModel", "_cardModel");
            object? energyCost = FindMemberValue(card, "EnergyCost", "energyCost", "_energyCost")
                ?? FindMemberValue(cardModel, "EnergyCost", "energyCost", "_energyCost")
                ?? FindMemberValue(cardInfo, "EnergyCost", "energyCost", "_energyCost");
            bool isAttack = IsCardObjectType(card, "attack");
            bool gainsBlock = ReadBool(card, "GainsBlock", "gainsBlock", "_gainsBlock") == true;
            int? dynamicVarValue = ReadSingleDynamicVarInt(card);
            int? damage = ReadFirstInt(new[] { card, cardModel, cardStats, cardInfo }, "damage", "_damage", "baseDamage", "_baseDamage", "currentDamage", "_currentDamage", "attackDamage", "_attackDamage", "damageVar", "_damageVar", "calculatedDamage", "_calculatedDamage", "calculatedDamageVar", "_calculatedDamageVar")
                ?? ReadDynamicVarInt(card, "damage", "dmg", "attack", "calculateddamage")
                ?? (isAttack ? dynamicVarValue : null);
            int? block = ReadFirstInt(new[] { card, cardModel, cardStats, cardInfo }, "block", "_block", "baseBlock", "_baseBlock", "currentBlock", "_currentBlock", "blockVar", "_blockVar", "calculatedBlock", "_calculatedBlock")
                ?? ReadDynamicVarInt(card, "block", "blk", "shield", "calculatedblock")
                ?? (gainsBlock ? dynamicVarValue : null);
            int? hits = ReadFirstInt(new[] { card, cardModel, cardStats, cardInfo }, "hits", "_hits", "times", "_times", "attackCount", "_attackCount", "repeatCount", "_repeatCount", "hitCount", "_hitCount", "calculatedHits", "_calculatedHits", "calculatedHitsKey", "_calculatedHitsKey")
                ?? ReadDynamicVarInt(card, "hits", "hit", "repeat", "times", "calculatedhits")
                ?? (damage is not null ? 1 : null);

            string cardName = ReadCardName(card, cardModel, cardInfo) ?? fallbackName;

            cards.Add(new Dictionary<string, object?>
            {
                ["instance_id"] = BuildCardInstanceId(pileName, index, cardName, card, cardInfo, cardModel),
                ["card_id"] = ReadFirstString(new[] { card, cardModel, cardInfo }, "id", "_id", "cardId", "_cardId", "key", "_key") ?? fallbackName,
                ["name"] = cardName,
                ["type"] = ReadFirstString(new[] { card, cardModel, cardInfo, cardStats }, "type", "_type", "cardType", "_cardType"),
                ["cost"] = ReadFirstInt(new[] { card, cardModel, cardStats, cardInfo, energyCost }, "cost", "_cost", "currentCost", "_currentCost", "energyCost", "_energyCost", "EnergyCost", "CanonicalEnergyCost", "canonicalEnergyCost", "_canonicalEnergyCost", "calculatedEnergy", "_calculatedEnergy", "calculatedEnergyKey", "_calculatedEnergyKey"),
                ["base_cost"] = ReadFirstInt(new[] { card, cardModel, cardStats, cardInfo, energyCost }, "baseCost", "_baseCost", "baseEnergyCost", "_baseEnergyCost", "energyCost", "_energyCost", "EnergyCost", "CanonicalEnergyCost", "canonicalEnergyCost", "_canonicalEnergyCost"),
                ["upgraded"] = ReadBool(card, "upgraded", "isUpgraded"),
                ["playable"] = ReadBool(card, "playable", "canPlay", "isPlayable"),
                ["target_type"] = ReadFirstString(new[] { card, cardModel, cardInfo, cardStats }, "targetType", "_targetType", "target", "_target", "cardTarget", "_cardTarget"),
                ["damage"] = damage,
                ["block"] = block,
                ["hits"] = hits,
                ["description"] = BuildCardDescription(pileName, card, cardModel, cardInfo, cardStats)
            });
            index++;
        }

        return cards;
    }

    private static string? ReadCardName(params object?[] sources)
    {
        return ReadFirstString(
            sources,
            "name",
            "_name",
            "displayName",
            "_displayName",
            "title",
            "_title",
            "Title");
    }

    private static string BuildCardInstanceId(string pileName, int index, string cardName, params object?[] sources)
    {
        string? runtimeId = ReadFirstString(
            sources,
            "instanceId",
            "_instanceId",
            "instance_id",
            "_instance_id",
            "uuid",
            "_uuid",
            "guid",
            "_guid",
            "runtimeId",
            "_runtimeId",
            "entityId",
            "_entityId",
            "uniqueId",
            "_uniqueId");
        if (!string.IsNullOrWhiteSpace(runtimeId))
        {
            return SanitizeActionId($"{pileName}_{runtimeId}");
        }

        return SanitizeActionId($"{pileName}_{index}_{cardName}");
    }

    private static string BuildCardRuntimeMergeKey(object card)
    {
        object? cardModel = FindMemberValue(card, "Model", "model", "_model", "cardModel", "_cardModel");
        object? cardInfo = FindMemberValue(card, "cardInfo", "_cardInfo", "info", "_info", "baseCard", "_baseCard");
        string? runtimeId = ReadFirstString(
            new[] { card, cardModel, cardInfo },
            "instanceId",
            "_instanceId",
            "instance_id",
            "_instance_id",
            "uuid",
            "_uuid",
            "guid",
            "_guid",
            "runtimeId",
            "_runtimeId",
            "entityId",
            "_entityId",
            "uniqueId",
            "_uniqueId");
        return string.IsNullOrWhiteSpace(runtimeId)
            ? $"ref:{RuntimeHelpers.GetHashCode(card)}"
            : $"id:{runtimeId}";
    }

    private static string? BuildCardDescription(string pileName, params object?[] cardSources)
    {
        foreach (object? source in cardSources)
        {
            string? description = TryGetDescriptionForPile(source, pileName);
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }
        }

        foreach (object? source in cardSources)
        {
            string? description = TryReadFormattedDescription(source);
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }
        }

        return ReadFirstString(
            cardSources,
            "description",
            "Description",
            "_description",
            "desc",
            "_desc",
            "rawDescription",
            "_rawDescription",
            "text",
            "_text",
            "descriptionLoc",
            "_descriptionLoc",
            "descriptionText",
            "_descriptionText");
    }

    private static string? TryGetDescriptionForPile(object? source, string pileName)
    {
        if (source is null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (MethodInfo method in source.GetType().GetMethods(flags)
            .Where(candidate => candidate.Name.Equals("GetDescriptionForPile", StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.GetParameters().Length))
        {
            object?[]? args = BuildGetDescriptionForPileArgs(method, pileName);
            if (args is null)
            {
                continue;
            }

            object? value = TryInvokeMethod(source, method, args);
            if (value is string text && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static object?[]? BuildGetDescriptionForPileArgs(MethodInfo method, string pileName)
    {
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length is < 1 or > 2)
        {
            return null;
        }

        object? pileType = CreatePileTypeValue(parameters[0].ParameterType, pileName);
        if (pileType is null)
        {
            return null;
        }

        if (parameters.Length == 1)
        {
            return new[] { pileType };
        }

        return new[] { pileType, null };
    }

    private static object? CreatePileTypeValue(Type pileType, string pileName)
    {
        if (!pileType.IsEnum)
        {
            return null;
        }

        string enumName = pileName switch
        {
            "draw" => "Draw",
            "hand" => "Hand",
            "discard" => "Discard",
            "exhaust" => "Exhaust",
            "play" => "Play",
            _ => "None"
        };

        try
        {
            return Enum.Parse(pileType, enumName, ignoreCase: true);
        }
        catch
        {
            return Enum.GetValues(pileType).Cast<object>().FirstOrDefault();
        }
    }

    private static string? TryReadFormattedDescription(object? source)
    {
        object? description = FindMemberValue(
            source,
            "Description",
            "description",
            "_description",
            "descriptionLoc",
            "_descriptionLoc",
            "descriptionText",
            "_descriptionText");
        if (description is null)
        {
            return null;
        }

        string? formatted = TryInvokeMethod(description, "GetFormattedText") as string;
        if (!string.IsNullOrWhiteSpace(formatted))
        {
            return formatted;
        }

        string? raw = TryInvokeMethod(description, "GetRawText") as string;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        return ReadObjectString(description);
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

    private static bool IsCardObjectType(object card, string expectedType)
    {
        string? cardType = ReadFirstString(new[] { card }, "type", "Type", "_type", "cardType", "_cardType");
        return !string.IsNullOrWhiteSpace(cardType)
            && cardType.Contains(expectedType, StringComparison.OrdinalIgnoreCase);
    }

    private static int? ReadSingleDynamicVarInt(object card)
    {
        List<int> values = EnumerateDynamicVars(card)
            .Select(ReadDynamicVarNumericValue)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .Distinct()
            .Take(2)
            .ToList();

        return values.Count == 1 ? values[0] : null;
    }

    private static int? ReadDynamicVarInt(object card, params string[] hints)
    {
        foreach (object dynamicVar in EnumerateDynamicVars(card))
        {
            string text = NormalizePowerText(BuildDynamicVarText(dynamicVar));
            if (!ContainsAny(text, hints))
            {
                continue;
            }

            int? value = ReadDynamicVarNumericValue(dynamicVar);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static IEnumerable<object> EnumerateDynamicVars(params object?[] sources)
    {
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        foreach (object? source in sources)
        {
            foreach (object dynamicSource in EnumerateDynamicVarSources(source))
            {
                foreach (object item in EnumerateObjects(dynamicSource))
                {
                    if (item is null || IsScalar(item.GetType()) || !seen.Add(item))
                    {
                        continue;
                    }

                    yield return item;
                }
            }
        }
    }

    private static IEnumerable<object> EnumerateDynamicVarSources(object? source)
    {
        if (source is null)
        {
            yield break;
        }

        foreach (string memberName in new[] { "DynamicVars", "dynamicVars", "_dynamicVars", "CanonicalVars", "canonicalVars", "_canonicalVars", "Vars", "vars", "_vars" })
        {
            object? value = FindMemberValue(source, memberName);
            if (value is not null)
            {
                yield return value;
            }
        }
    }

    private static int? ReadDynamicVarNumericValue(object dynamicVar)
    {
        return ReadInt(
            dynamicVar,
            "CurrentValue",
            "currentValue",
            "_currentValue",
            "Value",
            "value",
            "_value",
            "BaseValue",
            "baseValue",
            "_baseValue",
            "Amount",
            "amount",
            "_amount",
            "Number",
            "number",
            "_number");
    }

    private static string BuildDynamicVarText(object dynamicVar)
    {
        return string.Join(
            " ",
            GetReadableName(dynamicVar),
            ReadFirstString(
                new[] { dynamicVar },
                "Key",
                "key",
                "_key",
                "Id",
                "id",
                "_id",
                "Name",
                "name",
                "_name",
                "Title",
                "title",
                "_title",
                "Token",
                "token",
                "_token") ?? string.Empty,
            dynamicVar.GetType().FullName ?? dynamicVar.GetType().Name);
    }

    private static Dictionary<string, object?> ReadDictionary(Dictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out object? value))
        {
            return new Dictionary<string, object?>();
        }

        return value as Dictionary<string, object?> ?? new Dictionary<string, object?>();
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

    private static int? ReadJsonInt(JsonElement root, string objectName, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(objectName, out JsonElement child)
            || child.ValueKind != JsonValueKind.Object
            || !child.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return ReadJsonInt(value);
    }

    private static int? ReadJsonInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int ReadJsonArrayCount(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return value.GetArrayLength();
    }

    private static int ReadJsonArrayCount(JsonElement root, string objectName, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(objectName, out JsonElement child)
            || child.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        return ReadJsonArrayCount(child, propertyName);
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
            PowerGroups powerGroups = BuildPowerGroups(enemy, FindMemberValue(enemy, "Creature", "creature"));

            result.Add(new Dictionary<string, object?>
            {
                ["id"] = ReadString(enemy, "id", "enemyId", "monsterId") ?? $"enemy_{index}",
                ["name"] = ReadString(enemy, "name", "displayName") ?? GetReadableName(enemy),
                ["hp"] = ReadInt(enemy, "hp", "currentHp", "currentHealth", "health"),
                ["max_hp"] = ReadInt(enemy, "maxHp", "maxHealth"),
                ["block"] = ReadInt(enemy, "block", "currentBlock", "shield"),
                ["buffs"] = powerGroups.Buffs,
                ["debuffs"] = powerGroups.Debuffs,
                ["powers_unknown"] = powerGroups.Unknown,
                ["intent"] = BuildIntent(enemy)
            });
            index++;
        }

        return result;
    }

    private static PowerGroups BuildPowerGroups(params object?[] owners)
    {
        List<Dictionary<string, object?>> buffs = new();
        List<Dictionary<string, object?>> debuffs = new();
        List<Dictionary<string, object?>> unknown = new();
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);

        foreach (object? owner in owners)
        {
            foreach (PowerCandidate candidate in EnumeratePowerCandidates(owner))
            {
                if (!seen.Add(candidate.Power))
                {
                    continue;
                }

                Dictionary<string, object?> snapshot = BuildPowerSnapshot(candidate.Power);
                PowerPolarity polarity = ClassifyPower(candidate);
                if (polarity == PowerPolarity.Buff)
                {
                    buffs.Add(snapshot);
                }
                else if (polarity == PowerPolarity.Debuff)
                {
                    debuffs.Add(snapshot);
                }
                else
                {
                    unknown.Add(snapshot);
                }
            }
        }

        return new PowerGroups(buffs, debuffs, unknown);
    }

    private static IEnumerable<PowerCandidate> EnumeratePowerCandidates(object? owner)
    {
        if (owner is null)
        {
            yield break;
        }

        foreach (MemberInfo member in GetReadableMembers(owner.GetType()))
        {
            if (!PowerSourceMemberNames.Any(name => member.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            object? source = ReadMember(owner, member);
            foreach (object power in EnumeratePowerObjects(source))
            {
                yield return new PowerCandidate(power, member.Name);
            }
        }
    }

    private static IEnumerable<object> EnumeratePowerObjects(object? source)
    {
        return EnumeratePowerObjects(source, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static IEnumerable<object> EnumeratePowerObjects(object? source, int depth, HashSet<object> seenContainers)
    {
        foreach (object item in EnumerateObjects(source))
        {
            object? value = ReadNamedValue(item, "Value", "value");
            object candidate = value is not null && !IsScalar(value.GetType())
                ? value
                : item;

            if (IsPowerContainerLike(candidate))
            {
                if (depth >= 3 || !seenContainers.Add(candidate))
                {
                    continue;
                }

                foreach (object nested in EnumeratePowerObjects(FindPowerContainerContents(candidate), depth + 1, seenContainers))
                {
                    yield return nested;
                }

                continue;
            }

            if (!IsScalar(candidate.GetType()) && IsPowerLike(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static bool IsPowerLike(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        string readableName = GetReadableName(value);
        return ContainsAny(typeName, "Power", "StatusEffect", "Buff", "Debuff", "Effect")
            || ContainsAny(readableName, "Power", "StatusEffect", "Buff", "Debuff", "Effect")
            || ReadString(value, "id", "powerId", "key") is not null;
    }

    private static bool IsPowerContainerLike(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return ContainsAny(typeName, "List", "Collection", "Dictionary", "Manager", "Inventory", "Set", "Pile");
    }

    private static object? FindPowerContainerContents(object container)
    {
        return FindMemberValue(
            container,
            "Items",
            "items",
            "Values",
            "values",
            "List",
            "list",
            "Contents",
            "contents",
            "Powers",
            "powers",
            "_items",
            "_values",
            "_powers");
    }

    private static Dictionary<string, object?> BuildPowerSnapshot(object power)
    {
        string fallbackName = GetReadableName(power);
        return new Dictionary<string, object?>
        {
            ["id"] = ReadString(power, "id", "powerId", "key") ?? fallbackName,
            ["name"] = ReadString(power, "name", "displayName", "title") ?? fallbackName,
            ["amount"] = ReadInt(power, "amount", "stacks", "stack", "value", "counter", "count"),
            ["description"] = ReadString(power, "description", "desc", "tooltip", "toolTip"),
            ["source_type"] = "power",
            ["type_name"] = power.GetType().FullName ?? power.GetType().Name
        };
    }

    private static PowerPolarity ClassifyPower(PowerCandidate candidate)
    {
        if (candidate.SourceMemberName.Equals("buffs", StringComparison.OrdinalIgnoreCase))
        {
            return PowerPolarity.Buff;
        }

        if (candidate.SourceMemberName.Equals("debuffs", StringComparison.OrdinalIgnoreCase))
        {
            return PowerPolarity.Debuff;
        }

        bool? explicitDebuff = ReadBool(candidate.Power, "isDebuff", "debuff", "negative", "isNegative", "isBad");
        if (explicitDebuff == true)
        {
            return PowerPolarity.Debuff;
        }

        bool? explicitBuff = ReadBool(candidate.Power, "isBuff", "buff", "positive", "isPositive", "beneficial", "isGood");
        if (explicitBuff == true)
        {
            return PowerPolarity.Buff;
        }

        string classificationText = string.Join(
            " ",
            ReadString(candidate.Power, "type", "powerType", "kind", "category", "polarity") ?? string.Empty,
            ReadString(candidate.Power, "id", "powerId", "key") ?? string.Empty,
            ReadString(candidate.Power, "name", "displayName", "title") ?? string.Empty,
            candidate.Power.GetType().FullName ?? candidate.Power.GetType().Name);

        if (ContainsAny(classificationText, "debuff", "negative", "detrimental", "curse")
            || ContainsAny(NormalizePowerText(classificationText), DebuffPowerKeywords))
        {
            return PowerPolarity.Debuff;
        }

        if (ContainsAny(classificationText, "buff", "positive", "beneficial")
            || ContainsAny(NormalizePowerText(classificationText), BuffPowerKeywords))
        {
            return PowerPolarity.Buff;
        }

        return PowerPolarity.Unknown;
    }

    private static Dictionary<string, object?> BuildIntent(object enemy)
    {
        object? monster = FindMemberValue(enemy, "monster", "_monster", "Monster");
        object? directIntent = FindMemberValue(
            enemy,
            "intent",
            "_intent",
            "Intent",
            "currentIntent",
            "_currentIntent",
            "move",
            "_move",
            "Move",
            "currentMove",
            "_currentMove",
            "nextMove",
            "_nextMove",
            "NextMove",
            "cardIntent",
            "_cardIntent",
            "CardIntent");
        object? move = FindMemberValue(monster, "nextMove", "_nextMove", "NextMove")
            ?? FindMemberValue(directIntent, "move", "_move", "Move", "nextMove", "_nextMove", "NextMove")
            ?? FindMemberValue(enemy, "move", "_move", "Move", "nextMove", "_nextMove", "NextMove");
        List<object> intents = EnumerateObjects(FindMemberValue(move, "intents", "_intents", "Intents")).ToList();
        if (!intents.Any() && directIntent is not null && IsIntentLike(directIntent))
        {
            intents.Add(directIntent);
        }

        object? intent = intents.FirstOrDefault(IsAttackIntentLike)
            ?? intents.FirstOrDefault()
            ?? directIntent;
        object? targets = FindIntentTargets(enemy);
        object? damageCalc = FindMemberValue(intent, "damageCalc", "_damageCalc", "DamageCalc", "damage", "_damage")
            ?? FindMemberValue(move, "damageCalc", "_damageCalc", "DamageCalc", "damage", "_damage");
        object? repeatCalc = FindMemberValue(intent, "repeatCalc", "_repeatCalc", "RepeatCalc", "repeat", "_repeat", "hits", "_hits")
            ?? FindMemberValue(move, "repeatCalc", "_repeatCalc", "RepeatCalc", "repeat", "_repeat", "hits", "_hits");

        int? damage = TryInvokeInt(intent, "GetSingleDamage", targets, enemy)
            ?? ReadFirstInt(
            new[] { intent, move, damageCalc, enemy },
            "damage",
            "_damage",
            "Damage",
            "baseDamage",
            "_baseDamage",
            "BaseDamage",
            "damageCalc",
            "_damageCalc",
            "DamageCalc",
            "attackDamage",
            "_attackDamage",
            "damageAmount",
            "_damageAmount",
            "damagePerHit",
            "_damagePerHit",
            "intentDamage",
            "_intentDamage",
            "moveDamage",
            "_moveDamage");
        int? hits = TryReadIntProperty(intent, "Repeats", "repeats", "_repeats")
            ?? ReadFirstInt(
            new[] { intent, move, repeatCalc, enemy },
            "hits",
            "_hits",
            "Hits",
            "repeats",
            "_repeats",
            "Repeats",
            "times",
            "_times",
            "attackCount",
            "_attackCount",
            "hitCount",
            "_hitCount",
            "intentHits",
            "_intentHits",
            "moveHits",
            "_moveHits");
        int? totalDamage = TryInvokeInt(intent, "GetTotalDamage", targets, enemy)
            ?? ReadFirstInt(
            new[] { intent, move, enemy },
            "totalDamage",
            "_totalDamage",
            "TotalDamage",
            "intentTotalDamage",
            "_intentTotalDamage",
            "moveTotalDamage",
            "_moveTotalDamage",
            "total_damage");

        if (totalDamage is null && damage is not null && hits is not null)
        {
            totalDamage = damage.Value * hits.Value;
        }

        int damageValue = damage ?? 0;
        int hitsValue = hits ?? (damageValue > 0 ? 1 : 0);
        int totalDamageValue = totalDamage ?? (damageValue > 0 && hitsValue > 0 ? damageValue * hitsValue : 0);
        int blockValue = ReadFirstInt(
            new[] { intent, move, enemy },
            "block",
            "_block",
            "Block",
            "baseBlock",
            "_baseBlock",
            "BaseBlock",
            "shield",
            "_shield",
            "intentBlock",
            "_intentBlock",
            "moveBlock",
            "_moveBlock",
            "blockGain",
            "_blockGain",
            "blockAmount",
            "_blockAmount")
            ?? 0;
        string? moveId = ReadString(move, "id", "_id", "Id", "stateId", "_stateId", "StateId");
        List<string> rawIntents = intents
            .Select(candidate => GetIntentLabelText(candidate, targets, enemy) ?? GetReadableName(candidate))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        string? rawIntent = rawIntents.Any()
            ? string.Join(", ", rawIntents)
            : (intent is null ? (move is null ? null : GetReadableName(move)) : GetReadableName(intent));

        return new Dictionary<string, object?>
        {
            ["type"] = InferIntentType(intents, intent, rawIntent, damageValue, blockValue),
            ["move_id"] = moveId,
            ["raw_intent"] = rawIntent,
            ["damage"] = damageValue,
            ["hits"] = hitsValue,
            ["total_damage"] = totalDamageValue,
            ["block"] = blockValue,
            ["applied_powers"] = BuildIntentAppliedPowers(intent),
            ["description"] = GetIntentLabelText(intent, targets, enemy) ?? ReadFirstString(new[] { intent, move }, "description", "_description", "desc", "_desc", "tooltip", "_tooltip", "toolTip", "_toolTip", "moveName", "_moveName"),
            ["damage_is_adjusted"] = null,
            ["damage_source"] = damage is null && hits is null && totalDamage is null
                ? "unavailable"
                : "sts2_intent_methods_or_reflected_candidate_fields"
        };
    }

    private static string InferIntentType(List<object> intents, object? intent, string? rawIntent, int damage, int block)
    {
        List<string> intentTypes = intents
            .Select(candidate => ReadString(candidate, "intentType", "_intentType", "IntentType", "type", "_type", "Type") ?? GetReadableName(candidate))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        bool hasAttack = intentTypes.Any(text => ContainsAny(text, "attack")) || damage > 0;
        bool hasDebuff = intentTypes.Any(text => ContainsAny(text, "debuff"));

        if (hasAttack && hasDebuff)
        {
            return "attack_debuff";
        }

        if (hasAttack)
        {
            return "attack";
        }

        if (intentTypes.Any(text => ContainsAny(text, "defend", "block")) || block > 0)
        {
            return "defend";
        }

        if (hasDebuff)
        {
            return "debuff";
        }

        if (intentTypes.Any(text => ContainsAny(text, "buff")))
        {
            return "buff";
        }

        return InferIntentType(intent, rawIntent, damage, block);
    }

    private static string InferIntentType(object? intent, string? rawIntent, int damage, int block)
    {
        string text = string.Join(
            " ",
            rawIntent ?? string.Empty,
            ReadString(intent, "type", "intentType", "kind", "category") ?? string.Empty);

        if (ContainsAny(text, "attack") && ContainsAny(text, "debuff"))
        {
            return "attack_debuff";
        }

        if (ContainsAny(text, "attack") || damage > 0)
        {
            return "attack";
        }

        if (ContainsAny(text, "defend", "block") || block > 0)
        {
            return "defend";
        }

        if (ContainsAny(text, "debuff"))
        {
            return "debuff";
        }

        if (ContainsAny(text, "buff"))
        {
            return "buff";
        }

        return "unknown";
    }

    private static bool IsIntentLike(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return typeName.Contains("Intent", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAttackIntentLike(object value)
    {
        string text = string.Join(
            " ",
            value.GetType().FullName ?? value.GetType().Name,
            ReadString(value, "intentType", "_intentType", "IntentType") ?? string.Empty);
        return ContainsAny(text, "attack");
    }

    private static object? FindIntentTargets(object enemy)
    {
        object? combatState = FindMemberValue(enemy, "combatState", "_combatState", "CombatState");
        return FindMemberValue(combatState, "playerCreatures", "_playerCreatures", "PlayerCreatures")
            ?? FindMemberValue(combatState, "allies", "_allies", "Allies");
    }

    private static string? GetIntentLabelText(object? intent, object? targets, object enemy)
    {
        object? label = TryInvokeMethod(intent, "GetIntentLabel", targets, enemy);
        object? formattedText = TryInvokeMethod(label, "GetFormattedText");
        return formattedText as string;
    }

    private static int? TryReadIntProperty(object? source, params string[] names)
    {
        object? value = FindMemberValue(source, names);
        return ConvertToInt(value);
    }

    private static int? TryInvokeInt(object? source, string methodName, params object?[] args)
    {
        object? value = TryInvokeMethod(source, methodName, args);
        return ConvertToInt(value);
    }

    private static int? ConvertToInt(object? value)
    {
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

    private static object? TryInvokeMethod(object? source, string methodName, params object?[] args)
    {
        if (source is null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo? method = source.GetType()
            .GetMethods(flags)
            .FirstOrDefault(candidate =>
                candidate.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && candidate.GetParameters().Length == args.Length);
        if (method is null)
        {
            return null;
        }

        try
        {
            return method.Invoke(source, args);
        }
        catch
        {
            return null;
        }
    }

    private static object? TryInvokeMethod(object? source, MethodInfo method, params object?[] args)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            return method.Invoke(source, args);
        }
        catch
        {
            return null;
        }
    }

    private static List<Dictionary<string, object?>> BuildIntentAppliedPowers(object? intent)
    {
        PowerGroups powerGroups = BuildPowerGroups(intent);
        List<Dictionary<string, object?>> powers = new();
        powers.AddRange(powerGroups.Buffs.Select(power => AddPowerTarget(power, null)));
        powers.AddRange(powerGroups.Debuffs.Select(power => AddPowerTarget(power, null)));
        powers.AddRange(powerGroups.Unknown.Select(power => AddPowerTarget(power, null)));
        return powers;
    }

    private static Dictionary<string, object?> AddPowerTarget(Dictionary<string, object?> power, string? target)
    {
        Dictionary<string, object?> result = new(power)
        {
            ["target"] = target
        };
        return result;
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
                ["name"] = ReadString(relic, "name", "displayName", "title") ?? fallbackName,
                ["description"] = ReadString(relic, "description", "desc", "tooltip", "toolTip"),
                ["rarity"] = ReadString(relic, "rarity", "relicRarity"),
                ["counter"] = ReadInt(relic, "counter", "count", "charges", "uses", "value")
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

    private static object? FindFirstRuntimePlayer(ObjectGraph graph)
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
                return IsRuntimePlayerType(typeName);
            });
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

    private static object? ReadNamedValue(object? source, params string[] names)
    {
        return FindMemberValue(source, names);
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

    private static int? ReadFirstInt(IEnumerable<object?> sources, params string[] names)
    {
        foreach (object? source in sources)
        {
            int? value = ReadInt(source, names);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadFirstString(IEnumerable<object?> sources, params string[] names)
    {
        foreach (object? source in sources)
        {
            string? value = ReadString(source, names);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
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
            _ => ReadObjectString(value)
        };
    }

    private static string? ReadObjectString(object value)
    {
        string? nestedText = ReadNestedStringValue(value);
        if (!string.IsNullOrWhiteSpace(nestedText))
        {
            return nestedText;
        }

        string? text = value.ToString();
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return string.Equals(text, typeName, StringComparison.Ordinal)
            ? null
            : text;
    }

    private static string? ReadNestedStringValue(object value)
    {
        foreach (string memberName in new[] { "Text", "text", "_text", "Value", "value", "_value", "Key", "key", "_key", "Id", "id", "_id" })
        {
            object? nested = FindMemberValue(value, memberName);
            if (nested is string text && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
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

    private static string NormalizePowerText(string text)
    {
        StringBuilder builder = new(text.Length);
        foreach (char character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (character is '_' or '-' or ' ')
            {
                builder.Append('_');
            }
        }

        return builder.ToString();
    }

    private enum PowerPolarity
    {
        Unknown,
        Buff,
        Debuff
    }

    private sealed record PowerCandidate(object Power, string SourceMemberName);

    private sealed record PowerGroups(
        List<Dictionary<string, object?>> Buffs,
        List<Dictionary<string, object?>> Debuffs,
        List<Dictionary<string, object?>> Unknown);

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

    internal sealed record CombatExportProbe(
        bool IsInProgress,
        bool StateFound,
        bool HasPlayerVitals,
        bool HasHandCards,
        bool HasEnemies,
        int? PlayerHp,
        int? PlayerEnergy,
        int HandCount,
        int EnemyCount,
        string Reason)
    {
        public bool IsStable => StateFound && HasPlayerVitals && HasHandCards && HasEnemies;
    }

    private sealed record ObjectNode(string Path, object? Value, int Depth);
}
