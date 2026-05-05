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
    private const long CardSelectionSourceRememberMs = 30000;
    private static CardSelectionSourceHint? latestCardSelectionSourceHint;
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

    internal static void RememberCardSelectionSourceHint(string? cardId, string? cardName, bool? upgraded)
    {
        if (string.IsNullOrWhiteSpace(cardId) && string.IsNullOrWhiteSpace(cardName))
        {
            return;
        }

        latestCardSelectionSourceHint = new CardSelectionSourceHint(
            cardId,
            cardName,
            upgraded,
            Environment.TickCount64);
    }

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
    private static string? lastPublishedPhase;
    private static int lastPublishedLegalActionCount;

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

    internal static object? FindLatestRuntimeHandCardByCombatCardId(int combatCardId)
    {
        object? managerCombatState = ReadCombatManagerDebugState(GetCombatManagerInstance());
        object? combatRoot = managerCombatState ?? recentCombatState;
        if (combatRoot is null)
        {
            return null;
        }

        List<object> roots = GetExportRoots(combatRoot, managerCombatState);
        ObjectGraph graph = ObjectGraph.Collect(roots, MaxSearchDepth, MaxVisitedObjects);
        object? player = ResolveRuntimePlayer(combatRoot, graph)
            ?? recentPlayer
            ?? recentPlayerCombatState
            ?? FindFirst(graph, "Player", "PlayerCombatState");
        CardMovementObserver.ObserveContext(combatRoot, player);

        foreach (object card in SelectMergedHandSource(combatRoot, player, graph))
        {
            uint? runtimeCombatCardId = TryReadCombatCardId(card);
            if (runtimeCombatCardId is not null && runtimeCombatCardId.Value == (uint)combatCardId)
            {
                return card;
            }
        }

        return null;
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

    internal static string? ReadLatestStateFilePhase(string reason)
    {
        string outputPath = GetOutputPath();
        try
        {
            if (!File.Exists(outputPath))
            {
                return null;
            }

            string json = File.ReadAllText(outputPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("phase", out JsonElement phaseElement)
                && phaseElement.ValueKind == JsonValueKind.String)
            {
                return phaseElement.GetString();
            }
        }
        catch (Exception exception)
        {
            Logger.Warning($"최신 combat_state.json 단계 확인 중 예외가 발생했습니다. 게임 진행은 멈추지 않습니다. reason={reason}, path={outputPath}, {exception.GetType().Name}: {exception.Message}");
        }

        return null;
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

    internal static bool TryRefreshCombatStateFromCombatManager(string reason)
    {
        try
        {
            RuntimePhaseResolution runtimePhase = RuntimePhaseResolver.Resolve();
            if (!runtimePhase.CombatInProgress)
            {
                return false;
            }

            if (runtimePhase.CombatState is null)
            {
                Logger.Warning($"전투가 진행 중이지만 CombatManager.DebugOnlyGetState 값을 읽지 못했습니다. 화면 export를 건너뜁니다. reason={reason}, block_reason={runtimePhase.BlockScreenExportReason}");
                return true;
            }

            object combatRoot = runtimePhase.CombatState;
            RememberObservedRoot(combatRoot);
            TryExport(combatRoot);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warning($"CombatManager 기준 전투 상태 갱신 중 예외가 발생했습니다. 다음 타이머 틱에서 다시 시도합니다. reason={reason}, {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal static bool TryExportGameOverStateIfVisible()
    {
        try
        {
            object? gameOverScreen = FindGameOverScreen();
            if (gameOverScreen is null)
            {
                return false;
            }

            Dictionary<string, object?> state = BuildGameOverState(gameOverScreen);
            WriteState(gameOverScreen, state, "game_over", force: false, tickAfterExport: false);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warning($"Game over state export failed. The game will keep running. {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal static bool TryExportRewardStateIfVisible()
    {
        try
        {
            object? rewardScreen = FindRewardScreen();
            if (rewardScreen is null)
            {
                return false;
            }

            if (IsRewardScreenCoveredByOpenMap(rewardScreen))
            {
                return false;
            }

            Dictionary<string, object?> state = BuildRewardState(rewardScreen);
            WriteState(rewardScreen, state, "reward", force: false, tickAfterExport: false);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warning($"보상 화면 상태 추출에 실패했습니다. 게임 진행은 멈추지 않습니다. {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal static bool TryExportCardSelectionStateIfVisible()
    {
        try
        {
            object? cardSelectionScreen = FindCardSelectionScreen();
            if (cardSelectionScreen is null)
            {
                return false;
            }

            Dictionary<string, object?> state = BuildCardSelectionState(cardSelectionScreen);
            WriteState(cardSelectionScreen, state, "card_selection", force: false, tickAfterExport: false);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warning($"Card selection state export failed. The game will keep running. {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal static bool TryExportHandCardSelectionStateIfVisible()
    {
        try
        {
            object? hand = FindHandCardSelectionRoot();
            if (hand is null)
            {
                return false;
            }

            Dictionary<string, object?> state = BuildCardSelectionState(hand);
            WriteState(hand, state, "hand_card_selection", force: false, tickAfterExport: false);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warning($"Hand card selection state export failed. The game will keep running. {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal static bool TryExportAdapterCardSelectionStateIfPending()
    {
        try
        {
            PendingAdapterCardSelection? pending = AdapterCardSelectionBridge.GetPendingSelectionSnapshot();
            if (pending is null || pending.Completion.Task.IsCompleted)
            {
                return false;
            }

            object? combatRoot = ReadCombatManagerDebugState(GetCombatManagerInstance())
                ?? GetCombatManagerInstance()
                ?? pending;
            Dictionary<string, object?> state = BuildAdapterCardSelectionState(pending, combatRoot);
            WriteState(combatRoot, state, "adapter_card_selection", force: true, tickAfterExport: false);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warning($"어댑터 카드 선택 상태 추출에 실패했습니다. 게임 진행은 멈추지 않습니다. {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal static bool TryExportEventStateIfVisible()
    {
        try
        {
            object? eventRoom = FindEventRoom();
            if (eventRoom is null)
            {
                return false;
            }

            if (IsFinishedEventCoveredByOpenMap(eventRoom))
            {
                return false;
            }

            Dictionary<string, object?> state = BuildEventState(eventRoom);
            WriteState(eventRoom, state, "event", force: false, tickAfterExport: false);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warning($"Event room state export failed. The game will keep running. {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal static bool TryExportRestSiteStateIfVisible()
    {
        try
        {
            object? restSiteRoom = FindRestSiteRoom();
            if (restSiteRoom is null)
            {
                return false;
            }

            if (FindMapScreen() is not null)
            {
                return false;
            }

            Dictionary<string, object?> state = BuildRestSiteState(restSiteRoom);
            WriteState(restSiteRoom, state, "rest_site", force: false, tickAfterExport: false);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warning($"모닥불 화면 상태 추출에 실패했습니다. 게임 진행은 멈추지 않습니다. {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal static bool TryExportTreasureStateIfVisible()
    {
        try
        {
            object? treasureRoom = FindTreasureRoom();
            if (treasureRoom is null)
            {
                return false;
            }

            if (FindMapScreen() is not null)
            {
                return false;
            }

            Dictionary<string, object?> state = BuildTreasureState(treasureRoom);
            WriteState(treasureRoom, state, "treasure", force: false, tickAfterExport: false);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warning($"보물방 상태 추출에 실패했습니다. 게임 진행은 멈추지 않습니다. {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal static bool TryExportShopStateIfVisible()
    {
            try
            {
                object? shopScreen = ShopRuntimeLocator.FindShopScreen(CreateShopRuntimeLocatorContext());
                if (shopScreen is null)
                {
                    return false;
            }

            if (FindMapScreen() is not null)
            {
                return false;
            }

            Dictionary<string, object?> state = BuildShopState(shopScreen);
            WriteState(shopScreen, state, "shop", force: false, tickAfterExport: false);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warning($"상점 화면 상태 추출에 실패했습니다. 게임 진행은 멈추지 않습니다. {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal static bool TryExportMapStateIfVisible()
    {
        try
        {
            object? mapScreen = FindMapScreen();
            if (mapScreen is null)
            {
                return false;
            }

            if (TryExportCombatInsteadOfScreen("map", mapScreen, tickAfterExport: false))
            {
                return true;
            }

            Dictionary<string, object?> state = BuildMapState(mapScreen);
            WriteState(mapScreen, state, "map", force: false, tickAfterExport: false);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warning($"지도 화면 상태 추출에 실패했습니다. 게임 진행은 멈추지 않습니다. {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal static bool TryExportMainMenuStateIfVisible()
    {
        try
        {
            object? mainMenu = FindMainMenu();
            if (mainMenu is null || !IsLiveVisibleControl(mainMenu))
            {
                return false;
            }

            Dictionary<string, object?> state = BuildMainMenuState(mainMenu);
            WriteState(mainMenu, state, "main_menu", force: false, tickAfterExport: false);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warning($"메인 메뉴 상태 추출에 실패했습니다. 게임 진행은 멈추지 않습니다. {exception.GetType().Name}: {exception.Message}");
            return false;
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
            WriteState(combatRoot, BuildState(combatRoot), "combat", force: false, tickAfterExport: true);
        }
        catch (Exception exception)
        {
            Logger.Warning($"전투 상태 추출에 실패했습니다. 게임 진행은 멈추지 않습니다. {exception}");
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
        return WriteState(combatRoot, BuildState(combatRoot), "combat", force, tickAfterExport);
    }

    private static Dictionary<string, object?>? WriteState(
        object runtimeRoot,
        Dictionary<string, object?> state,
        string stateIdPrefix,
        bool force,
        bool tickAfterExport)
    {
        RuntimePhaseResolution runtimePhase = RuntimePhaseResolver.Resolve();
        string requestedPhase = ReadDictionaryString(state, "phase") ?? stateIdPrefix;
        if (ShouldBlockNonCombatExport(runtimePhase, requestedPhase))
        {
            if (runtimePhase.CombatState is null)
            {
                Logger.Warning($"전투 진행 중 {requestedPhase} export를 차단했습니다. 전투 debug state가 없어 이전 안정 상태를 유지합니다. reason={runtimePhase.BlockScreenExportReason}");
                ClearPendingExport();
                return null;
            }

            object combatRoot = runtimePhase.CombatState;
            RememberObservedRoot(combatRoot);
            Dictionary<string, object?> combatState = BuildState(combatRoot);
            Logger.Info($"전투 진행 중 {requestedPhase} export를 차단하고 전투 상태로 전환합니다. reason={runtimePhase.BlockScreenExportReason}");
            return WriteState(combatRoot, combatState, "combat", force, tickAfterExport);
        }

        if (!state.ContainsKey("runtime_phase"))
        {
            state["runtime_phase"] = runtimePhase.ToDiagnostics(requestedPhase);
        }
        state["room_context"] = BuildRoomContext(runtimeRoot, state);
        if (ShouldSuppressUnstableExport(runtimePhase, state, requestedPhase, out string unstableReason))
        {
            Logger.Info($"불안정한 중간 export를 보류합니다. requested_phase={requestedPhase}, reason={unstableReason}");
            ClearPendingExport();
            return null;
        }

        string stateFingerprint = ComputeStateFingerprint(state);
        state["state_id"] = $"{stateIdPrefix}_{stateFingerprint[..16]}";
        state["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Dictionary<string, object?> safeState = NormalizeStateForJson(state, "combat_state");
        string json = JsonSerializer.Serialize(safeState, JsonOptions);
        if (!force && stateFingerprint == lastStateFingerprint)
        {
            CombatActionRuntimeContext.UpdateFromExport(runtimeRoot, json);
            CombatStateBridgePoster.PostedStateSnapshot? postedState = CombatStateBridgePoster.GetLatestPostedState();
            if (postedState is null || postedState.StateId != safeState["state_id"]?.ToString())
            {
                    CombatStateBridgePoster.TryPost(json);
            }

            RememberPublishedState(safeState);
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
        CombatActionRuntimeContext.UpdateFromExport(runtimeRoot, json);
        CombatStateBridgePoster.TryPost(json);
        RememberPublishedState(safeState);
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

    private static bool TryExportCombatInsteadOfScreen(string requestedPhase, object screenRoot, bool tickAfterExport)
    {
        RuntimePhaseResolution runtimePhase = RuntimePhaseResolver.Resolve();
        if (!runtimePhase.ShouldBlockScreenExport)
        {
            return false;
        }

        if (runtimePhase.CombatState is null)
        {
            Logger.Warning($"전투 진행 중 {requestedPhase} export를 차단했습니다. 전투 debug state가 없어 이전 안정 상태를 유지합니다. reason={runtimePhase.BlockScreenExportReason}");
            return true;
        }

        object combatRoot = runtimePhase.CombatState;
        RememberObservedRoot(combatRoot);
        Dictionary<string, object?> combatState = BuildState(combatRoot);
        combatState["runtime_phase"] = runtimePhase.ToDiagnostics(requestedPhase);
        Logger.Info($"전투 진행 중 {requestedPhase} export를 차단하고 전투 상태로 전환합니다. reason={runtimePhase.BlockScreenExportReason}");
        WriteState(combatRoot, combatState, "combat", force: false, tickAfterExport: tickAfterExport);
        return true;
    }

    private static bool ShouldBlockNonCombatExport(RuntimePhaseResolution runtimePhase, string requestedPhase)
    {
        return runtimePhase.ShouldBlockScreenExport
            && !IsCombatExportPhase(requestedPhase);
    }

    private static bool IsCombatExportPhase(string requestedPhase)
    {
        return requestedPhase.Equals("combat_turn", StringComparison.OrdinalIgnoreCase)
            || requestedPhase.Equals("combat", StringComparison.OrdinalIgnoreCase)
            || requestedPhase.Equals("card_selection", StringComparison.OrdinalIgnoreCase)
            || requestedPhase.Equals("hand_card_selection", StringComparison.OrdinalIgnoreCase)
            || requestedPhase.Equals("adapter_card_selection", StringComparison.OrdinalIgnoreCase)
            || requestedPhase.Equals("unstable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSuppressUnstableExport(
        RuntimePhaseResolution runtimePhase,
        Dictionary<string, object?> state,
        string requestedPhase,
        out string reason)
    {
        reason = string.Empty;
        if (runtimePhase.Phase.Equals("unstable", StringComparison.OrdinalIgnoreCase))
        {
            reason = runtimePhase.UnstableReason ?? "runtime_phase_unstable";
            return true;
        }

        if (IsStrictCombatTurnExport(requestedPhase) && !runtimePhase.CombatInProgress)
        {
            reason = "combat_turn_export_without_active_combat_manager";
            return true;
        }

        if (IsStrictCombatTurnExport(requestedPhase)
            && runtimePhase.CombatInProgress
            && !runtimePhase.CombatPlayPhase)
        {
            reason = "combat_turn_export_before_play_phase";
            return true;
        }

        Dictionary<string, object?> roomContext = ReadDictionary(state, "room_context");
        string? roomPhase = ReadDictionaryString(roomContext, "phase");
        bool? roomCombatInProgress = ReadDictionaryBool(roomContext, "combat_in_progress");
        bool requestedCombatTurn = IsStrictCombatTurnExport(requestedPhase);
        if (requestedCombatTurn
            && !runtimePhase.Phase.Equals("combat_turn", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"runtime_phase_mismatch:{runtimePhase.Phase}";
            return true;
        }

        if (requestedCombatTurn && roomCombatInProgress == false)
        {
            reason = "room_context_reports_no_combat_for_combat_turn";
            return true;
        }

        if (string.Equals(roomPhase, "combat_turn", StringComparison.OrdinalIgnoreCase)
            && !runtimePhase.CombatInProgress)
        {
            reason = "room_context_combat_turn_without_runtime_combat";
            return true;
        }

        if (requestedCombatTurn
            && IsNonCombatPublishedPhase(lastPublishedPhase)
            && LooksLikeCombatEntryHydrationState(state))
        {
            reason = "combat_entry_hydration_state";
            return true;
        }

        if (string.Equals(requestedPhase, "main_menu", StringComparison.OrdinalIgnoreCase)
            && IsMainMenuLoadingState(state))
        {
            reason = "main_menu_loading_without_legal_actions";
            return true;
        }

        return false;
    }

    private static bool IsStrictCombatTurnExport(string requestedPhase)
    {
        return requestedPhase.Equals("combat_turn", StringComparison.OrdinalIgnoreCase)
            || requestedPhase.Equals("combat", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeCombatEntryHydrationState(Dictionary<string, object?> state)
    {
        List<Dictionary<string, object?>> legalActions = ReadDictionaryList(state, "legal_actions");
        if (legalActions.Count > 1)
        {
            return false;
        }

        string? onlyActionType = legalActions.Count == 1
            ? ReadDictionaryString(legalActions[0], "type")
            : null;
        bool onlyEndTurn = legalActions.Count == 0
            || string.Equals(onlyActionType, "end_turn", StringComparison.OrdinalIgnoreCase);
        if (!onlyEndTurn)
        {
            return false;
        }

        Dictionary<string, object?> piles = ReadDictionary(state, "piles");
        int handCount = ReadDictionaryList(piles, "hand").Count;
        return handCount == 0 || legalActions.Count <= 1;
    }

    private static bool IsMainMenuLoadingState(Dictionary<string, object?> state)
    {
        List<Dictionary<string, object?>> legalActions = ReadDictionaryList(state, "legal_actions");
        return legalActions.Count == 0;
    }

    private static bool IsNonCombatPublishedPhase(string? phase)
    {
        return string.IsNullOrWhiteSpace(phase)
            || (!phase.Equals("combat_turn", StringComparison.OrdinalIgnoreCase)
                && !phase.Equals("combat", StringComparison.OrdinalIgnoreCase)
                && !phase.Equals("adapter_card_selection", StringComparison.OrdinalIgnoreCase)
                && !phase.Equals("card_selection", StringComparison.OrdinalIgnoreCase)
                && !phase.Equals("hand_card_selection", StringComparison.OrdinalIgnoreCase));
    }

    private static void RememberPublishedState(Dictionary<string, object?> state)
    {
        lastPublishedPhase = ReadDictionaryString(state, "phase");
        lastPublishedLegalActionCount = ReadDictionaryList(state, "legal_actions").Count;
    }

    private static Dictionary<string, object?> BuildRoomContext(object runtimeRoot, Dictionary<string, object?> state)
    {
        object? runManager = GetStaticPropertyValue("MegaCrit.Sts2.Core.Runs.RunManager", "Instance");
        object? runState = ReadRunManagerDebugState(runManager)
            ?? FindMemberValue(runManager, "RunState", "runState", "_runState", "State", "state")
            ?? FindMemberValue(runtimeRoot, "RunState", "runState", "_runState", "State", "state");
        object? currentRoom = FindMemberValue(runManager, "CurrentRoom", "currentRoom", "_currentRoom")
            ?? FindMemberValue(runState, "CurrentRoom", "currentRoom", "_currentRoom")
            ?? FindMemberValue(runtimeRoot, "CurrentRoom", "currentRoom", "_currentRoom");
        object? currentMapPoint = FindMemberValue(runState, "CurrentMapPoint", "currentMapPoint", "_currentMapPoint")
            ?? FindMemberValue(runManager, "CurrentMapPoint", "currentMapPoint", "_currentMapPoint");
        object? currentCoord = FindMemberValue(runState, "CurrentMapCoord", "currentMapCoord", "_currentMapCoord")
            ?? FindMemberValue(currentMapPoint, "coord", "Coord", "_coord");
        object? combatManager = GetCombatManagerInstance();
        object? screenContext = GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext",
            "Instance");
        object? currentScreen = TryInvokeMethod(screenContext, "GetCurrentScreen");
        string phase = ReadDictionaryString(state, "phase") ?? "unknown";

        return new Dictionary<string, object?>
        {
            ["phase"] = phase,
            ["observed_root_type"] = runtimeRoot.GetType().FullName ?? runtimeRoot.GetType().Name,
            ["observed_root_name"] = GetReadableName(runtimeRoot),
            ["current_screen_type"] = currentScreen?.GetType().FullName ?? currentScreen?.GetType().Name,
            ["current_screen_name"] = currentScreen is null ? null : GetReadableName(currentScreen),
            ["run_in_progress"] = ReadBool(runManager, "IsInProgress", "isInProgress", "_isInProgress"),
            ["combat_in_progress"] = ReadBool(combatManager, "IsInProgress", "isInProgress", "_isInProgress"),
            ["combat_play_phase"] = ReadBool(combatManager, "IsPlayPhase", "isPlayPhase", "_isPlayPhase"),
            ["current_room"] = BuildRoomObjectSummary(currentRoom),
            ["current_map_point"] = BuildMapPointObjectSummary(currentMapPoint, currentCoord)
        };
    }

    private static Dictionary<string, object?>? BuildRoomObjectSummary(object? room)
    {
        if (room is null)
        {
            return null;
        }

        string typeName = room.GetType().FullName ?? room.GetType().Name;
        return new Dictionary<string, object?>
        {
            ["type_name"] = typeName,
            ["kind"] = ClassifyRoomKind(typeName, GetReadableName(room)),
            ["name"] = GetReadableName(room),
            ["room_id"] = ReadModelIdentifier(room),
            ["is_complete"] = ReadBool(room, "IsComplete", "isComplete", "_isComplete", "Complete", "complete"),
            ["is_visited"] = ReadBool(room, "IsVisited", "isVisited", "_isVisited", "Visited", "visited")
        };
    }

    private static Dictionary<string, object?>? BuildMapPointObjectSummary(object? mapPoint, object? coord)
    {
        if (mapPoint is null && coord is null)
        {
            return null;
        }

        object? pointType = FindMemberValue(mapPoint, "Type", "type", "_type", "PointType", "pointType", "_pointType");
        string? readableName = mapPoint is null ? null : GetReadableName(mapPoint);
        return new Dictionary<string, object?>
        {
            ["type_name"] = mapPoint?.GetType().FullName ?? mapPoint?.GetType().Name,
            ["kind"] = pointType?.ToString() ?? ClassifyRoomKind(mapPoint?.GetType().FullName, readableName),
            ["name"] = readableName,
            ["row"] = ReadInt(coord, "Row", "row", "Y", "y"),
            ["column"] = ReadInt(coord, "Column", "column", "X", "x"),
            ["map_point_id"] = ReadModelIdentifier(mapPoint)
        };
    }

    private static string ClassifyRoomKind(string? typeName, string? readableName)
    {
        string text = $"{typeName ?? string.Empty} {readableName ?? string.Empty}";
        if (ContainsAny(text, "Shop", "Merchant", "Store"))
        {
            return "shop";
        }

        if (ContainsAny(text, "Rest", "Campfire", "Bonfire"))
        {
            return "rest_site";
        }

        if (ContainsAny(text, "Event", "Unknown"))
        {
            return "event";
        }

        if (ContainsAny(text, "Treasure", "Chest"))
        {
            return "treasure";
        }

        if (ContainsAny(text, "Elite"))
        {
            return "elite";
        }

        if (ContainsAny(text, "Boss"))
        {
            return "boss";
        }

        if (ContainsAny(text, "Monster", "Combat"))
        {
            return "monster";
        }

        if (ContainsAny(text, "Map"))
        {
            return "map";
        }

        return "unknown";
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

    private static object? GetRunManagerInstance()
    {
        return GetStaticPropertyValue("MegaCrit.Sts2.Core.Runs.RunManager", "Instance");
    }

    private static object? ReadCombatManagerDebugState(object? combatManager)
    {
        return TryInvokeMethod(combatManager, "DebugOnlyGetState");
    }

    private static object? ReadRunManagerDebugState(object? runManager)
    {
        return TryInvokeMethod(runManager, "DebugOnlyGetState");
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

        return CombatSnapshotBuilder.Build(new CombatSnapshotBuildInput(
            Run: BuildRun(combatRoot, graph),
            Player: playerState,
            Piles: piles,
            Enemies: enemies,
            Relics: BuildRelics(relicsSource, graph),
            Debug: BuildDebug(currentRoot, combatRoot, player, enemiesSource, graph)));
    }

    private static Dictionary<string, object?> BuildGameOverState(object gameOverScreen)
    {
        object? runManager = GetStaticPropertyValue("MegaCrit.Sts2.Core.Runs.RunManager", "Instance");
        object? runState = FindMemberValue(gameOverScreen, "_runState", "runState", "RunState")
            ?? FindMemberValue(runManager, "RunState", "runState", "_runState", "State", "state");
        object? serializableRun = FindMemberValue(gameOverScreen, "_serializableRun", "serializableRun", "SerializableRun");
        object? history = FindMemberValue(gameOverScreen, "_history", "history", "History")
            ?? FindMemberValue(runManager, "History", "history", "_history");
        object? player = FindMemberValue(gameOverScreen, "_localPlayer", "localPlayer", "LocalPlayer")
            ?? recentPlayer
            ?? recentPlayerCombatState
            ?? EnumerateObjects(FindMemberValue(runState, "Players", "players", "_players")).FirstOrDefault();
        object? relicsSource = FindMemberValue(player, "relics")
            ?? FindMemberValue(runState, "relics");

        List<object> roots = new();
        AddRoot(roots, gameOverScreen);
        AddRoot(roots, runState);
        AddRoot(roots, serializableRun);
        AddRoot(roots, history);
        AddRoot(roots, player);
        ObjectGraph graph = ObjectGraph.Collect(roots, 3, 260);

        Dictionary<string, object?> gameOverState = BuildGameOverScreenState(gameOverScreen, runState, history);

        return new Dictionary<string, object?>
        {
            ["schema_version"] = "combat_state.v1",
            ["phase"] = "game_over",
            ["state_id"] = "game_over_pending",
            ["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["run"] = BuildRun(runState ?? gameOverScreen, graph),
            ["player"] = player is null ? new Dictionary<string, object?>() : BuildPlayer(player),
            ["piles"] = BuildEmptyPiles(),
            ["enemies"] = new List<Dictionary<string, object?>>(),
            ["game_over"] = gameOverState,
            ["legal_actions"] = BuildGameOverLegalActions(gameOverState),
            ["relics"] = BuildRelics(relicsSource, graph),
            ["debug"] = BuildGameOverDebug(gameOverScreen, runState, history, graph)
        };
    }

    private static List<Dictionary<string, object?>> BuildGameOverLegalActions(Dictionary<string, object?> gameOverState)
    {
        List<Dictionary<string, object?>> actions = new();
        Dictionary<string, object?> continueButton = ReadDictionary(gameOverState, "continue_button");
        bool canContinue = ReadDictionaryBool(continueButton, "found") == true
            && ReadDictionaryBool(continueButton, "visible") == true
            && ReadDictionaryBool(continueButton, "visible_in_tree") == true
            && ReadDictionaryBool(continueButton, "enabled") == true
            && ReadDictionaryBool(gameOverState, "summary_animating") != true;
        Dictionary<string, object?> mainMenuButton = ReadDictionary(gameOverState, "main_menu_button");
        bool canReturnToMainMenu = ReadDictionaryBool(mainMenuButton, "found") == true
            && ReadDictionaryBool(mainMenuButton, "visible") == true
            && ReadDictionaryBool(mainMenuButton, "visible_in_tree") == true
            && ReadDictionaryBool(mainMenuButton, "enabled") == true
            && ReadDictionaryBool(gameOverState, "summary_animating") != true;

        if (canContinue || canReturnToMainMenu)
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = "dismiss_game_over",
                ["type"] = "dismiss_game_over",
                ["summary"] = "게임 오버 화면의 계속 버튼을 눌러 다음 화면으로 진행한다.",
                ["validation_note"] = "게임 오버 요약 애니메이션이 끝났고 계속 버튼이 보이며 활성화된 상태입니다."
            });
        }

        return actions;
    }

    private static object? FindMainMenu()
    {
        object? nGame = GetStaticPropertyValue("MegaCrit.Sts2.Core.Nodes.NGame", "Instance");
        object? mainMenu = FindMemberValue(nGame, "MainMenu", "mainMenu", "_mainMenu");
        if (mainMenu is not null)
        {
            return mainMenu;
        }

        object? rootSceneContainer = FindMemberValue(nGame, "RootSceneContainer", "rootSceneContainer", "_rootSceneContainer");
        object? currentScene = FindMemberValue(rootSceneContainer, "CurrentScene", "currentScene", "_currentScene");
        if (IsMainMenuLike(currentScene))
        {
            return currentScene;
        }

        return EnumerateNodeDescendants(rootSceneContainer)
            .FirstOrDefault(IsMainMenuLike);
    }

    private static bool IsMainMenuLike(object? source)
    {
        if (source is null)
        {
            return false;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        return typeName.Contains("NMainMenu", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("MainMenu", StringComparison.OrdinalIgnoreCase)
            && TryInvokeBoolMethod(source, "IsVisibleInTree") == true;
    }

    private static Dictionary<string, object?> BuildMainMenuState(object mainMenu)
    {
        object? runManager = GetStaticPropertyValue("MegaCrit.Sts2.Core.Runs.RunManager", "Instance");
        object? continueButton = FindMemberValue(mainMenu, "_continueButton", "ContinueButton", "continueButton");
        object? singleplayerButton = FindMemberValue(mainMenu, "_singleplayerButton", "SingleplayerButton", "singleplayerButton");
        object? readRunSaveResult = FindMemberValue(mainMenu, "_readRunSaveResult", "ReadRunSaveResult", "readRunSaveResult");
        bool canContinue = IsMainMenuContinueUsable(continueButton, readRunSaveResult);
        bool canStartNewRun = IsMainMenuButtonUsable(singleplayerButton);
        List<Dictionary<string, object?>> legalActions = new();
        if (canContinue)
        {
            legalActions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = "continue_run",
                ["type"] = "continue_run",
                ["summary"] = "저장된 런을 이어서 진행한다.",
                ["validation_note"] = "메인 메뉴의 계속 버튼이 보이고, 활성화되어 있으며, 저장 데이터를 읽은 상태입니다."
            });
        }

        if (!canContinue && canStartNewRun)
        {
            legalActions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = "start_new_run",
                ["type"] = "start_new_run",
                ["summary"] = "저장된 런이 없으므로 싱글플레이 새 런을 시작한다.",
                ["validation_note"] = "메인 메뉴의 싱글플레이 버튼이 보이고 활성화된 상태입니다."
            });
        }

        return new Dictionary<string, object?>
        {
            ["schema_version"] = "combat_state.v1",
            ["phase"] = "main_menu",
            ["state_id"] = "main_menu_pending",
            ["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["run"] = new Dictionary<string, object?>
            {
                ["game"] = "Slay the Spire 2",
                ["mode"] = null,
                ["run_in_progress"] = ReadBool(runManager, "IsInProgress", "isInProgress", "_isInProgress")
            },
            ["player"] = new Dictionary<string, object?>(),
            ["piles"] = BuildEmptyPiles(),
            ["enemies"] = new List<Dictionary<string, object?>>(),
            ["main_menu"] = new Dictionary<string, object?>
            {
                ["screen_type"] = mainMenu.GetType().FullName ?? mainMenu.GetType().Name,
                ["visible_in_tree"] = TryInvokeBoolMethod(mainMenu, "IsVisibleInTree"),
                ["continue_button"] = BuildButtonState(continueButton),
                ["singleplayer_button"] = BuildButtonState(singleplayerButton),
                ["read_run_save_result_found"] = readRunSaveResult is not null,
                ["read_run_save_success"] = ReadBool(readRunSaveResult, "Success", "success", "_success"),
                ["save_data_found"] = FindMemberValue(readRunSaveResult, "SaveData", "saveData", "_saveData") is not null,
                ["can_continue"] = canContinue,
                ["can_start_new_run"] = canStartNewRun
            },
            ["legal_actions"] = legalActions,
            ["relics"] = new List<Dictionary<string, object?>>(),
            ["debug"] = new Dictionary<string, object?>
            {
                ["current_root_type"] = mainMenu.GetType().FullName ?? mainMenu.GetType().Name,
                ["run_manager_type"] = runManager?.GetType().FullName ?? runManager?.GetType().Name,
                ["observed_types"] = LastObservedTypes.ToArray()
            }
        };
    }

    private static bool IsMainMenuContinueUsable(object? continueButton, object? readRunSaveResult)
    {
        bool buttonEnabled = ReadBool(continueButton, "IsEnabled", "isEnabled", "_isEnabled") == true;
        bool buttonVisible = TryInvokeBoolMethod(continueButton, "IsVisible") == true;
        bool buttonVisibleInTree = TryInvokeBoolMethod(continueButton, "IsVisibleInTree") == true;
        bool readSaveSuccess = ReadBool(readRunSaveResult, "Success", "success", "_success") == true;
        bool saveDataFound = FindMemberValue(readRunSaveResult, "SaveData", "saveData", "_saveData") is not null;
        return continueButton is not null
            && buttonEnabled
            && buttonVisible
            && buttonVisibleInTree
            && readRunSaveResult is not null
            && readSaveSuccess
            && saveDataFound;
    }

    private static bool IsMainMenuButtonUsable(object? button)
    {
        return button is not null
            && ReadBool(button, "IsEnabled", "isEnabled", "_isEnabled") == true
            && TryInvokeBoolMethod(button, "IsVisible") == true
            && TryInvokeBoolMethod(button, "IsVisibleInTree") == true;
    }

    private static object? FindGameOverScreen()
    {
        object? screenContext = GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext",
            "Instance");
        object? currentScreen = TryInvokeMethod(screenContext, "GetCurrentScreen");
        if (IsGameOverScreen(currentScreen))
        {
            return currentScreen;
        }

        object? overlayStack = GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack",
            "Instance");
        object? topOverlay = TryInvokeMethod(overlayStack, "Peek");
        if (IsGameOverScreen(topOverlay))
        {
            return topOverlay;
        }

        return EnumerateNodeDescendants(overlayStack)
            .FirstOrDefault(IsGameOverScreen);
    }

    private static bool IsGameOverScreen(object? source)
    {
        if (source is null)
        {
            return false;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        if (typeName.Contains("NGameOverScreen", StringComparison.OrdinalIgnoreCase))
        {
            return IsLiveVisibleControl(source);
        }

        string? screenType = ReadString(source, "ScreenType", "screenType", "_screenType");
        return !string.IsNullOrWhiteSpace(screenType)
            && screenType.Contains("GameOver", StringComparison.OrdinalIgnoreCase)
            && IsLiveVisibleControl(source);
    }

    private static Dictionary<string, object?> BuildGameOverScreenState(object gameOverScreen, object? runState, object? history)
    {
        bool? isVictory = ReadBool(history, "Win", "win", "_win")
            ?? ReadBool(FindMemberValue(runState, "CurrentRoom", "currentRoom", "_currentRoom"), "IsVictoryRoom", "isVictoryRoom", "_isVictoryRoom");
        object? continueButton = FindMemberValue(gameOverScreen, "_continueButton", "continueButton", "ContinueButton");
        object? mainMenuButton = FindMemberValue(gameOverScreen, "_mainMenuButton", "mainMenuButton", "MainMenuButton");
        object? viewRunButton = FindMemberValue(gameOverScreen, "_viewRunButton", "viewRunButton", "ViewRunButton");

        return new Dictionary<string, object?>
        {
            ["screen_type"] = gameOverScreen.GetType().FullName ?? gameOverScreen.GetType().Name,
            ["result"] = isVictory == true ? "victory" : "defeat",
            ["is_victory"] = isVictory,
            ["score"] = ReadInt(gameOverScreen, "_score", "score", "Score"),
            ["score_threshold"] = ReadInt(gameOverScreen, "_scoreThreshold", "scoreThreshold", "ScoreThreshold"),
            ["floor"] = ReadInt(runState, "TotalFloor", "totalFloor", "_totalFloor"),
            ["ascension"] = ReadInt(runState, "AscensionLevel", "ascensionLevel", "_ascensionLevel"),
            ["game_mode"] = ReadString(runState, "GameMode", "gameMode", "_gameMode"),
            ["continue_button"] = BuildButtonState(continueButton),
            ["main_menu_button"] = BuildButtonState(mainMenuButton),
            ["view_run_button"] = BuildButtonState(viewRunButton),
            ["summary_animating"] = ReadBool(gameOverScreen, "_isAnimatingSummary", "isAnimatingSummary", "IsAnimatingSummary")
        };
    }

    private static Dictionary<string, object?> BuildButtonState(object? button)
    {
        if (button is null)
        {
            return new Dictionary<string, object?>
            {
                ["found"] = false
            };
        }

        return new Dictionary<string, object?>
        {
            ["found"] = true,
            ["type_name"] = button.GetType().FullName ?? button.GetType().Name,
            ["visible"] = ReadBool(button, "Visible", "visible"),
            ["visible_in_tree"] = TryInvokeBoolMethod(button, "IsVisibleInTree"),
            ["enabled"] = ReadBool(button, "IsEnabled", "isEnabled", "_isEnabled")
        };
    }

    private static Dictionary<string, object?> BuildGameOverDebug(object gameOverScreen, object? runState, object? history, ObjectGraph graph)
    {
        return new Dictionary<string, object?>
        {
            ["current_root_type"] = gameOverScreen.GetType().FullName ?? gameOverScreen.GetType().Name,
            ["run_state_type"] = runState?.GetType().FullName ?? runState?.GetType().Name,
            ["history_type"] = history?.GetType().FullName ?? history?.GetType().Name,
            ["observed_types"] = LastObservedTypes.ToArray(),
            ["graph_node_count"] = graph.Nodes.Count
        };
    }

    private static Dictionary<string, object?> BuildCardSelectionState(object cardSelectionScreen)
    {
        object? managerCombatState = ReadCombatManagerDebugState(GetCombatManagerInstance());
        if (managerCombatState is not null)
        {
            RememberObservedRoot(managerCombatState);
        }

        object? runManager = GetStaticPropertyValue("MegaCrit.Sts2.Core.Runs.RunManager", "Instance");
        object? runState = FindMemberValue(runManager, "RunState", "runState", "_runState", "State", "state");
        object? player = recentPlayer
            ?? recentPlayerCombatState
            ?? EnumerateObjects(FindMemberValue(runState, "Players", "players", "_players")).FirstOrDefault();
        object? relicsSource = FindMemberValue(player, "relics")
            ?? FindMemberValue(runState, "relics");

        List<object> roots = new();
        AddRoot(roots, cardSelectionScreen);
        AddRoot(roots, runState);
        AddRoot(roots, player);
        AddRoot(roots, managerCombatState);
        ObjectGraph graph = ObjectGraph.Collect(roots, 3, 260);

        Dictionary<string, object?> cardSelection = BuildCardSelectionScreenState(cardSelectionScreen);
        List<Dictionary<string, object?>> cards = ReadDictionaryList(cardSelection, "cards");

        return new Dictionary<string, object?>
        {
            ["schema_version"] = "combat_state.v1",
            ["phase"] = "card_selection",
            ["state_id"] = "card_selection_pending",
            ["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["run"] = BuildRun(runState ?? managerCombatState ?? cardSelectionScreen, graph),
            ["player"] = player is null ? new Dictionary<string, object?>() : BuildPlayer(player),
            ["piles"] = BuildEmptyPiles(),
            ["enemies"] = new List<Dictionary<string, object?>>(),
            ["card_selection"] = cardSelection,
            ["legal_actions"] = BuildCardSelectionLegalActions(cardSelection, cards),
            ["relics"] = BuildRelics(relicsSource, graph),
            ["debug"] = BuildCardSelectionDebug(cardSelectionScreen, graph)
        };
    }

    private static Dictionary<string, object?> BuildAdapterCardSelectionState(PendingAdapterCardSelection pending, object combatRoot)
    {
        object? managerCombatState = ReadCombatManagerDebugState(GetCombatManagerInstance());
        if (managerCombatState is not null)
        {
            RememberObservedRoot(managerCombatState);
        }

        List<object> roots = GetExportRoots(combatRoot, managerCombatState);
        ObjectGraph graph = ObjectGraph.Collect(roots, MaxSearchDepth, MaxVisitedObjects);
        object? player = ResolveRuntimePlayer(combatRoot, graph)
            ?? recentPlayer
            ?? recentPlayerCombatState
            ?? FindFirst(graph, "Player", "PlayerCombatState");
        object? runManager = GetRunManagerInstance();
        object? runState = ReadRunManagerDebugState(runManager)
            ?? FindMemberValue(runManager, "RunState", "runState", "_runState", "State", "state")
            ?? FindMemberValue(combatRoot, "RunState", "runState", "_runState", "State", "state");
        object? relicsSource = player ?? runState ?? combatRoot;

        List<Dictionary<string, object?>> cards = BuildAdapterCardSelectionCards(pending);
        string? sourcePile = InferCardSelectionSourcePile(cards);
        CardSelectionSourceHint? sourceHint = ResolveCardSelectionSourceHint();
        int selectedCount = pending.SelectedIndexes.Count;
        Dictionary<string, object?> cardSelection = new()
        {
            ["selection_id"] = pending.SelectionId,
            ["selection_kind"] = "adapter_card_selection",
            ["selection_purpose"] = null,
            ["selection_purpose_source"] = "not_inferred_by_adapter",
            ["source_card_id"] = sourceHint?.CardId,
            ["source_card_name"] = sourceHint?.CardName,
            ["source_upgraded"] = sourceHint?.Upgraded,
            ["source_card_observation_source"] = sourceHint is null ? null : "last_play_card_action",
            ["source_pile"] = sourcePile,
            ["prompt"] = null,
            ["prompt_source"] = "selector_interface_does_not_expose_prompt",
            ["min_select"] = pending.MinSelect,
            ["max_select"] = pending.MaxSelect,
            ["selected_count"] = selectedCount,
            ["can_confirm"] = selectedCount >= pending.MinSelect,
            ["can_cancel"] = pending.MinSelect <= 0,
            ["cards"] = cards
        };

        return new Dictionary<string, object?>
        {
            ["schema_version"] = "combat_state.v1",
            ["phase"] = "adapter_card_selection",
            ["state_id"] = pending.SelectionId,
            ["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["run"] = BuildRun(runState ?? managerCombatState ?? combatRoot, graph),
            ["player"] = player is null ? new Dictionary<string, object?>() : BuildPlayer(player),
            ["piles"] = BuildEmptyPiles(),
            ["enemies"] = new List<Dictionary<string, object?>>(),
            ["card_selection"] = cardSelection,
            ["legal_actions"] = BuildCardSelectionLegalActions(cardSelection, cards),
            ["relics"] = BuildRelics(relicsSource, graph),
            ["debug"] = new Dictionary<string, object?>
            {
                ["current_root_type"] = "AdapterCardSelectionBridge",
                ["selection_started_at_ms"] = pending.StartedAtMs,
                ["option_count"] = pending.Options.Count,
                ["selected_indexes"] = pending.SelectedIndexes.ToArray()
            }
        };
    }

    private static List<Dictionary<string, object?>> BuildAdapterCardSelectionCards(PendingAdapterCardSelection pending)
    {
        List<Dictionary<string, object?>> cards = new();
        HashSet<int> selectedIndexes = pending.SelectedIndexes.ToHashSet();

        foreach (AdapterCardSelectionOption option in pending.Options)
        {
            Dictionary<string, object?> cardState = BuildCards("card_selection", new[] { option.Card }).FirstOrDefault()
                ?? new Dictionary<string, object?>();
            cardState["card_selection_id"] = option.CardSelectionId;
            cardState["card_selection_index"] = option.Index;
            cardState["card_selection_key"] = SanitizeActionId($"adapter_{option.CardId}_{option.Name}_{(option.Upgraded ? "upgraded" : "base")}");
            cardState["card_id"] = option.CardId;
            cardState["name"] = option.Name;
            cardState["upgraded"] = option.Upgraded;
            cardState["pile"] = option.Pile ?? ReadDictionaryString(cardState, "pile");
            cardState["selected"] = selectedIndexes.Contains(option.Index);
            cardState["visible"] = true;
            cardState["visible_in_tree"] = false;
            cardState["clickable"] = true;
            cardState["holder_type"] = "AdapterCardSelectionOption";
            cardState["runtime_type"] = option.Card.GetType().FullName ?? option.Card.GetType().Name;
            cardState["observation_source"] = "CardSelectCmd.ICardSelector.options";
            cards.Add(cardState);
        }

        return cards;
    }

    private static object? FindCardSelectionScreen()
    {
        object? overlayStack = GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack",
            "Instance");
        object? topOverlay = TryInvokeMethod(overlayStack, "Peek");
        if (IsCardSelectionScreen(topOverlay))
        {
            return topOverlay;
        }

        object? screenContext = GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext",
            "Instance");
        object? currentScreen = TryInvokeMethod(screenContext, "GetCurrentScreen");
        if (IsCardSelectionScreen(currentScreen))
        {
            return currentScreen;
        }

        return EnumerateNodeDescendants(overlayStack)
            .FirstOrDefault(IsCardSelectionScreen);
    }

    private static object? FindHandCardSelectionRoot()
    {
        object? handTypeInstance = GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand",
            "Instance");
        if (IsHandCardSelectionRoot(handTypeInstance))
        {
            return handTypeInstance;
        }

        object? combatRoom = GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom",
            "Instance");
        object? combatUi = FindMemberValue(combatRoom, "Ui", "ui", "_ui");
        object? hand = FindMemberValue(combatUi, "Hand", "hand", "_hand");
        return IsHandCardSelectionRoot(hand) ? hand : null;
    }

    private static bool IsHandCardSelectionRoot(object? source)
    {
        if (source is null)
        {
            return false;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        return typeName.Contains("NPlayerHand", StringComparison.OrdinalIgnoreCase)
            && ReadBool(source, "IsInCardSelection", "isInCardSelection") == true;
    }

    private static bool IsCardSelectionScreen(object? source)
    {
        if (source is null)
        {
            return false;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        return IsLiveVisibleControl(source)
            && ContainsAny(
                typeName,
                "NDeckEnchantSelectScreen",
                "NDeckUpgradeSelectScreen",
                "NDeckTransformSelectScreen",
                "NDeckCardSelectScreen",
                "NChooseACardSelectionScreen",
                "NSimpleCardSelectScreen",
                "SimpleCardSelectScreen");
    }

    private static Dictionary<string, object?> BuildCardSelectionScreenState(object cardSelectionScreen)
    {
        if (IsHandCardSelectionRoot(cardSelectionScreen))
        {
            return BuildHandCardSelectionScreenState(cardSelectionScreen);
        }

        object? prefs = FindMemberValue(cardSelectionScreen, "_prefs", "Prefs", "prefs");
        object? selectedCardsSource = FindMemberValue(cardSelectionScreen, "_selectedCards", "SelectedCards", "selectedCards");
        List<object> selectedCards = EnumerateObjects(selectedCardsSource).ToList();
        int? minSelect = ReadInt(prefs, "MinSelect", "minSelect", "_minSelect");
        int? maxSelect = ReadInt(prefs, "MaxSelect", "maxSelect", "_maxSelect");
        object? prompt = FindMemberValue(prefs, "Prompt", "prompt", "_prompt");
        string? promptText = ReadFormattedText(prompt);
        object? confirmButton = FindCardSelectionConfirmButton(cardSelectionScreen);
        string selectionKind = InferCardSelectionKind(cardSelectionScreen, promptText);
        List<Dictionary<string, object?>> cards = BuildCardSelectionCards(cardSelectionScreen, selectedCards, selectionKind);
        string? sourcePile = InferCardSelectionSourcePile(cards);
        CardSelectionSourceHint? sourceHint = ResolveCardSelectionSourceHint();
        Dictionary<string, object?> confirmButtonState = BuildButtonState(confirmButton);
        bool canConfirm = ReadDictionaryBool(confirmButtonState, "enabled") == true
            || (selectedCards.Count >= Math.Max(1, minSelect ?? 1));

        return new Dictionary<string, object?>
        {
            ["screen_type"] = cardSelectionScreen.GetType().FullName ?? cardSelectionScreen.GetType().Name,
            ["selection_kind"] = selectionKind,
            ["selection_purpose"] = null,
            ["selection_purpose_source"] = "not_inferred_by_adapter",
            ["source_card_id"] = sourceHint?.CardId,
            ["source_card_name"] = sourceHint?.CardName,
            ["source_card_upgraded"] = sourceHint?.Upgraded,
            ["source_card_observation_source"] = sourceHint is null ? null : "last_play_card_action",
            ["source_pile"] = sourcePile,
            ["prompt"] = promptText,
            ["min_select"] = minSelect,
            ["max_select"] = maxSelect,
            ["selected_count"] = selectedCards.Count,
            ["can_confirm"] = canConfirm,
            ["preview_visible"] = IsAnyCardSelectionPreviewVisible(cardSelectionScreen),
            ["confirm_button"] = confirmButtonState,
            ["cards"] = cards
        };
    }

    private static Dictionary<string, object?> BuildHandCardSelectionScreenState(object hand)
    {
        object? prefs = FindMemberValue(hand, "_prefs", "Prefs", "prefs");
        object? selectedCardsSource = FindMemberValue(hand, "_selectedCards", "SelectedCards", "selectedCards");
        List<object> selectedCards = EnumerateObjects(selectedCardsSource).ToList();
        string currentMode = ReadString(hand, "CurrentMode", "_currentMode") ?? string.Empty;
        int? minSelect = ReadInt(prefs, "MinSelect", "minSelect", "_minSelect") ?? 1;
        int? maxSelect = ReadInt(prefs, "MaxSelect", "maxSelect", "_maxSelect") ?? 1;
        object? prompt = FindMemberValue(prefs, "Prompt", "prompt", "_prompt");
        string? promptText = ReadFormattedText(prompt);
        object? confirmButton = FindCardSelectionConfirmButton(hand);
        string selectionKind = currentMode.Contains("Upgrade", StringComparison.OrdinalIgnoreCase)
            ? "upgrade"
            : "choose_one";
        List<Dictionary<string, object?>> cards = BuildCardSelectionCards(hand, selectedCards, selectionKind);
        CardSelectionSourceHint? sourceHint = ResolveCardSelectionSourceHint();
        Dictionary<string, object?> confirmButtonState = BuildButtonState(confirmButton);
        bool canConfirm = ReadDictionaryBool(confirmButtonState, "enabled") == true
            || selectedCards.Count >= Math.Max(1, minSelect ?? 1);

        return new Dictionary<string, object?>
        {
            ["screen_type"] = hand.GetType().FullName ?? hand.GetType().Name,
            ["selection_kind"] = selectionKind,
            ["selection_purpose"] = null,
            ["selection_purpose_source"] = "not_inferred_by_adapter",
            ["source_card_id"] = sourceHint?.CardId,
            ["source_card_name"] = sourceHint?.CardName,
            ["source_card_upgraded"] = sourceHint?.Upgraded,
            ["source_card_observation_source"] = sourceHint is null ? null : "last_play_card_action",
            ["source_pile"] = "hand",
            ["prompt"] = promptText,
            ["min_select"] = minSelect,
            ["max_select"] = maxSelect,
            ["selected_count"] = selectedCards.Count,
            ["can_confirm"] = canConfirm,
            ["preview_visible"] = IsAnyCardSelectionPreviewVisible(hand),
            ["confirm_button"] = confirmButtonState,
            ["cards"] = cards
        };
    }

    private static string InferCardSelectionKind(object cardSelectionScreen, string? prompt)
    {
        if (!string.IsNullOrWhiteSpace(prompt)
            && ContainsAny(prompt, "Remove", "Removal", "Purge", "제거"))
        {
            return "remove";
        }

        string typeName = cardSelectionScreen.GetType().FullName ?? cardSelectionScreen.GetType().Name;
        if (typeName.Contains("Enchant", StringComparison.OrdinalIgnoreCase))
        {
            return "enchant";
        }

        if (typeName.Contains("Upgrade", StringComparison.OrdinalIgnoreCase))
        {
            return "upgrade";
        }

        if (typeName.Contains("Transform", StringComparison.OrdinalIgnoreCase))
        {
            return "transform";
        }

        if (typeName.Contains("DeckCardSelect", StringComparison.OrdinalIgnoreCase))
        {
            return "deck_card_select";
        }

        if (typeName.Contains("ChooseACardSelection", StringComparison.OrdinalIgnoreCase))
        {
            return "choose_one";
        }

        return "unknown";
    }

    private static string InferCardSelectionPurpose(string selectionKind, string? prompt, string? sourcePile)
    {
        string text = $"{selectionKind} {prompt ?? string.Empty} {sourcePile ?? string.Empty}";
        if (ContainsAny(text, "upgrade", "강화"))
        {
            return "upgrade_from_hand";
        }

        if (ContainsAny(text, "discard", "버림", "버려"))
        {
            return "discard_from_hand";
        }

        if (ContainsAny(text, "exhaust", "소멸"))
        {
            return "exhaust_from_hand";
        }

        if (ContainsAny(text, "transform", "변화", "변형"))
        {
            return "transform";
        }

        if (string.Equals(sourcePile, "discard_pile", StringComparison.OrdinalIgnoreCase))
        {
            return "move_from_discard_to_draw_top";
        }

        if (string.Equals(sourcePile, "draw_pile", StringComparison.OrdinalIgnoreCase))
        {
            return "put_back_on_draw_top";
        }

        if (string.Equals(sourcePile, "generated_choices", StringComparison.OrdinalIgnoreCase))
        {
            return "choose_generated_card";
        }

        return selectionKind;
    }

    private static string? InferCardSelectionSourcePile(List<Dictionary<string, object?>> cards)
    {
        List<string> piles = cards
            .Select(card => ReadDictionaryString(card, "pile"))
            .Where(pile => !string.IsNullOrWhiteSpace(pile))
            .Select(pile => pile!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return piles.Count == 1 ? piles[0] : null;
    }

    private static CardSelectionSourceHint? ResolveCardSelectionSourceHint()
    {
        CardSelectionSourceHint? remembered = latestCardSelectionSourceHint;
        if (remembered is not null && Environment.TickCount64 - remembered.ObservedAtMs <= CardSelectionSourceRememberMs)
        {
            return remembered;
        }

        return null;
    }

    private static List<Dictionary<string, object?>> BuildCardSelectionCards(object cardSelectionScreen, List<object> selectedCards, string selectionKind)
    {
        List<object> holders = FindGridCardHolders(cardSelectionScreen);
        List<Dictionary<string, object?>> cards = new();
        for (int index = 0; index < holders.Count; index++)
        {
            object holder = holders[index];
            object? card = ExtractCardFromHolder(holder);
            if (card is null)
            {
                continue;
            }

            Dictionary<string, object?> cardState = BuildCards("card_selection", new[] { card }).FirstOrDefault()
                ?? new Dictionary<string, object?>();
            string name = ReadDictionaryString(cardState, "name")
                ?? ReadCardName(card)
                ?? GetReadableName(card);
            string cardId = ReadDictionaryString(cardState, "card_id")
                ?? ReadString(card, "Id", "id", "_id")
                ?? name;
            bool? upgraded = ReadDictionaryBool(cardState, "upgraded");
            string cardSelectionId = BuildCardSelectionId(selectionKind, index, cardId, name, upgraded);
            cardState["card_selection_id"] = cardSelectionId;
            cardState["card_selection_index"] = index;
            cardState["card_selection_key"] = SanitizeActionId($"{selectionKind}_{cardId}_{name}_{(upgraded == true ? "upgraded" : "base")}");
            cardState["pile"] = InferCardPileName(card);
            cardState["selected"] = IsCardSelected(card, selectedCards);
            cardState["visible"] = ReadBool(holder, "Visible", "visible");
            cardState["visible_in_tree"] = TryInvokeBoolMethod(holder, "IsVisibleInTree");
            cardState["clickable"] = ReadBool(holder, "_isClickable", "isClickable", "IsClickable");
            cardState["holder_type"] = holder.GetType().FullName ?? holder.GetType().Name;
            cards.Add(cardState);
        }

        return cards;
    }

    private static string? InferCardPileName(object card)
    {
        object? pile = FindMemberValue(card, "Pile", "pile", "_pile", "CardPile", "cardPile", "_cardPile");
        string rawPile = ReadString(pile, "Type", "type", "PileType", "pileType", "Name", "name", "Id", "id")
            ?? pile?.ToString()
            ?? string.Empty;
        string cardTypeName = card.GetType().FullName ?? card.GetType().Name;
        string text = $"{rawPile} {cardTypeName}";
        if (ContainsAny(text, "Discard"))
        {
            return "discard_pile";
        }

        if (ContainsAny(text, "Draw"))
        {
            return "draw_pile";
        }

        if (ContainsAny(text, "Hand"))
        {
            return "hand";
        }

        if (ContainsAny(text, "Deck"))
        {
            return "deck";
        }

        if (ContainsAny(text, "Generated", "Choice", "Reward"))
        {
            return "generated_choices";
        }

        return string.IsNullOrWhiteSpace(rawPile) ? null : SanitizeActionId(rawPile);
    }

    private static string BuildCardSelectionId(string selectionKind, int index, string cardId, string name, bool? upgraded)
    {
        string upgradeToken = upgraded == true ? "upgraded" : "base";
        return SanitizeActionId($"card_selection_{selectionKind}_{index}_{cardId}_{name}_{upgradeToken}");
    }

    private static List<object> FindGridCardHolders(object cardSelectionScreen)
    {
        if (IsHandCardSelectionRoot(cardSelectionScreen))
        {
            object? activeHolders = FindMemberValue(cardSelectionScreen, "ActiveHolders");
            List<object> handHolders = EnumerateObjects(activeHolders)
                .Where(IsHandCardHolder)
                .Where(holder => ReadBool(holder, "Visible", "visible") != false)
                .ToList();
            if (handHolders.Count > 0)
            {
                return handHolders;
            }

            object? holderContainer = FindMemberValue(cardSelectionScreen, "CardHolderContainer", "_cardHolderContainer");
            return EnumerateNodeDescendants(holderContainer)
                .Where(IsHandCardHolder)
                .Where(holder => ReadBool(holder, "Visible", "visible") != false)
                .ToList();
        }

        object? grid = FindMemberValue(cardSelectionScreen, "_grid", "Grid", "grid");
        List<object> holders = EnumerateNodeDescendants(grid)
            .Where(IsGridCardHolder)
            .ToList();
        if (holders.Count > 0)
        {
            return holders;
        }

        return EnumerateNodeDescendants(cardSelectionScreen)
            .Where(IsGridCardHolder)
            .ToList();
    }

    private static bool IsGridCardHolder(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return typeName.Contains("NGridCardHolder", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHandCardHolder(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return typeName.Contains("NHandCardHolder", StringComparison.OrdinalIgnoreCase);
    }

    private static object? ExtractCardFromHolder(object holder)
    {
        object? cardModel = FindMemberValue(holder, "CardModel", "cardModel", "_cardModel", "_baseCard");
        if (cardModel is not null)
        {
            return cardModel;
        }

        object? cardNode = FindMemberValue(holder, "CardNode", "cardNode", "_cardNode");
        return FindMemberValue(cardNode, "Model", "model", "_model") ?? cardNode;
    }

    private static bool IsCardSelected(object card, List<object> selectedCards)
    {
        return selectedCards.Any(selectedCard => ReferenceEquals(selectedCard, card) || selectedCard.Equals(card));
    }

    private static bool IsAnyCardSelectionPreviewVisible(object cardSelectionScreen)
    {
        string[] previewMemberNames =
        {
            "_enchantSinglePreviewContainer",
            "_enchantMultiPreviewContainer",
            "_upgradeSinglePreviewContainer",
            "_upgradeMultiPreviewContainer",
            "_previewContainer"
        };

        return previewMemberNames
            .Select(name => FindMemberValue(cardSelectionScreen, name))
            .Any(preview => preview is not null && ReadBool(preview, "Visible", "visible") == true);
    }

    private static object? FindCardSelectionConfirmButton(object cardSelectionScreen)
    {
        if (IsHandCardSelectionRoot(cardSelectionScreen))
        {
            return FindMemberValue(cardSelectionScreen, "_selectModeConfirmButton", "SelectModeConfirmButton", "selectModeConfirmButton");
        }

        string[] preferredMemberNames =
        {
            "_singlePreviewConfirmButton",
            "_multiPreviewConfirmButton",
            "_previewConfirmButton",
            "_confirmButton"
        };

        List<object> buttons = preferredMemberNames
            .Select(name => FindMemberValue(cardSelectionScreen, name))
            .Where(button => button is not null)
            .Cast<object>()
            .ToList();

        return buttons.FirstOrDefault(button => ReadBool(button, "IsEnabled", "isEnabled", "_isEnabled") == true)
            ?? buttons.FirstOrDefault();
    }

    private static List<Dictionary<string, object?>> BuildCardSelectionLegalActions(
        Dictionary<string, object?> cardSelection,
        List<Dictionary<string, object?>> cards)
    {
        List<Dictionary<string, object?>> actions = new();
        string selectionKind = ReadDictionaryString(cardSelection, "selection_kind") ?? "unknown";
        string? selectionPurpose = ReadDictionaryString(cardSelection, "selection_purpose");
        string? sourceCardId = ReadDictionaryString(cardSelection, "source_card_id");
        string? sourceCardName = ReadDictionaryString(cardSelection, "source_card_name");
        string? sourcePile = ReadDictionaryString(cardSelection, "source_pile");
        string? selectionId = ReadDictionaryString(cardSelection, "selection_id");
        int? selectedCount = ReadDictionaryInt(cardSelection, "selected_count");
        int? minSelect = ReadDictionaryInt(cardSelection, "min_select");
        int? maxSelect = ReadDictionaryInt(cardSelection, "max_select");
        int requiredSelect = Math.Max(1, minSelect ?? 1);
        bool canChooseMore = maxSelect is null || (selectedCount ?? 0) < maxSelect.Value;
        bool canConfirm = ReadDictionaryBool(cardSelection, "can_confirm") == true;
        bool canCancel = ReadDictionaryBool(cardSelection, "can_cancel") != false;

        if (canChooseMore)
        {
            foreach (Dictionary<string, object?> card in cards)
            {
                if (ReadDictionaryBool(card, "selected") == true)
                {
                    continue;
                }

                int? cardSelectionIndex = ReadDictionaryInt(card, "card_selection_index");
                string cardSelectionId = ReadDictionaryString(card, "card_selection_id") ?? $"card_selection_{cardSelectionIndex?.ToString() ?? "unknown"}";
                string name = ReadDictionaryString(card, "name") ?? cardSelectionId;
                actions.Add(new Dictionary<string, object?>
                {
                    ["action_id"] = SanitizeActionId($"choose_{cardSelectionId}"),
                    ["type"] = "choose_card_selection",
                    ["card_selection_id"] = cardSelectionId,
                    ["card_selection_index"] = cardSelectionIndex,
                    ["card_selection_key"] = ReadDictionaryString(card, "card_selection_key"),
                    ["selection_id"] = selectionId,
                    ["selection_kind"] = selectionKind,
                    ["selection_purpose"] = selectionPurpose,
                    ["source_card_id"] = sourceCardId,
                    ["source_card_name"] = sourceCardName,
                    ["source_pile"] = sourcePile,
                    ["card_id"] = ReadDictionaryString(card, "card_id"),
                    ["name"] = name,
                    ["card_type"] = ReadDictionaryString(card, "type"),
                    ["rarity"] = ReadDictionaryString(card, "rarity"),
                    ["cost"] = ReadDictionaryInt(card, "cost"),
                    ["upgraded"] = ReadDictionaryBool(card, "upgraded"),
                    ["pile"] = ReadDictionaryString(card, "pile"),
                    ["selected_count"] = selectedCount,
                    ["min_select"] = minSelect,
                    ["max_select"] = maxSelect,
                    ["summary"] = $"Choose card selection: {name}",
                    ["validation_note"] = "Visible card selection holder."
                });
            }
        }

        if ((selectedCount ?? 0) >= requiredSelect && canConfirm)
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = "confirm_card_selection",
                ["type"] = "confirm_card_selection",
                ["selection_id"] = selectionId,
                ["selection_kind"] = selectionKind,
                ["selection_purpose"] = selectionPurpose,
                ["source_card_id"] = sourceCardId,
                ["source_card_name"] = sourceCardName,
                ["source_pile"] = sourcePile,
                ["selected_count"] = selectedCount,
                ["min_select"] = minSelect,
                ["max_select"] = maxSelect,
                ["summary"] = "Confirm current card selection.",
                ["validation_note"] = "A card selection is already active."
            });
        }

        if (canCancel)
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = "cancel_card_selection",
                ["type"] = "cancel_card_selection",
                ["selection_kind"] = selectionKind,
                ["selection_purpose"] = selectionPurpose,
                ["source_card_id"] = sourceCardId,
                ["source_card_name"] = sourceCardName,
                ["source_pile"] = sourcePile,
                ["selected_count"] = selectedCount,
                ["summary"] = "Cancel the current card selection and return to the previous screen when the UI supports it.",
                ["validation_note"] = "Visible card selection screen cancel/back action."
            });
        }

        return actions;
    }

    private static Dictionary<string, object?> BuildCardSelectionDebug(object cardSelectionScreen, ObjectGraph graph)
    {
        return new Dictionary<string, object?>
        {
            ["current_root_type"] = cardSelectionScreen.GetType().FullName ?? cardSelectionScreen.GetType().Name,
            ["observed_types"] = LastObservedTypes.ToArray(),
            ["graph_node_count"] = graph.Nodes.Count,
            ["grid_holder_count"] = FindGridCardHolders(cardSelectionScreen).Count
        };
    }

    private static Dictionary<string, object?> BuildEventState(object eventRoom)
    {
        object? managerCombatState = ReadCombatManagerDebugState(GetCombatManagerInstance());
        if (managerCombatState is not null)
        {
            RememberObservedRoot(managerCombatState);
        }

        object? eventModel = FindMemberValue(eventRoom, "_event", "Event", "event");
        object? layout = FindMemberValue(eventRoom, "Layout", "layout");
        object? owner = FindMemberValue(eventModel, "Owner", "owner", "_owner");
        object? runState = FindMemberValue(owner, "RunState", "runState", "_runState")
            ?? FindMemberValue(eventRoom, "_runState", "runState", "RunState")
            ?? FindMemberValue(GetStaticPropertyValue("MegaCrit.Sts2.Core.Runs.RunManager", "Instance"), "RunState", "runState", "_runState", "State", "state");
        object? player = recentPlayer
            ?? recentPlayerCombatState
            ?? owner
            ?? EnumerateObjects(FindMemberValue(runState, "Players", "players", "_players")).FirstOrDefault();
        object? relicsSource = FindMemberValue(player, "relics")
            ?? FindMemberValue(runState, "relics");

        List<object> roots = new();
        AddRoot(roots, eventRoom);
        AddRoot(roots, eventModel);
        AddRoot(roots, layout);
        AddRoot(roots, owner);
        AddRoot(roots, runState);
        AddRoot(roots, player);
        AddRoot(roots, managerCombatState);
        ObjectGraph graph = ObjectGraph.Collect(roots, 3, 260);

        Dictionary<string, object?> eventState = BuildEventScreenState(eventRoom, eventModel, layout);
        List<Dictionary<string, object?>> options = ReadDictionaryList(eventState, "options");
        string eventFingerprint = BuildEventFingerprint(eventState, options);
        eventState["fingerprint"] = eventFingerprint;

        return new Dictionary<string, object?>
        {
            ["schema_version"] = "combat_state.v1",
            ["phase"] = "event",
            ["state_id"] = "event_pending",
            ["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["run"] = BuildRun(runState ?? managerCombatState ?? eventRoom, graph),
            ["player"] = player is null ? new Dictionary<string, object?>() : BuildPlayer(player),
            ["piles"] = BuildEmptyPiles(),
            ["enemies"] = new List<Dictionary<string, object?>>(),
            ["event"] = eventState,
            ["legal_actions"] = BuildEventLegalActions(options, eventFingerprint),
            ["relics"] = BuildRelics(relicsSource, graph),
            ["debug"] = BuildEventDebug(eventRoom, eventModel, layout, graph)
        };
    }

    private static object? FindEventRoom()
    {
        object? room = GetStaticPropertyValue("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom", "Instance");
        if (IsEventRoomVisible(room))
        {
            return room;
        }

        object? screenContext = GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext",
            "Instance");
        object? currentScreen = TryInvokeMethod(screenContext, "GetCurrentScreen");
        if (IsEventRoomVisible(currentScreen))
        {
            return currentScreen;
        }

        object? run = GetStaticPropertyValue("MegaCrit.Sts2.Core.Nodes.Rooms.NRun", "Instance");
        object? runEventRoom = FindMemberValue(run, "EventRoom", "eventRoom", "_eventRoom");
        return IsEventRoomVisible(runEventRoom) ? runEventRoom : null;
    }

    private static bool IsFinishedEventCoveredByOpenMap(object eventRoom)
    {
        if (FindMapScreen() is null)
        {
            return false;
        }

        object? eventModel = FindMemberValue(eventRoom, "_event", "Event", "event");
        if (ReadBool(eventModel, "IsFinished", "isFinished", "_isFinished") == true)
        {
            return true;
        }

        object? layout = FindMemberValue(eventRoom, "Layout", "layout");
        return BuildEventOptions(layout, eventRoom)
            .Any(option =>
                ReadDictionaryBool(option, "is_proceed") == true
                && ReadDictionaryBool(option, "was_chosen") == true);
    }

    private static bool IsEventRoomVisible(object? source)
    {
        if (source is null)
        {
            return false;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        return typeName.Contains("NEventRoom", StringComparison.OrdinalIgnoreCase)
            && IsLiveVisibleControl(source);
    }

    private static Dictionary<string, object?> BuildEventScreenState(object eventRoom, object? eventModel, object? layout)
    {
        List<Dictionary<string, object?>> options = BuildEventOptions(layout, eventRoom, eventModel);
        string eventTypeName = eventModel?.GetType().FullName ?? eventModel?.GetType().Name ?? string.Empty;
        return new Dictionary<string, object?>
        {
            ["screen_type"] = eventRoom.GetType().FullName ?? eventRoom.GetType().Name,
            ["event_id"] = ReadString(eventModel, "Id", "id", "_id", "Key", "key", "TextKey", "textKey") ?? GetReadableName(eventModel ?? eventRoom),
            ["event_type_name"] = eventTypeName,
            ["title"] = ReadFormattedText(FindMemberValue(eventModel, "Title", "title", "_title")),
            ["description"] = ReadFormattedText(FindMemberValue(eventModel, "Description", "description", "_description")),
            ["is_finished"] = ReadBool(eventModel, "IsFinished", "isFinished", "_isFinished"),
            ["is_shared"] = ReadBool(eventModel, "IsShared", "isShared", "_isShared"),
            ["option_count"] = options.Count,
            ["options"] = options
        };
    }

    private static List<Dictionary<string, object?>> BuildEventOptions(object? layout, object eventRoom, object? eventModel = null)
    {
        List<object> optionButtons = EnumerateObjects(FindMemberValue(layout, "OptionButtons", "optionButtons")).ToList();
        if (optionButtons.Count == 0)
        {
            optionButtons = EnumerateNodeDescendants(eventRoom)
                .Where(IsEventOptionButton)
                .ToList();
        }

        List<Dictionary<string, object?>> options = new();
        string eventId = ReadString(eventModel, "Id", "id", "_id", "Key", "key", "TextKey", "textKey") ?? GetReadableName(eventModel ?? eventRoom);
        string eventTypeName = eventModel?.GetType().Name ?? string.Empty;
        for (int index = 0; index < optionButtons.Count; index++)
        {
            options.Add(BuildEventOption(optionButtons[index], index, eventId, eventTypeName));
        }

        return options;
    }

    private static Dictionary<string, object?> BuildEventOption(object button, int fallbackIndex, string eventId, string eventTypeName)
    {
        object? option = FindMemberValue(button, "Option", "option", "_option");
        int optionIndex = ReadInt(button, "Index", "index", "_index") ?? fallbackIndex;
        string optionId = $"event_option_{optionIndex}";
        string? title = ReadFormattedText(FindMemberValue(option, "Title", "title", "_title"));
        string? description = ReadFormattedText(FindMemberValue(option, "Description", "description", "_description"));
        string? textKey = ReadString(option, "TextKey", "textKey", "_textKey");
        Dictionary<string, object?> outcome = EventOutcomeInterpreter.BuildKnownOutcome(eventId, eventTypeName, textKey, title, description);
        return new Dictionary<string, object?>
        {
            ["event_option_id"] = optionId,
            ["event_option_index"] = optionIndex,
            ["text_key"] = textKey,
            ["title"] = title,
            ["description"] = description,
            ["is_locked"] = ReadBool(option, "IsLocked", "isLocked", "_isLocked"),
            ["is_proceed"] = ReadBool(option, "IsProceed", "isProceed", "_isProceed"),
            ["was_chosen"] = ReadBool(option, "WasChosen", "wasChosen", "_wasChosen"),
            ["is_enabled"] = ReadBool(button, "IsEnabled", "isEnabled", "_isEnabled"),
            ["adapter_confidence"] = outcome["adapter_confidence"],
            ["outcome_known_level"] = outcome["outcome_known_level"],
            ["runtime_warnings"] = outcome["runtime_warnings"],
            ["known_outcome"] = outcome["known_outcome"],
            ["type_name"] = option?.GetType().FullName ?? option?.GetType().Name,
            ["button_type_name"] = button.GetType().FullName ?? button.GetType().Name,
            ["fingerprint"] = BuildEventOptionFingerprint(optionId, optionIndex, title, description, textKey),
            ["summary"] = BuildEventOptionSummary(title, description, optionId)
        };
    }

    private static string BuildEventOptionSummary(string? title, string? description, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(description))
        {
            return $"{title}: {description}";
        }

        return title ?? description ?? fallback;
    }

    private static bool IsEventOptionButton(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return typeName.Contains("NEventOptionButton", StringComparison.OrdinalIgnoreCase);
    }

    private static List<Dictionary<string, object?>> BuildEventLegalActions(List<Dictionary<string, object?>> options, string eventFingerprint)
    {
        List<Dictionary<string, object?>> actions = new();
        foreach (Dictionary<string, object?> option in options)
        {
            if (ReadDictionaryBool(option, "is_locked") == true)
            {
                continue;
            }

            bool isEnabled = ReadDictionaryBool(option, "is_enabled") != false;
            bool wasChosen = ReadDictionaryBool(option, "was_chosen") == true;
            if (!isEnabled && !wasChosen)
            {
                continue;
            }

            int? optionIndex = ReadDictionaryInt(option, "event_option_index");
            string optionId = ReadDictionaryString(option, "event_option_id") ?? $"event_option_{optionIndex?.ToString() ?? "unknown"}";
            string summary = ReadDictionaryString(option, "summary") ?? optionId;
            string actionIdPrefix = wasChosen && !isEnabled ? "continue_chosen" : "choose";
            string actionSummary = wasChosen && !isEnabled
                ? $"Continue chosen event option: {summary}"
                : $"Choose event option: {summary}";
            string validationNote = wasChosen && !isEnabled
                ? "Already chosen event option button. Runtime execution rechecks option index and invokes the event release path again so combat-start transitions can finish."
                : "Visible event option button. Runtime execution rechecks option index and current visible event screen.";
            actions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = SanitizeActionId($"{actionIdPrefix}_{optionId}"),
                ["type"] = "choose_event_option",
                ["event_option_id"] = optionId,
                ["event_option_index"] = optionIndex,
                ["text_key"] = ReadDictionaryString(option, "text_key"),
                ["title"] = ReadDictionaryString(option, "title"),
                ["description"] = ReadDictionaryString(option, "description"),
                ["event_fingerprint"] = eventFingerprint,
                ["option_fingerprint"] = ReadDictionaryString(option, "fingerprint"),
                ["is_proceed"] = ReadDictionaryBool(option, "is_proceed"),
                ["adapter_confidence"] = ReadDictionaryString(option, "adapter_confidence"),
                ["outcome_known_level"] = ReadDictionaryString(option, "outcome_known_level"),
                ["runtime_warnings"] = option.TryGetValue("runtime_warnings", out object? runtimeWarnings) ? runtimeWarnings : null,
                ["known_outcome"] = option.TryGetValue("known_outcome", out object? knownOutcome) ? knownOutcome : null,
                ["execution"] = new Dictionary<string, object?>
                {
                    ["schema"] = "event_action_execution.v1",
                    ["event_fingerprint"] = eventFingerprint,
                    ["option_fingerprint"] = ReadDictionaryString(option, "fingerprint"),
                    ["checks"] = new Dictionary<string, object?>
                    {
                        ["event_option_index"] = optionIndex,
                        ["is_locked_at_export"] = ReadDictionaryBool(option, "is_locked") == true,
                        ["is_enabled_at_export"] = ReadDictionaryBool(option, "is_enabled"),
                        ["was_chosen_at_export"] = wasChosen,
                        ["runtime_state_rechecked_before_execution"] = true
                    }
                },
                ["summary"] = actionSummary,
                ["validation_note"] = validationNote
            });
        }

        return actions;
    }

    private static string BuildEventFingerprint(Dictionary<string, object?> eventState, List<Dictionary<string, object?>> options)
    {
        Dictionary<string, object?> comparable = new()
        {
            ["event_id"] = ReadDictionaryString(eventState, "event_id"),
            ["title"] = ReadDictionaryString(eventState, "title"),
            ["description"] = ReadDictionaryString(eventState, "description"),
            ["options"] = options
                .Select(option => new Dictionary<string, object?>
                {
                    ["event_option_index"] = ReadDictionaryInt(option, "event_option_index"),
                    ["text_key"] = ReadDictionaryString(option, "text_key"),
                    ["title"] = ReadDictionaryString(option, "title"),
                    ["description"] = ReadDictionaryString(option, "description"),
                    ["is_locked"] = ReadDictionaryBool(option, "is_locked"),
                    ["is_proceed"] = ReadDictionaryBool(option, "is_proceed")
                })
                .ToList()
        };

        return ComputeStableFingerprint(comparable);
    }

    private static string BuildEventOptionFingerprint(
        string optionId,
        int optionIndex,
        string? title,
        string? description,
        string? textKey)
    {
        Dictionary<string, object?> comparable = new()
        {
            ["event_option_id"] = optionId,
            ["event_option_index"] = optionIndex,
            ["text_key"] = textKey,
            ["title"] = title,
            ["description"] = description
        };

        return ComputeStableFingerprint(comparable);
    }

    private static string ComputeStableFingerprint(Dictionary<string, object?> value)
    {
        Dictionary<string, object?> normalized = NormalizeStateForJson(value, "stable_fingerprint");
        string json = JsonSerializer.Serialize(normalized, JsonOptions);
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static Dictionary<string, object?> BuildEventDebug(object eventRoom, object? eventModel, object? layout, ObjectGraph graph)
    {
        return new Dictionary<string, object?>
        {
            ["current_root_type"] = eventRoom.GetType().FullName ?? eventRoom.GetType().Name,
            ["event_model_type"] = eventModel?.GetType().FullName ?? eventModel?.GetType().Name,
            ["layout_type"] = layout?.GetType().FullName ?? layout?.GetType().Name,
            ["observed_types"] = LastObservedTypes.ToArray(),
            ["graph_node_count"] = graph.Nodes.Count
        };
    }

    private static Dictionary<string, object?> BuildRestSiteState(object restSiteRoom)
    {
        object? runManager = GetStaticPropertyValue("MegaCrit.Sts2.Core.Runs.RunManager", "Instance");
        object? runState = FindMemberValue(restSiteRoom, "_runState", "runState", "RunState")
            ?? FindMemberValue(runManager, "RunState", "runState", "_runState", "State", "state");
        object? player = recentPlayer
            ?? recentPlayerCombatState
            ?? EnumerateObjects(FindMemberValue(runState, "Players", "players", "_players")).FirstOrDefault();
        object? relicsSource = FindMemberValue(player, "relics")
            ?? FindMemberValue(runState, "relics");

        List<object> roots = new();
        AddRoot(roots, restSiteRoom);
        AddRoot(roots, runState);
        AddRoot(roots, player);
        ObjectGraph graph = ObjectGraph.Collect(roots, 3, 260);

        Dictionary<string, object?> restSite = BuildRestSiteScreenState(restSiteRoom);
        List<Dictionary<string, object?>> options = ReadDictionaryList(restSite, "options");

        return new Dictionary<string, object?>
        {
            ["schema_version"] = "combat_state.v1",
            ["phase"] = "rest_site",
            ["state_id"] = "rest_site_pending",
            ["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["run"] = BuildRun(runState ?? restSiteRoom, graph),
            ["player"] = player is null ? new Dictionary<string, object?>() : BuildPlayer(player),
            ["piles"] = BuildEmptyPiles(),
            ["enemies"] = new List<Dictionary<string, object?>>(),
            ["rest_site"] = restSite,
            ["legal_actions"] = BuildRestSiteLegalActions(options, ReadDictionaryBool(restSite, "proceed_enabled") == true),
            ["relics"] = BuildRelics(relicsSource, graph),
            ["debug"] = BuildRestSiteDebug(restSiteRoom, runState, graph)
        };
    }

    private static object? FindRestSiteRoom()
    {
        object? room = GetStaticPropertyValue("MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom", "Instance");
        if (IsRestSiteRoomVisible(room))
        {
            return room;
        }

        object? screenContext = GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext",
            "Instance");
        object? currentScreen = TryInvokeMethod(screenContext, "GetCurrentScreen");
        if (IsRestSiteRoomVisible(currentScreen))
        {
            return currentScreen;
        }

        object? run = GetStaticPropertyValue("MegaCrit.Sts2.Core.Nodes.Rooms.NRun", "Instance");
        object? runRestSiteRoom = FindMemberValue(run, "RestSiteRoom", "restSiteRoom", "_restSiteRoom");
        return IsRestSiteRoomVisible(runRestSiteRoom) ? runRestSiteRoom : null;
    }

    private static bool IsRestSiteRoomVisible(object? source)
    {
        if (source is null)
        {
            return false;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        return typeName.Contains("NRestSiteRoom", StringComparison.OrdinalIgnoreCase)
            && IsLiveVisibleControl(source);
    }

    private static Dictionary<string, object?> BuildRestSiteScreenState(object restSiteRoom)
    {
        List<Dictionary<string, object?>> options = BuildRestSiteOptions(restSiteRoom);
        object? proceedButton = FindMemberValue(restSiteRoom, "ProceedButton", "_proceedButton", "proceedButton");
        return new Dictionary<string, object?>
        {
            ["screen_type"] = restSiteRoom.GetType().FullName ?? restSiteRoom.GetType().Name,
            ["option_count"] = options.Count,
            ["options"] = options,
            ["proceed_enabled"] = ReadBool(proceedButton, "IsEnabled", "isEnabled", "_isEnabled"),
            ["proceed_visible"] = ReadBool(proceedButton, "Visible", "visible")
        };
    }

    private static List<Dictionary<string, object?>> BuildRestSiteOptions(object restSiteRoom)
    {
        List<object> optionButtons = EnumerateNodeDescendants(restSiteRoom)
            .Where(IsRestSiteButton)
            .ToList();

        List<Dictionary<string, object?>> options = new();
        for (int index = 0; index < optionButtons.Count; index++)
        {
            options.Add(BuildRestSiteOption(optionButtons[index], index));
        }

        return options;
    }

    private static Dictionary<string, object?> BuildRestSiteOption(object button, int fallbackIndex)
    {
        object? option = FindMemberValue(button, "Option", "option", "_option");
        string optionId = ReadString(option, "OptionId", "optionId", "_optionId") ?? $"rest_option_{fallbackIndex}";
        string? title = ReadFormattedText(FindMemberValue(option, "Title", "title", "_title"));
        string? description = ReadFormattedText(FindMemberValue(option, "Description", "description", "_description"));
        bool? isEnabled = ReadBool(option, "IsEnabled", "isEnabled", "_isEnabled");
        return new Dictionary<string, object?>
        {
            ["rest_option_id"] = optionId,
            ["rest_option_index"] = fallbackIndex,
            ["title"] = title,
            ["description"] = description,
            ["is_enabled"] = isEnabled,
            ["type_name"] = option?.GetType().FullName ?? option?.GetType().Name,
            ["button_type_name"] = button.GetType().FullName ?? button.GetType().Name,
            ["summary"] = BuildRestSiteOptionSummary(optionId, title, description)
        };
    }

    private static string BuildRestSiteOptionSummary(string optionId, string? title, string? description)
    {
        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(description))
        {
            return $"{title}: {description}";
        }

        return title ?? description ?? optionId;
    }

    private static bool IsRestSiteButton(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return typeName.Contains("NRestSiteButton", StringComparison.OrdinalIgnoreCase);
    }

    private static List<Dictionary<string, object?>> BuildRestSiteLegalActions(List<Dictionary<string, object?>> options, bool canProceed)
    {
        List<Dictionary<string, object?>> actions = new();
        foreach (Dictionary<string, object?> option in options)
        {
            if (ReadDictionaryBool(option, "is_enabled") == false)
            {
                continue;
            }

            int? optionIndex = ReadDictionaryInt(option, "rest_option_index");
            string optionId = ReadDictionaryString(option, "rest_option_id") ?? $"rest_option_{optionIndex?.ToString() ?? "unknown"}";
            actions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = SanitizeActionId($"choose_rest_site_{optionId}_{optionIndex?.ToString() ?? "unknown"}"),
                ["type"] = "choose_rest_site_option",
                ["rest_option_id"] = optionId,
                ["rest_option_index"] = optionIndex,
                ["title"] = ReadDictionaryString(option, "title"),
                ["description"] = ReadDictionaryString(option, "description"),
                ["summary"] = $"모닥불 선택지를 고릅니다: {ReadDictionaryString(option, "summary") ?? optionId}",
                ["validation_note"] = "현재 모닥불 화면의 실제 선택 버튼입니다."
            });
        }

        if (canProceed)
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = "proceed_rest_site",
                ["type"] = "proceed_rest_site",
                ["summary"] = "모닥불 화면을 닫고 지도로 진행합니다.",
                ["validation_note"] = "현재 모닥불 화면의 진행 버튼입니다."
            });
        }

        return actions;
    }

    private static Dictionary<string, object?> BuildRestSiteDebug(object restSiteRoom, object? runState, ObjectGraph graph)
    {
        return new Dictionary<string, object?>
        {
            ["current_root_type"] = restSiteRoom.GetType().FullName ?? restSiteRoom.GetType().Name,
            ["run_state_type"] = runState?.GetType().FullName ?? runState?.GetType().Name,
            ["observed_types"] = LastObservedTypes.ToArray(),
            ["graph_node_count"] = graph.Nodes.Count
        };
    }

    private static Dictionary<string, object?> BuildEmptyPiles()
    {
        return new Dictionary<string, object?>
        {
            ["hand"] = new List<Dictionary<string, object?>>(),
            ["draw_pile"] = new List<Dictionary<string, object?>>(),
            ["discard_pile"] = new List<Dictionary<string, object?>>(),
            ["exhaust_pile"] = new List<Dictionary<string, object?>>()
        };
    }

    private static Dictionary<string, object?> BuildTreasureState(object treasureRoom)
    {
        object? runManager = GetRunManagerInstance();
        object? runState = ReadRunManagerDebugState(runManager)
            ?? FindMemberValue(treasureRoom, "_runState", "runState", "RunState")
            ?? FindMemberValue(runManager, "RunState", "runState", "_runState", "State", "state");
        object? currentRoom = FindMemberValue(runState, "CurrentRoom", "currentRoom", "_currentRoom")
            ?? FindMemberValue(runManager, "CurrentRoom", "currentRoom", "_currentRoom");
        object? player = recentPlayer
            ?? recentPlayerCombatState
            ?? EnumerateObjects(FindMemberValue(runState, "Players", "players", "_players")).FirstOrDefault();
        object? relicsSource = FindMemberValue(player, "relics")
            ?? FindMemberValue(runState, "relics");
        object? synchronizer = FindMemberValue(runManager, "TreasureRoomRelicSynchronizer", "treasureRoomRelicSynchronizer", "_treasureRoomRelicSynchronizer");
        List<object> roots = new();
        AddRoot(roots, treasureRoom);
        AddRoot(roots, runState);
        AddRoot(roots, currentRoom);
        AddRoot(roots, player);
        AddRoot(roots, synchronizer);
        ObjectGraph graph = ObjectGraph.Collect(roots, 3, 260);

        Dictionary<string, object?> treasureState = BuildTreasureRoomState(treasureRoom, currentRoom, synchronizer);
        return new Dictionary<string, object?>
        {
            ["schema_version"] = "combat_state.v1",
            ["phase"] = "treasure",
            ["state_id"] = "treasure_pending",
            ["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["run"] = BuildRun(runState ?? treasureRoom, graph),
            ["player"] = player is null ? new Dictionary<string, object?>() : BuildPlayer(player),
            ["piles"] = BuildEmptyPiles(),
            ["enemies"] = new List<Dictionary<string, object?>>(),
            ["treasure"] = treasureState,
            ["legal_actions"] = BuildTreasureLegalActionsV2(treasureState),
            ["relics"] = BuildRelics(relicsSource, graph),
            ["debug"] = BuildTreasureDebug(treasureRoom, currentRoom, synchronizer, graph)
        };
    }

    private static object? FindTreasureRoom()
    {
        object? nRun = GetStaticPropertyValue("MegaCrit.Sts2.Core.Nodes.NRun", "Instance");
        object? nRunTreasureRoom = FindMemberValue(nRun, "TreasureRoom", "treasureRoom", "_treasureRoom");
        if (IsTreasureRoomVisible(nRunTreasureRoom))
        {
            return nRunTreasureRoom;
        }

        object? runManager = GetRunManagerInstance();
        object? runState = ReadRunManagerDebugState(runManager)
            ?? FindMemberValue(runManager, "RunState", "runState", "_runState", "State", "state");
        object? currentRoom = FindMemberValue(runState, "CurrentRoom", "currentRoom", "_currentRoom")
            ?? FindMemberValue(runManager, "CurrentRoom", "currentRoom", "_currentRoom");
        if (IsTreasureRoomLike(currentRoom))
        {
            return currentRoom;
        }

        object? screenContext = GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext",
            "Instance");
        object? currentScreen = TryInvokeMethod(screenContext, "GetCurrentScreen");
        return IsTreasureRoomVisible(currentScreen) ? currentScreen : null;
    }

    private static bool IsTreasureRoomVisible(object? source)
    {
        if (!IsTreasureRoomLike(source))
        {
            return false;
        }

        bool? isQueuedForDeletion = TryInvokeBoolMethod(source, "IsQueuedForDeletion");
        if (isQueuedForDeletion == true)
        {
            return false;
        }

        bool? isInsideTree = TryInvokeBoolMethod(source, "IsInsideTree");
        bool? visible = ReadBool(source, "Visible", "visible");
        return isInsideTree != false && visible != false;
    }

    private static bool IsTreasureRoomLike(object? source)
    {
        if (source is null)
        {
            return false;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        return typeName.Contains("TreasureRoom", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Chest", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> BuildTreasureRoomState(object treasureRoom, object? currentRoom, object? synchronizer)
    {
        List<Dictionary<string, object?>> relicOptions = BuildTreasureRelicOptions(synchronizer);
        object? proceedButton = FindMemberValue(treasureRoom, "ProceedButton", "_proceedButton", "proceedButton");
        return new Dictionary<string, object?>
        {
            ["screen_type"] = treasureRoom.GetType().FullName ?? treasureRoom.GetType().Name,
            ["room_type"] = currentRoom?.GetType().FullName ?? currentRoom?.GetType().Name,
            ["room_kind"] = ClassifyRoomKind(currentRoom?.GetType().FullName, currentRoom is null ? null : GetReadableName(currentRoom)),
            ["is_inside_tree"] = TryInvokeBoolMethod(treasureRoom, "IsInsideTree"),
            ["visible"] = ReadBool(treasureRoom, "Visible", "visible"),
            ["has_chest_been_opened"] = ReadBool(treasureRoom, "_hasChestBeenOpened", "HasChestBeenOpened", "hasChestBeenOpened"),
            ["is_relic_collection_open"] = ReadBool(treasureRoom, "_isRelicCollectionOpen", "IsRelicCollectionOpen", "isRelicCollectionOpen"),
            ["proceed_button_enabled"] = ReadBool(proceedButton, "IsEnabled", "isEnabled"),
            ["default_focused_control_found"] = FindMemberValue(treasureRoom, "DefaultFocusedControl", "defaultFocusedControl") is not null,
            ["relic_options"] = relicOptions,
            ["relic_option_count"] = relicOptions.Count
        };
    }

    private static List<Dictionary<string, object?>> BuildTreasureLegalActions(Dictionary<string, object?> treasure)
    {
        bool chestOpened = ReadDictionaryBool(treasure, "has_chest_been_opened") == true;
        bool relicCollectionOpen = ReadDictionaryBool(treasure, "is_relic_collection_open") == true;
        if (chestOpened || relicCollectionOpen)
        {
            return new List<Dictionary<string, object?>>();
        }

        return new List<Dictionary<string, object?>>
        {
            new()
            {
                ["action_id"] = "open_treasure_chest",
                ["type"] = "open_treasure_chest",
                ["summary"] = "보물상자를 열어 보상과 유물 선택을 표시합니다.",
                ["validation_note"] = "현재 보물방의 원래 Chest 버튼 해제 핸들러를 호출합니다."
            }
        };
    }

    private static List<Dictionary<string, object?>> BuildTreasureLegalActionsV2(Dictionary<string, object?> treasure)
    {
        bool chestOpened = ReadDictionaryBool(treasure, "has_chest_been_opened") == true;
        bool relicCollectionOpen = ReadDictionaryBool(treasure, "is_relic_collection_open") == true;
        if (!chestOpened && !relicCollectionOpen)
        {
            return new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["action_id"] = "open_treasure_chest",
                    ["type"] = "open_treasure_chest",
                    ["summary"] = "보물상자를 열어 보상과 유물 선택을 표시합니다.",
                    ["validation_note"] = "현재 보물방의 원래 Chest 버튼 해제 핸들러를 호출합니다."
                }
            };
        }

        List<Dictionary<string, object?>> actions = new();
        if (relicCollectionOpen)
        {
            List<Dictionary<string, object?>> relicOptions = ReadDictionaryList(treasure, "relic_options");
            string actionType = relicOptions.Count > 1 ? "choose_treasure_relic" : "claim_treasure_relic";
            foreach (Dictionary<string, object?> relic in relicOptions)
            {
                int? relicIndex = ReadDictionaryInt(relic, "treasure_relic_index") ?? ReadDictionaryInt(relic, "index");
                string relicId = ReadDictionaryString(relic, "relic_id")
                    ?? ReadDictionaryString(relic, "model_id")
                    ?? ReadDictionaryString(relic, "id")
                    ?? $"relic_{relicIndex?.ToString() ?? "unknown"}";
                string treasureRelicId = ReadDictionaryString(relic, "treasure_relic_id")
                    ?? BuildTreasureRelicStableId(relicIndex ?? -1, relicId);
                string relicName = ReadDictionaryString(relic, "name") ?? relicId;
                actions.Add(new Dictionary<string, object?>
                {
                    ["action_id"] = SanitizeActionId($"{actionType}_{treasureRelicId}"),
                    ["type"] = actionType,
                    ["treasure_relic_id"] = treasureRelicId,
                    ["treasure_relic_index"] = relicIndex,
                    ["relic_id"] = relicId,
                    ["model_id"] = relicId,
                    ["name"] = relicName,
                    ["summary"] = $"보물방 유물을 획득합니다: {relicName}",
                    ["validation_note"] = "현재 보물방 유물 선택 UI의 실제 유물 홀더를 선택합니다."
                });
            }
        }

        bool proceedEnabled = ReadDictionaryBool(treasure, "proceed_button_enabled") == true;
        if (chestOpened && !relicCollectionOpen && proceedEnabled)
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = "proceed_treasure",
                ["type"] = "proceed_treasure",
                ["summary"] = "보물방을 나가 다음 지도 선택으로 돌아갑니다.",
                ["validation_note"] = "보물방 Proceed 버튼이 활성화된 뒤 원래 진행 버튼 경로를 호출합니다."
            });
        }

        return actions;
    }

    private static List<Dictionary<string, object?>> BuildTreasureRelicOptions(object? synchronizer)
    {
        object? currentRelics = FindMemberValue(synchronizer, "CurrentRelics", "currentRelics", "_currentRelics");
        List<Dictionary<string, object?>> result = new();
        int index = 0;
        foreach (object relic in EnumerateObjects(currentRelics))
        {
            Dictionary<string, object?>? summary = BuildItemSummary(relic);
            string relicId = ReadDictionaryString(summary ?? new Dictionary<string, object?>(), "id")
                ?? ReadModelIdentifier(relic)
                ?? $"relic_{index}";
            string treasureRelicId = BuildTreasureRelicStableId(index, relicId);
            result.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["treasure_relic_id"] = treasureRelicId,
                ["treasure_relic_index"] = index,
                ["relic_id"] = relicId,
                ["model_id"] = relicId,
                ["id"] = relicId,
                ["name"] = ReadDictionaryString(summary ?? new Dictionary<string, object?>(), "name") ?? GetReadableName(relic),
                ["description"] = ReadDictionaryString(summary ?? new Dictionary<string, object?>(), "description"),
                ["type_name"] = relic.GetType().FullName ?? relic.GetType().Name,
                ["rarity"] = ReadDictionaryString(summary ?? new Dictionary<string, object?>(), "rarity")
                    ?? ReadString(relic, "Rarity", "rarity", "_rarity")
            });
            index++;
        }

        return result;
    }

    private static string BuildTreasureRelicStableId(int index, string relicId)
    {
        return SanitizeActionId($"treasure_relic_{index}_{relicId}");
    }

    private static Dictionary<string, object?> BuildTreasureDebug(object treasureRoom, object? currentRoom, object? synchronizer, ObjectGraph graph)
    {
        return new Dictionary<string, object?>
        {
            ["current_root_type"] = treasureRoom.GetType().FullName ?? treasureRoom.GetType().Name,
            ["current_room_type"] = currentRoom?.GetType().FullName ?? currentRoom?.GetType().Name,
            ["synchronizer_found"] = synchronizer is not null,
            ["synchronizer_type"] = synchronizer?.GetType().FullName ?? synchronizer?.GetType().Name,
            ["graph_node_count"] = graph.Nodes.Count
        };
    }

    private static Dictionary<string, object?> BuildShopState(object shopScreen)
    {
        object? managerCombatState = ReadCombatManagerDebugState(GetCombatManagerInstance());
        if (managerCombatState is not null)
        {
            RememberObservedRoot(managerCombatState);
        }

        object? runManager = GetRunManagerInstance();
        object? runState = ReadRunManagerDebugState(runManager)
            ?? FindMemberValue(shopScreen, "_runState", "runState", "RunState")
            ?? FindMemberValue(runManager, "RunState", "runState", "_runState", "State", "state");
        ShopRuntimeLocatorContext shopRuntimeContext = CreateShopRuntimeLocatorContext();
        object? currentRoom = ShopRuntimeLocator.FindCurrentRoom(shopRuntimeContext, runState, runManager);
        object? player = recentPlayer
            ?? recentPlayerCombatState
            ?? EnumerateObjects(FindMemberValue(runState, "Players", "players", "_players")).FirstOrDefault();
        object? relicsSource = FindMemberValue(player, "relics")
            ?? FindMemberValue(runState, "relics");
        object? runtimeInventory = ShopRuntimeLocator.FindRuntimeMerchantInventory(shopRuntimeContext, player, runState, currentRoom, null);
        object? inventoryPlayer = FindMemberValue(runtimeInventory, "Player", "player", "_player");
        player = inventoryPlayer ?? player;
        relicsSource = FindMemberValue(player, "relics")
            ?? relicsSource;

        List<object> roots = new();
        AddRoot(roots, shopScreen);
        AddRoot(roots, runState);
        AddRoot(roots, currentRoom);
        AddRoot(roots, runtimeInventory);
        AddRoot(roots, player);
        AddRoot(roots, managerCombatState);
        ObjectGraph graph = ObjectGraph.Collect(roots, 3, 320);

        Dictionary<string, object?> playerState = player is null ? new Dictionary<string, object?>() : BuildPlayer(player);
        Dictionary<string, object?> shop = BuildShopScreenState(shopScreen, player, runState, currentRoom, runtimeInventory, playerState, graph);
        List<Dictionary<string, object?>> items = ReadDictionaryList(shop, "items");

        return ShopSnapshotBuilder.Build(new ShopSnapshotBuildInput(
            Run: BuildRun(runState ?? managerCombatState ?? shopScreen, graph),
            Player: playerState,
            Piles: BuildEmptyPiles(),
            Enemies: new List<Dictionary<string, object?>>(),
            Shop: shop,
            Items: items,
            CanProceed: ReadDictionaryBool(shop, "proceed_enabled") == true,
            Relics: BuildRelics(relicsSource, graph),
            Debug: BuildShopDebug(shopScreen, graph)));
    }

    private static Dictionary<string, object?> BuildShopScreenState(
        object shopScreen,
        object? player,
        object? runState,
        object? currentRoom,
        object? runtimeInventory,
        Dictionary<string, object?> playerState,
        ObjectGraph graph)
    {
        object? proceedButton = FindMemberValue(shopScreen, "_proceedButton", "proceedButton", "ProceedButton", "_continueButton", "continueButton");
        ShopRuntimeLocatorContext shopRuntimeContext = CreateShopRuntimeLocatorContext();
        object? inventory = ShopRuntimeLocator.FindShopInventory(shopRuntimeContext, shopScreen, graph.Nodes.Select(node => node.Value));
        runtimeInventory ??= ShopRuntimeLocator.FindRuntimeMerchantInventory(shopRuntimeContext, player, runState, currentRoom, graph.Nodes.Select(node => node.Value));
        int? gold = ReadFirstInt(new[] { player, recentPlayer }, "gold", "_gold", "currentGold", "_currentGold");
        List<Dictionary<string, object?>> items = BuildShopItems(shopScreen, runtimeInventory, graph);
        ShopInventorySnapshotBuilder.NormalizeItems(items, gold, playerState);

        return new Dictionary<string, object?>
        {
            ["screen_type"] = shopScreen.GetType().FullName ?? shopScreen.GetType().Name,
            ["current_room_type"] = currentRoom?.GetType().FullName ?? currentRoom?.GetType().Name,
            ["is_current_room_merchant"] = ShopRuntimeLocator.IsMerchantRoom(currentRoom),
            ["runtime_inventory_found"] = runtimeInventory is not null,
            ["runtime_inventory_type"] = runtimeInventory?.GetType().FullName ?? runtimeInventory?.GetType().Name,
            ["gold"] = gold,
            ["inventory_open"] = ReadBool(inventory, "IsOpen", "isOpen", "_isOpen") ?? ReadBool(shopScreen, "IsOpen", "isOpen", "_isOpen"),
            ["card_removal_available"] = items.Any(ShopInventorySnapshotBuilder.IsAvailableRemovalService),
            ["potion_slots"] = playerState.TryGetValue("potion_slots", out object? potionSlots) ? potionSlots : null,
            ["filled_potion_slots"] = ReadDictionaryInt(playerState, "filled_potion_slots"),
            ["max_potion_slots"] = ReadDictionaryInt(playerState, "max_potion_slots"),
            ["has_open_potion_slots"] = ReadDictionaryBool(playerState, "has_open_potion_slots"),
            ["item_count"] = items.Count,
            ["items"] = items,
            ["proceed_button"] = BuildButtonState(proceedButton),
            ["proceed_enabled"] = IsButtonEnabledOrVisible(proceedButton),
            ["proceed_visible"] = IsLiveVisibleControlOrNull(proceedButton)
        };
    }

    private static ShopRuntimeLocatorContext CreateShopRuntimeLocatorContext()
    {
        return new ShopRuntimeLocatorContext(
            GetStaticPropertyValue: GetStaticPropertyValue,
            TryInvokeMethod: TryInvokeMethod,
            FindMemberValue: FindMemberValue,
            EnumerateNodeDescendants: EnumerateNodeDescendants,
            IsLiveVisibleControl: IsLiveVisibleControl);
    }

    private static List<Dictionary<string, object?>> BuildShopItems(object shopScreen, object? runtimeInventory, ObjectGraph graph)
    {
        return ShopItemCandidateCollector.Collect(new ShopItemCandidateCollectorContext(
            ShopScreen: shopScreen,
            RuntimeInventory: runtimeInventory,
            GraphValues: graph.Nodes.Select(node => node.Value),
            FindMemberValue: FindMemberValue,
            EnumerateObjects: EnumerateObjects,
            EnumerateNodeDescendants: EnumerateNodeDescendants,
            IsLiveVisibleControlOrNull: IsLiveVisibleControlOrNull,
            TryInvokeBoolMethod: TryInvokeBoolMethod,
            FindGridCardHolders: FindGridCardHolders,
            ExtractCardFromHolder: ExtractCardFromHolder,
            BuildCards: BuildCards,
            BuildItemSummary: BuildItemSummary,
            ReadCardName: ReadCardName,
            GetReadableName: value => value is null ? null : GetReadableName(value),
            ReadString: ReadString,
            ReadPrice: ReadPrice,
            ReadBool: ReadBool));
    }

    private static int? ReadPrice(params object?[] sources)
    {
        return ReadFirstInt(
            sources,
            "price",
            "_price",
            "Price",
            "cost",
            "_cost",
            "Cost",
            "goldCost",
            "_goldCost",
            "GoldCost",
            "shopPrice",
            "_shopPrice",
            "ShopPrice");
    }

    private static bool? IsButtonEnabledOrVisible(object? button)
    {
        if (button is null)
        {
            return false;
        }

        return ReadBool(button, "IsEnabled", "isEnabled", "_isEnabled")
            ?? IsLiveVisibleControlOrNull(button);
    }

    private static bool? IsLiveVisibleControlOrNull(object? source)
    {
        return source is null ? null : IsLiveVisibleControl(source);
    }

    private static Dictionary<string, object?> BuildShopDebug(object shopScreen, ObjectGraph graph)
    {
        return new Dictionary<string, object?>
        {
            ["current_root_type"] = shopScreen.GetType().FullName ?? shopScreen.GetType().Name,
            ["observed_types"] = LastObservedTypes.ToArray(),
            ["graph_node_count"] = graph.Nodes.Count,
            ["shop_related_node_types"] = graph.Nodes
                .Select(node => node.Value)
                .Where(value => value is not null)
                .Select(value => value!.GetType().FullName ?? value.GetType().Name)
                .Where(typeName => ContainsAny(typeName, "Shop", "Merchant", "Store", "Relic", "Potion"))
                .Distinct(StringComparer.Ordinal)
                .Take(40)
                .ToArray()
        };
    }

    private static Dictionary<string, object?> BuildRewardState(object rewardScreen)
    {
        object? managerCombatState = ReadCombatManagerDebugState(GetCombatManagerInstance());
        if (managerCombatState is not null)
        {
            RememberObservedRoot(managerCombatState);
        }

        List<object> roots = new();
        AddRoot(roots, rewardScreen);
        AddRoot(roots, managerCombatState);
        AddRoot(roots, recentCombatState);
        AddRoot(roots, recentPlayerCombatState);
        AddRoot(roots, recentPlayer);
        ObjectGraph graph = ObjectGraph.Collect(roots, 3, 220);

        object? player = recentPlayer
            ?? recentPlayerCombatState
            ?? FindFirst(graph, "Player", "PlayerCombatState");
        object? relicsSource = FindMemberValue(player, "relics")
            ?? FindMemberValue(managerCombatState, "relics")
            ?? FindMemberValue(recentCombatState, "relics");
        Dictionary<string, object?> playerState = player is null
            ? new Dictionary<string, object?>()
            : BuildPlayer(player);
        Dictionary<string, object?> reward = BuildRewardScreenState(rewardScreen);
        List<Dictionary<string, object?>> rewards = ReadDictionaryList(reward, "rewards");

        return new Dictionary<string, object?>
        {
            ["schema_version"] = "combat_state.v1",
            ["phase"] = "reward",
            ["state_id"] = "reward_pending",
            ["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["run"] = BuildRun(managerCombatState ?? recentCombatState ?? rewardScreen, graph),
            ["player"] = playerState,
            ["piles"] = new Dictionary<string, object?>
            {
                ["hand"] = new List<Dictionary<string, object?>>(),
                ["draw_pile"] = new List<Dictionary<string, object?>>(),
                ["discard_pile"] = new List<Dictionary<string, object?>>(),
                ["exhaust_pile"] = new List<Dictionary<string, object?>>()
            },
            ["enemies"] = new List<Dictionary<string, object?>>(),
            ["reward"] = reward,
            ["legal_actions"] = BuildRewardLegalActions(rewards, playerState),
            ["relics"] = BuildRelics(relicsSource, graph),
            ["debug"] = BuildRewardDebug(rewardScreen, graph)
        };
    }

    private static object? FindRewardScreen()
    {
        object? screenContext = GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext",
            "Instance");
        object? currentScreen = TryInvokeMethod(screenContext, "GetCurrentScreen");
        if (IsRewardScreen(currentScreen))
        {
            return currentScreen;
        }

        object? overlayStack = GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack",
            "Instance");
        object? topOverlay = TryInvokeMethod(overlayStack, "Peek");
        if (IsRewardScreen(topOverlay))
        {
            return topOverlay;
        }

        return null;
    }

    private static bool IsRewardScreenCoveredByOpenMap(object rewardScreen)
    {
        object? mapScreen = FindMapScreen();
        if (mapScreen is null)
        {
            return false;
        }

        bool? isComplete = ReadBool(rewardScreen, "IsComplete", "isComplete", "_isComplete");
        if (isComplete == true)
        {
            return true;
        }

        Dictionary<string, object?> reward = BuildRewardScreenState(rewardScreen);
        List<Dictionary<string, object?>> rewards = ReadDictionaryList(reward, "rewards");
        return rewards.Count == 0;
    }

    private static bool IsRewardScreen(object? source)
    {
        if (source is null)
        {
            return false;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        if (typeName.Contains("NRewardsScreen", StringComparison.OrdinalIgnoreCase))
        {
            return IsLiveVisibleControl(source);
        }

        string? screenType = ReadString(source, "ScreenType", "screenType", "_screenType");
        return !string.IsNullOrWhiteSpace(screenType)
            && screenType.Contains("Rewards", StringComparison.OrdinalIgnoreCase)
            && IsLiveVisibleControl(source);
    }

    private static bool IsLiveVisibleControl(object source)
    {
        bool? isQueuedForDeletion = TryInvokeBoolMethod(source, "IsQueuedForDeletion");
        if (isQueuedForDeletion == true)
        {
            return false;
        }

        bool? isVisibleInTree = TryInvokeBoolMethod(source, "IsVisibleInTree");
        if (isVisibleInTree is not null)
        {
            return isVisibleInTree.Value;
        }

        bool? visible = ReadBool(source, "Visible", "visible");
        return visible == true;
    }

    private static Dictionary<string, object?> BuildRewardScreenState(object rewardScreen)
    {
        object? rewardButtonsSource = FindMemberValue(rewardScreen, "_rewardButtons", "RewardButtons", "rewardButtons");
        object? skippedButtonsSource = FindMemberValue(rewardScreen, "_skippedRewardButtons", "SkippedRewardButtons", "skippedRewardButtons");
        List<object> rewardButtons = EnumerateObjects(rewardButtonsSource).ToList();
        List<object> skippedRewardButtons = EnumerateObjects(skippedButtonsSource).ToList();
        List<Dictionary<string, object?>> rewards = new();
        for (int index = 0; index < rewardButtons.Count; index++)
        {
            rewards.Add(BuildRewardEntryFromButton(rewardButtons[index], $"reward_{index}"));
        }

        bool? skipDisallowed = ReadBool(rewardScreen, "_skipDisallowed", "SkipDisallowed", "skipDisallowed");
        return new Dictionary<string, object?>
        {
            ["screen_type"] = rewardScreen.GetType().FullName ?? rewardScreen.GetType().Name,
            ["reward_count"] = rewards.Count,
            ["skipped_reward_count"] = skippedRewardButtons.Count,
            ["skip_disallowed"] = skipDisallowed,
            ["can_skip"] = skipDisallowed is null ? null : !skipDisallowed.Value,
            ["rewards"] = rewards
        };
    }

    private static Dictionary<string, object?> BuildRewardEntryFromButton(object button, string rewardId)
    {
        object? reward = FindMemberValue(button, "Reward", "reward", "_reward")
            ?? FindMemberValue(button, "LinkedRewardSet", "linkedRewardSet", "_linkedRewardSet");
        if (reward is null)
        {
            return new Dictionary<string, object?>
            {
                ["reward_id"] = rewardId,
                ["type"] = "unknown",
                ["name"] = GetReadableName(button),
                ["description"] = TryReadFormattedDescription(button),
                ["type_name"] = null,
                ["button_type_name"] = button.GetType().FullName ?? button.GetType().Name,
                ["read_status"] = "reward_missing"
            };
        }

        return BuildRewardEntry(reward, rewardId, button);
    }

    private static Dictionary<string, object?> BuildRewardEntry(object reward, string rewardId, object? button)
    {
        string rewardType = ClassifyRewardType(reward);
        Dictionary<string, object?> entry = new()
        {
            ["reward_id"] = rewardId,
            ["type"] = rewardType,
            ["name"] = ReadRewardName(reward, rewardType),
            ["description"] = TryReadFormattedDescription(reward) ?? ReadString(reward, "Description", "description"),
            ["is_populated"] = ReadBool(reward, "IsPopulated", "isPopulated", "_isPopulated"),
            ["reward_type"] = ReadString(reward, "RewardType", "rewardType", "_rewardType"),
            ["rewards_set_index"] = ReadInt(reward, "RewardsSetIndex", "rewardsSetIndex", "_rewardsSetIndex"),
            ["type_name"] = reward.GetType().FullName ?? reward.GetType().Name,
            ["button_type_name"] = button?.GetType().FullName ?? button?.GetType().Name
        };

        if (rewardType == "card_reward")
        {
            entry["can_skip"] = ReadBool(reward, "CanSkip", "canSkip", "_canSkip");
            entry["can_reroll"] = ReadBool(reward, "CanReroll", "canReroll", "_canReroll");
            entry["cards"] = BuildRewardCards(FindMemberValue(reward, "Cards", "cards", "_cards"));
        }
        else if (rewardType == "gold")
        {
            entry["amount"] = ReadInt(reward, "Amount", "amount", "_amount");
        }
        else if (rewardType == "potion")
        {
            entry["potion"] = BuildItemSummary(FindMemberValue(reward, "Potion", "potion", "_potion", "ClaimedPotion", "claimedPotion"));
        }
        else if (rewardType == "relic")
        {
            entry["rarity"] = ReadString(reward, "Rarity", "rarity", "_rarity");
            entry["relic"] = BuildItemSummary(FindMemberValue(reward, "_relic", "relic", "ClaimedRelic", "claimedRelic"));
        }
        else if (rewardType == "linked_reward_set")
        {
            List<Dictionary<string, object?>> nestedRewards = new();
            List<object> nestedRewardObjects = EnumerateObjects(FindMemberValue(reward, "Rewards", "rewards", "_rewards")).ToList();
            for (int nestedIndex = 0; nestedIndex < nestedRewardObjects.Count; nestedIndex++)
            {
                nestedRewards.Add(BuildRewardEntry(nestedRewardObjects[nestedIndex], $"{rewardId}_{nestedIndex}", button));
            }

            entry["rewards"] = nestedRewards;
        }

        return entry;
    }

    private static string ClassifyRewardType(object reward)
    {
        string typeName = reward.GetType().FullName ?? reward.GetType().Name;
        if (typeName.Contains("CardReward", StringComparison.OrdinalIgnoreCase))
        {
            return "card_reward";
        }

        if (typeName.Contains("GoldReward", StringComparison.OrdinalIgnoreCase))
        {
            return "gold";
        }

        if (typeName.Contains("PotionReward", StringComparison.OrdinalIgnoreCase))
        {
            return "potion";
        }

        if (typeName.Contains("RelicReward", StringComparison.OrdinalIgnoreCase))
        {
            return "relic";
        }

        if (typeName.Contains("LinkedRewardSet", StringComparison.OrdinalIgnoreCase))
        {
            return "linked_reward_set";
        }

        return "unknown";
    }

    private static string ReadRewardName(object reward, string rewardType)
    {
        string? explicitName = ReadString(reward, "Name", "name", "DisplayName", "displayName", "Title", "title");
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            return explicitName;
        }

        return rewardType switch
        {
            "card_reward" => "Card reward",
            "gold" => "Gold reward",
            "potion" => "Potion reward",
            "relic" => "Relic reward",
            "linked_reward_set" => "Linked reward set",
            _ => GetReadableName(reward)
        };
    }

    private static List<Dictionary<string, object?>> BuildRewardCards(object? source)
    {
        List<Dictionary<string, object?>> cards = new();
        int index = 0;
        foreach (object card in EnumerateCards(source))
        {
            object? rewardCardModel = FindMemberValue(card, "Card", "card", "_modifiedCard", "originalCard");
            object primaryCard = rewardCardModel ?? card;
            string fallbackName = GetReadableName(primaryCard);
            object? cardStats = FindMemberValue(primaryCard, "cardStats", "_cardStats", "stats", "_stats");
            object? cardInfo = FindMemberValue(primaryCard, "cardInfo", "_cardInfo", "info", "_info", "baseCard", "_baseCard");
            object? cardModel = FindMemberValue(primaryCard, "Model", "model", "_model", "cardModel", "_cardModel") ?? primaryCard;
            object? energyCost = FindMemberValue(cardModel, "EnergyCost", "energyCost", "_energyCost");
            string cardId = ReadFirstString(new[] { primaryCard, cardModel, cardInfo }, "id", "_id", "cardId", "_cardId", "key", "_key") ?? fallbackName;
            string cardName = ReadCardName(primaryCard, cardModel, cardInfo) ?? fallbackName;
            string stableId = BuildRewardStableId("card_reward", cardId, index);
            int? resolvedCost = TryInvokeInt(energyCost, "GetAmountToSpend")
                ?? ReadFirstInt(new[] { primaryCard, cardModel, cardStats, cardInfo }, "cost", "_cost", "currentCost", "_currentCost", "energyCost", "_energyCost", "EnergyCost", "CanonicalEnergyCost", "canonicalEnergyCost", "_canonicalEnergyCost");
            int? baseCost = ReadInt(energyCost, "Canonical")
                ?? ReadFirstInt(new[] { primaryCard, cardModel, cardStats, cardInfo }, "baseCost", "_baseCost", "baseEnergyCost", "_baseEnergyCost", "energyCost", "_energyCost", "EnergyCost", "CanonicalEnergyCost", "canonicalEnergyCost", "_canonicalEnergyCost");
            cards.Add(new Dictionary<string, object?>
            {
                ["card_reward_index"] = index,
                ["reward_type"] = "card_reward",
                ["model_id"] = cardId,
                ["reward_stable_id"] = stableId,
                ["card_reward_stable_id"] = stableId,
                ["card_id"] = cardId,
                ["name"] = cardName,
                ["type"] = ReadFirstString(new[] { primaryCard, cardModel, cardInfo, cardStats }, "type", "_type", "cardType", "_cardType"),
                ["cost"] = resolvedCost,
                ["base_cost"] = baseCost,
                ["rarity"] = ReadFirstString(new[] { primaryCard, cardModel, cardInfo }, "rarity", "_rarity", "cardRarity", "_cardRarity"),
                ["upgraded"] = ReadBool(primaryCard, "upgraded", "isUpgraded", "IsUpgraded"),
                ["description"] = BuildCardDescription("reward", primaryCard, cardModel, cardInfo, cardStats)
            });
            index++;
        }

        return cards;
    }

    private static Dictionary<string, object?>? BuildItemSummary(object? source)
    {
        if (source is null)
        {
            return null;
        }

        string fallbackName = GetReadableName(source);
        return new Dictionary<string, object?>
        {
            ["id"] = ReadModelIdentifier(source) ?? fallbackName,
            ["name"] = ReadDisplayText(source, "Name", "name", "_name", "DisplayName", "displayName", "_displayName", "Title", "title", "_title") ?? fallbackName,
            ["description"] = ReadItemDescription(source),
            ["rarity"] = ReadString(source, "rarity", "_rarity", "relicRarity", "potionRarity"),
            ["type_name"] = source.GetType().FullName ?? source.GetType().Name
        };
    }

    private static string? ReadModelIdentifier(object? source)
    {
        object? id = FindMemberValue(source, "Id", "id", "_id", "Key", "key", "_key");
        object? entry = FindMemberValue(id, "Entry", "entry", "_entry");
        string? entryText = entry is null ? null : ReadObjectString(entry);
        if (!string.IsNullOrWhiteSpace(entryText))
        {
            return entryText;
        }

        string? idText = id is null ? null : ReadObjectString(id);
        return string.IsNullOrWhiteSpace(idText) ? null : idText;
    }

    private static string? ReadDisplayText(object? source, params string[] names)
    {
        object? value = FindMemberValue(source, names);
        return ReadFormattedText(value) ?? (value is null ? null : ReadObjectString(value));
    }

    private static string? ReadItemDescription(object? source)
    {
        string? direct = TryReadFormattedDescription(source)
            ?? ReadDisplayText(source, "Tooltip", "tooltip", "_tooltip", "ToolTip", "toolTip")
            ?? ReadString(source, "description", "_description", "desc", "_desc", "tooltip", "toolTip");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        foreach (string methodName in new[] { "GetDescription", "GetFormattedDescription", "GetTooltip", "GetToolTip" })
        {
            object? value = TryInvokeMethod(source, methodName);
            string? text = ReadFormattedText(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static List<Dictionary<string, object?>> BuildRewardLegalActions(List<Dictionary<string, object?>> rewards, Dictionary<string, object?> player)
    {
        List<Dictionary<string, object?>> actions = new();
        if (rewards.Count == 0)
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = "proceed_rewards",
                ["type"] = "proceed_reward_screen",
                ["summary"] = "보상 화면을 닫고 다음 화면으로 진행한다."
            });
            return actions;
        }

        AddRewardLegalActions(actions, rewards, player);
        return actions;
    }

    private static void AddRewardLegalActions(List<Dictionary<string, object?>> actions, List<Dictionary<string, object?>> rewards, Dictionary<string, object?> player)
    {
        foreach (Dictionary<string, object?> reward in rewards)
        {
            string rewardId = ReadDictionaryString(reward, "reward_id") ?? "reward_unknown";
            string rewardType = ReadDictionaryString(reward, "type") ?? "unknown";
            if (rewardType == "card_reward")
            {
                foreach (Dictionary<string, object?> card in ReadDictionaryList(reward, "cards"))
                {
                    int? cardIndex = ReadDictionaryInt(card, "card_reward_index");
                    string modelId = ReadDictionaryString(card, "model_id")
                        ?? ReadDictionaryString(card, "card_id")
                        ?? "unknown_card";
                    string stableId = ReadDictionaryString(card, "reward_stable_id")
                        ?? BuildRewardStableId(rewardType, modelId, cardIndex ?? -1);
                    string cardName = ReadDictionaryString(card, "name") ?? modelId;
                    actions.Add(new Dictionary<string, object?>
                    {
                        ["action_id"] = SanitizeActionId($"choose_{rewardId}_{stableId}"),
                        ["type"] = "choose_card_reward",
                        ["reward_id"] = rewardId,
                        ["reward_type"] = rewardType,
                        ["reward_stable_id"] = stableId,
                        ["model_id"] = modelId,
                        ["card_reward_stable_id"] = stableId,
                        ["card_reward_index"] = cardIndex,
                        ["card_id"] = ReadDictionaryString(card, "card_id"),
                        ["card_name"] = cardName,
                        ["summary"] = $"{cardName} 카드를 보상으로 선택한다.",
                        ["validation_note"] = "현재 보상 화면의 실제 카드 선택 버튼을 실행할 수 있는 후보입니다."
                    });
                }

                if (ReadDictionaryBool(reward, "can_skip") == true)
                {
                    actions.Add(new Dictionary<string, object?>
                    {
                        ["action_id"] = SanitizeActionId($"skip_{rewardId}"),
                        ["type"] = "skip_card_reward",
                        ["reward_id"] = rewardId,
                        ["summary"] = "카드 보상을 건너뛴다.",
                        ["validation_note"] = "현재 보상 화면의 실제 건너뛰기 동작을 실행할 수 있는 후보입니다."
                    });
                }
            }
            else if (rewardType == "gold")
            {
                actions.Add(new Dictionary<string, object?>
                {
                    ["action_id"] = SanitizeActionId($"claim_{rewardId}"),
                    ["type"] = "claim_gold_reward",
                    ["reward_id"] = rewardId,
                    ["summary"] = "골드 보상을 획득한다."
                });
            }
            else if (rewardType == "relic")
            {
                actions.Add(new Dictionary<string, object?>
                {
                    ["action_id"] = SanitizeActionId($"claim_{rewardId}"),
                    ["type"] = "claim_relic_reward",
                    ["reward_id"] = rewardId,
                    ["summary"] = "유물 보상을 획득한다.",
                    ["validation_note"] = "현재 보상 화면의 실제 유물 보상 버튼을 실행할 수 있는 후보입니다."
                });
            }
            else if (rewardType == "potion")
            {
                AddPotionRewardLegalActions(actions, reward, rewardId, rewardType, player);
            }
            else if (rewardType == "potion")
            {
                actions.Add(new Dictionary<string, object?>
                {
                    ["action_id"] = SanitizeActionId($"claim_{rewardId}"),
                    ["type"] = "claim_potion_reward",
                    ["reward_id"] = rewardId,
                    ["summary"] = "포션 보상을 획득한다.",
                    ["validation_note"] = "현재 보상 화면의 실제 포션 보상 버튼을 실행할 수 있는 후보입니다."
                });
            }
            else if (rewardType == "linked_reward_set")
            {
                AddRewardLegalActions(actions, ReadDictionaryList(reward, "rewards"), player);
            }
        }
    }

    private static void AddPotionRewardLegalActions(
        List<Dictionary<string, object?>> actions,
        Dictionary<string, object?> reward,
        string rewardId,
        string rewardType,
        Dictionary<string, object?> player)
    {
        Dictionary<string, object?>? potion = ReadDictionaryObject(reward, "potion");
        string modelId = ReadDictionaryString(potion ?? new Dictionary<string, object?>(), "potion_id")
            ?? ReadDictionaryString(potion ?? new Dictionary<string, object?>(), "id")
            ?? ReadDictionaryString(reward, "name")
            ?? "unknown_potion";
        string potionName = ReadDictionaryString(potion ?? new Dictionary<string, object?>(), "name") ?? modelId;
        string stableId = BuildRewardStableId(rewardType, modelId, TryReadRewardIndex(rewardId) ?? -1);
        if (ReadDictionaryBool(player, "has_open_potion_slots") != false)
        {
            actions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = SanitizeActionId($"claim_{rewardId}_{stableId}"),
                ["type"] = "claim_potion_reward",
                ["reward_id"] = rewardId,
                ["reward_type"] = rewardType,
                ["reward_stable_id"] = stableId,
                ["model_id"] = modelId,
                ["potion_id"] = modelId,
                ["summary"] = $"{potionName} 포션 보상을 받습니다.",
                ["validation_note"] = "현재 보상 화면과 포션 슬롯 여유를 다시 확인한 뒤 실행합니다."
            });
            return;
        }

        foreach (Dictionary<string, object?> potionSlot in ReadDictionaryList(player, "potion_slots"))
        {
            if (ReadDictionaryBool(potionSlot, "empty") == true)
            {
                continue;
            }

            int? discardSlotIndex = ReadDictionaryInt(potionSlot, "slot_index");
            Dictionary<string, object?>? discardPotion = ReadDictionaryObject(potionSlot, "potion");
            string discardPotionId = ReadDictionaryString(discardPotion ?? new Dictionary<string, object?>(), "potion_id")
                ?? ReadDictionaryString(discardPotion ?? new Dictionary<string, object?>(), "id")
                ?? "unknown_potion";
            string discardPotionName = ReadDictionaryString(discardPotion ?? new Dictionary<string, object?>(), "name") ?? discardPotionId;
            actions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = SanitizeActionId($"claim_{rewardId}_{stableId}_discard_slot_{discardSlotIndex?.ToString() ?? "unknown"}"),
                ["type"] = "claim_potion_reward_with_discard",
                ["reward_id"] = rewardId,
                ["reward_type"] = rewardType,
                ["reward_stable_id"] = stableId,
                ["model_id"] = modelId,
                ["potion_id"] = modelId,
                ["potion_name"] = potionName,
                ["discard_potion_slot_index"] = discardSlotIndex,
                ["discard_potion_id"] = discardPotionId,
                ["discard_potion_name"] = discardPotionName,
                ["summary"] = $"{discardSlotIndex?.ToString() ?? "?"}번 슬롯의 {discardPotionName} 포션을 버리고 {potionName} 포션 보상을 받습니다.",
                ["validation_note"] = "실행 직전에 받을 포션과 버릴 포션 슬롯이 그대로인지 다시 확인합니다."
            });
        }
    }

    private static string BuildRewardStableId(string rewardType, string modelId, int index)
    {
        return SanitizeActionId($"reward_{rewardType}_{modelId}_{index}");
    }

    private static int? TryReadRewardIndex(string rewardId)
    {
        const string Marker = "reward_";
        int markerIndex = rewardId.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        int start = markerIndex + Marker.Length;
        int end = start;
        while (end < rewardId.Length && char.IsDigit(rewardId[end]))
        {
            end++;
        }

        return end > start && int.TryParse(rewardId[start..end], out int index)
            ? index
            : null;
    }

    private static Dictionary<string, object?> BuildRewardDebug(object rewardScreen, ObjectGraph graph)
    {
        return new Dictionary<string, object?>
        {
            ["current_root_type"] = rewardScreen.GetType().FullName ?? rewardScreen.GetType().Name,
            ["observed_types"] = LastObservedTypes.ToArray(),
            ["graph_node_count"] = graph.Nodes.Count
        };
    }

    private static Dictionary<string, object?> BuildMapState(object mapScreen)
    {
        object? managerCombatState = ReadCombatManagerDebugState(GetCombatManagerInstance());
        if (managerCombatState is not null)
        {
            RememberObservedRoot(managerCombatState);
        }

        object? runManager = GetRunManagerInstance();
        object? runState = ReadRunManagerDebugState(runManager)
            ?? FindMemberValue(mapScreen, "_runState", "runState", "RunState")
            ?? FindMemberValue(runManager, "RunState", "runState", "_runState", "State", "state");
        object? player = recentPlayer
            ?? recentPlayerCombatState
            ?? EnumerateObjects(FindMemberValue(runState, "Players", "players", "_players")).FirstOrDefault();
        object? map = FindMemberValue(mapScreen, "_map", "Map", "map")
            ?? FindMemberValue(runState, "Map", "map", "_map");
        object? relicsSource = FindMemberValue(player, "relics")
            ?? FindMemberValue(runState, "relics");
        List<object> roots = new();
        AddRoot(roots, mapScreen);
        AddRoot(roots, runState);
        AddRoot(roots, map);
        AddRoot(roots, player);
        AddRoot(roots, recentPlayer);
        ObjectGraph graph = ObjectGraph.Collect(roots, 3, 260);

        Dictionary<string, object?> mapState = BuildMapScreenState(mapScreen, runState, map);
        List<Dictionary<string, object?>> availableNextNodes = ReadDictionaryList(mapState, "available_next_nodes");

        return new Dictionary<string, object?>
        {
            ["schema_version"] = "combat_state.v1",
            ["phase"] = "map",
            ["state_id"] = "map_pending",
            ["exported_at_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["run"] = BuildRun(runState ?? managerCombatState ?? mapScreen, graph),
            ["player"] = player is null ? new Dictionary<string, object?>() : BuildPlayer(player),
            ["piles"] = new Dictionary<string, object?>
            {
                ["hand"] = new List<Dictionary<string, object?>>(),
                ["draw_pile"] = new List<Dictionary<string, object?>>(),
                ["discard_pile"] = new List<Dictionary<string, object?>>(),
                ["exhaust_pile"] = new List<Dictionary<string, object?>>()
            },
            ["enemies"] = new List<Dictionary<string, object?>>(),
            ["map"] = mapState,
            ["legal_actions"] = BuildMapLegalActions(availableNextNodes),
            ["relics"] = BuildRelics(relicsSource, graph),
            ["debug"] = BuildMapDebug(mapScreen, runState, map, graph)
        };
    }

    private static object? FindMapScreen()
    {
        object? screen = GetStaticPropertyValue("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen", "Instance");
        if (IsMapScreenOpen(screen))
        {
            return screen;
        }

        object? screenContext = GetStaticPropertyValue(
            "MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext",
            "Instance");
        object? currentScreen = TryInvokeMethod(screenContext, "GetCurrentScreen");
        return IsMapScreenOpen(currentScreen) ? currentScreen : null;
    }

    private static bool IsMapScreenOpen(object? source)
    {
        if (source is null)
        {
            return false;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        if (!typeName.Contains("NMapScreen", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool? isOpen = ReadBool(source, "IsOpen", "isOpen", "_isOpen");
        bool? visible = ReadBool(source, "Visible", "visible");
        bool? isQueuedForDeletion = TryInvokeBoolMethod(source, "IsQueuedForDeletion");
        return isQueuedForDeletion != true && (isOpen == true || visible == true);
    }

    private static Dictionary<string, object?> BuildMapScreenState(object mapScreen, object? runState, object? map)
    {
        Dictionary<string, object?> currentNode = BuildCurrentMapNode(runState);
        List<Dictionary<string, object?>> availableNextNodes = BuildAvailableMapNodes(mapScreen);
        Dictionary<string, object?> fullGraph = BuildFullMapGraph(mapScreen, map, currentNode, availableNextNodes);
        return new Dictionary<string, object?>
        {
            ["screen_type"] = mapScreen.GetType().FullName ?? mapScreen.GetType().Name,
            ["is_open"] = ReadBool(mapScreen, "IsOpen", "isOpen", "_isOpen"),
            ["is_travel_enabled"] = ReadBool(mapScreen, "IsTravelEnabled", "isTravelEnabled", "_isTravelEnabled"),
            ["is_traveling"] = ReadBool(mapScreen, "IsTraveling", "isTraveling", "_isTraveling"),
            ["act_index"] = ReadInt(runState, "CurrentActIndex", "currentActIndex", "_currentActIndex"),
            ["act_floor"] = ReadInt(runState, "ActFloor", "actFloor", "_actFloor"),
            ["total_floor"] = ReadInt(runState, "TotalFloor", "totalFloor"),
            ["row_count"] = TryInvokeMethod(map, "GetRowCount"),
            ["column_count"] = TryInvokeMethod(map, "GetColumnCount"),
            ["current"] = currentNode,
            ["available_next_nodes"] = availableNextNodes,
            ["available_next_node_count"] = availableNextNodes.Count,
            ["full_graph"] = fullGraph,
            ["path_options_summary"] = BuildMapPathOptionsSummary(availableNextNodes, fullGraph)
        };
    }

    private static Dictionary<string, object?> BuildCurrentMapNode(object? runState)
    {
        object? currentPoint = FindMemberValue(runState, "CurrentMapPoint", "currentMapPoint");
        if (currentPoint is not null)
        {
            return BuildMapPointSummary(currentPoint, null);
        }

        object? currentCoord = FindMemberValue(runState, "CurrentMapCoord", "currentMapCoord");
        Dictionary<string, object?> coord = BuildMapCoordSummary(currentCoord);
        return new Dictionary<string, object?>
        {
            ["node_id"] = BuildMapNodeId(coord),
            ["column"] = ReadDictionaryInt(coord, "column"),
            ["row"] = ReadDictionaryInt(coord, "row"),
            ["room_type"] = null,
            ["state"] = null,
            ["is_current"] = currentCoord is not null
        };
    }

    private static List<Dictionary<string, object?>> BuildAvailableMapNodes(object mapScreen)
    {
        List<Dictionary<string, object?>> result = new();
        HashSet<string> seenNodeIds = new(StringComparer.Ordinal);
        object? mapPointDictionary = FindMemberValue(mapScreen, "_mapPointDictionary", "mapPointDictionary", "MapPointDictionary");
        object? mapPointNodesSource = FindMemberValue(mapPointDictionary, "Values", "values")
            ?? FindMemberValue(mapScreen, "_points", "Points", "points");

        foreach (object mapPointNode in EnumerateObjects(mapPointNodesSource))
        {
            if (!IsTravelableMapPointNode(mapPointNode))
            {
                continue;
            }

            Dictionary<string, object?> summary = BuildMapPointSummary(FindMemberValue(mapPointNode, "Point", "point", "_point"), mapPointNode);
            string? nodeId = ReadDictionaryString(summary, "node_id");
            if (string.IsNullOrWhiteSpace(nodeId) || !seenNodeIds.Add(nodeId))
            {
                continue;
            }

            summary["reachable_now"] = true;
            result.Add(summary);
        }

        return result
            .OrderBy(node => ReadDictionaryInt(node, "row") ?? int.MaxValue)
            .ThenBy(node => ReadDictionaryInt(node, "column") ?? int.MaxValue)
            .ToList();
    }

    private static bool IsTravelableMapPointNode(object source)
    {
        object? point = FindMemberValue(source, "Point", "point", "_point");
        if (point is null)
        {
            return false;
        }

        string? state = ReadString(source, "State", "state", "_state");
        if (string.Equals(state, "Travelable", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool? isEnabled = ReadBool(source, "IsEnabled", "isEnabled", "_isEnabled");
        return isEnabled == true;
    }

    private static Dictionary<string, object?> BuildFullMapGraph(
        object mapScreen,
        object? map,
        Dictionary<string, object?> currentNode,
        List<Dictionary<string, object?>> availableNextNodes)
    {
        List<Dictionary<string, object?>> nodes = BuildAllMapNodes(mapScreen, map);
        HashSet<string> selectableNow = availableNextNodes
            .Select(node => ReadDictionaryString(node, "node_id"))
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        string? currentNodeId = ReadDictionaryString(currentNode, "node_id");

        foreach (Dictionary<string, object?> node in nodes)
        {
            string? nodeId = ReadDictionaryString(node, "node_id");
            node["selectable_now"] = !string.IsNullOrWhiteSpace(nodeId) && selectableNow.Contains(nodeId);
            node["is_current"] = !string.IsNullOrWhiteSpace(nodeId)
                && !string.IsNullOrWhiteSpace(currentNodeId)
                && nodeId.Equals(currentNodeId, StringComparison.Ordinal);
            node["reachable_from_current"] = !string.IsNullOrWhiteSpace(nodeId)
                && IsNodeReachableFromAnyNextNode(nodeId, availableNextNodes, nodes);
        }

        List<Dictionary<string, object?>> edges = BuildMapEdges(nodes);
        return new Dictionary<string, object?>
        {
            ["nodes"] = nodes,
            ["edges"] = edges,
            ["node_count"] = nodes.Count,
            ["edge_count"] = edges.Count,
            ["row_count"] = nodes.Select(node => ReadDictionaryInt(node, "row")).Where(row => row is not null).Distinct().Count(),
            ["column_count"] = nodes.Select(node => ReadDictionaryInt(node, "column")).Where(column => column is not null).Distinct().Count()
        };
    }

    private static List<Dictionary<string, object?>> BuildAllMapNodes(object mapScreen, object? map)
    {
        List<Dictionary<string, object?>> result = new();
        HashSet<string> seenNodeIds = new(StringComparer.Ordinal);
        foreach (object source in EnumerateMapPointSources(mapScreen, map))
        {
            object? point = FindMemberValue(source, "Point", "point", "_point") ?? source;
            Dictionary<string, object?> summary = BuildMapPointSummary(point, source);
            string? nodeId = ReadDictionaryString(summary, "node_id");
            if (string.IsNullOrWhiteSpace(nodeId) || nodeId == "map_unknown" || !seenNodeIds.Add(nodeId))
            {
                continue;
            }

            result.Add(summary);
        }

        return result
            .OrderBy(node => ReadDictionaryInt(node, "row") ?? int.MaxValue)
            .ThenBy(node => ReadDictionaryInt(node, "column") ?? int.MaxValue)
            .ToList();
    }

    private static IEnumerable<object> EnumerateMapPointSources(object mapScreen, object? map)
    {
        object? mapPointDictionary = FindMemberValue(mapScreen, "_mapPointDictionary", "mapPointDictionary", "MapPointDictionary");
        object? mapPointNodesSource = FindMemberValue(mapPointDictionary, "Values", "values")
            ?? FindMemberValue(mapScreen, "_points", "Points", "points");
        foreach (object mapPointNode in EnumerateObjects(mapPointNodesSource))
        {
            yield return mapPointNode;
        }

        object? allMapPoints = TryInvokeMethod(map, "GetAllMapPoints")
            ?? FindMemberValue(map, "MapPoints", "mapPoints", "_mapPoints", "Points", "points", "_points");
        foreach (object mapPoint in EnumerateObjects(allMapPoints))
        {
            yield return mapPoint;
        }
    }

    private static List<Dictionary<string, object?>> BuildMapEdges(List<Dictionary<string, object?>> nodes)
    {
        HashSet<string> nodeIds = nodes
            .Select(node => ReadDictionaryString(node, "node_id"))
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<Dictionary<string, object?>> edges = new();
        foreach (Dictionary<string, object?> node in nodes)
        {
            string? fromNodeId = ReadDictionaryString(node, "node_id");
            if (string.IsNullOrWhiteSpace(fromNodeId))
            {
                continue;
            }

            foreach (string childNodeId in ReadDictionaryStringList(node, "child_node_ids"))
            {
                if (!nodeIds.Contains(childNodeId))
                {
                    continue;
                }

                string edgeId = $"{fromNodeId}->{childNodeId}";
                if (!seen.Add(edgeId))
                {
                    continue;
                }

                edges.Add(new Dictionary<string, object?>
                {
                    ["from_node_id"] = fromNodeId,
                    ["to_node_id"] = childNodeId
                });
            }
        }

        return edges;
    }

    private static bool IsNodeReachableFromAnyNextNode(string nodeId, List<Dictionary<string, object?>> availableNextNodes, List<Dictionary<string, object?>> nodes)
    {
        HashSet<string> startNodeIds = availableNextNodes
            .Select(node => ReadDictionaryString(node, "node_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        if (startNodeIds.Contains(nodeId))
        {
            return true;
        }

        Dictionary<string, List<string>> adjacency = BuildAdjacency(nodes);
        Queue<string> queue = new(startNodeIds);
        HashSet<string> visited = new(startNodeIds, StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            foreach (string child in adjacency.GetValueOrDefault(current, new List<string>()))
            {
                if (!visited.Add(child))
                {
                    continue;
                }

                if (child.Equals(nodeId, StringComparison.Ordinal))
                {
                    return true;
                }

                queue.Enqueue(child);
            }
        }

        return false;
    }

    private static Dictionary<string, object?> BuildMapPathOptionsSummary(
        List<Dictionary<string, object?>> availableNextNodes,
        Dictionary<string, object?> fullGraph)
    {
        List<Dictionary<string, object?>> nodes = ReadDictionaryList(fullGraph, "nodes");
        Dictionary<string, List<string>> adjacency = BuildAdjacency(nodes);
        Dictionary<string, Dictionary<string, object?>> nodeById = nodes
            .Select(node => (Id: ReadDictionaryString(node, "node_id"), Node: node))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Id))
            .ToDictionary(pair => pair.Id!, pair => pair.Node, StringComparer.Ordinal);

        List<Dictionary<string, object?>> options = new();
        foreach (Dictionary<string, object?> nextNode in availableNextNodes)
        {
            string? startId = ReadDictionaryString(nextNode, "node_id");
            if (string.IsNullOrWhiteSpace(startId))
            {
                continue;
            }

            List<List<string>> paths = EnumerateMapPaths(startId, adjacency, 24, 64);
            options.Add(new Dictionary<string, object?>
            {
                ["start_node_id"] = startId,
                ["start_room_type"] = ReadDictionaryString(nextNode, "room_type"),
                ["path_count_sampled"] = paths.Count,
                ["reachable_room_counts"] = CountReachableRooms(startId, adjacency, nodeById),
                ["sample_paths"] = paths.Take(8).Select(path => BuildPathSummary(path, nodeById)).ToList()
            });
        }

        return new Dictionary<string, object?>
        {
            ["options"] = options,
            ["note"] = "각 start_node_id는 지금 바로 선택 가능한 다음 노드이며, sample_paths는 해당 노드 이후 스테이지 끝까지 이어지는 대표 경로입니다."
        };
    }

    private static Dictionary<string, List<string>> BuildAdjacency(List<Dictionary<string, object?>> nodes)
    {
        Dictionary<string, List<string>> adjacency = new(StringComparer.Ordinal);
        foreach (Dictionary<string, object?> node in nodes)
        {
            string? nodeId = ReadDictionaryString(node, "node_id");
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                continue;
            }

            adjacency[nodeId] = ReadDictionaryStringList(node, "child_node_ids")
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        return adjacency;
    }

    private static List<List<string>> EnumerateMapPaths(string startNodeId, Dictionary<string, List<string>> adjacency, int maxDepth, int maxPaths)
    {
        List<List<string>> paths = new();
        void Walk(string nodeId, List<string> path)
        {
            if (paths.Count >= maxPaths || path.Count >= maxDepth)
            {
                paths.Add(path.ToList());
                return;
            }

            List<string> children = adjacency.GetValueOrDefault(nodeId, new List<string>());
            if (children.Count == 0)
            {
                paths.Add(path.ToList());
                return;
            }

            foreach (string child in children)
            {
                if (path.Contains(child, StringComparer.Ordinal))
                {
                    continue;
                }

                path.Add(child);
                Walk(child, path);
                path.RemoveAt(path.Count - 1);
                if (paths.Count >= maxPaths)
                {
                    return;
                }
            }
        }

        Walk(startNodeId, new List<string> { startNodeId });
        return paths;
    }

    private static Dictionary<string, object?> BuildPathSummary(List<string> path, Dictionary<string, Dictionary<string, object?>> nodeById)
    {
        List<string> roomTypes = path
            .Select(nodeId => nodeById.TryGetValue(nodeId, out Dictionary<string, object?>? node)
                ? ReadDictionaryString(node, "room_type") ?? "unknown"
                : "unknown")
            .ToList();
        return new Dictionary<string, object?>
        {
            ["node_ids"] = path,
            ["room_types"] = roomTypes,
            ["room_counts"] = CountRoomTypes(roomTypes),
            ["length"] = path.Count
        };
    }

    private static Dictionary<string, object?> CountReachableRooms(string startId, Dictionary<string, List<string>> adjacency, Dictionary<string, Dictionary<string, object?>> nodeById)
    {
        Queue<string> queue = new(new[] { startId });
        HashSet<string> visited = new(StringComparer.Ordinal) { startId };
        List<string> roomTypes = new();
        while (queue.Count > 0)
        {
            string nodeId = queue.Dequeue();
            if (nodeById.TryGetValue(nodeId, out Dictionary<string, object?>? node))
            {
                roomTypes.Add(ReadDictionaryString(node, "room_type") ?? "unknown");
            }

            foreach (string child in adjacency.GetValueOrDefault(nodeId, new List<string>()))
            {
                if (visited.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        return CountRoomTypes(roomTypes);
    }

    private static Dictionary<string, object?> CountRoomTypes(IEnumerable<string> roomTypes)
    {
        return roomTypes
            .GroupBy(roomType => string.IsNullOrWhiteSpace(roomType) ? "unknown" : roomType)
            .ToDictionary(group => group.Key, group => (object?)group.Count(), StringComparer.Ordinal);
    }

    private static Dictionary<string, object?> BuildMapPointSummary(object? point, object? node)
    {
        Dictionary<string, object?> coord = BuildMapCoordSummary(FindMemberValue(point, "coord", "Coord", "_coord"));
        string nodeId = BuildMapNodeId(coord);
        List<string> childNodeIds = EnumerateObjects(FindMemberValue(point, "Children", "children", "_children"))
            .Select(child => BuildMapNodeId(BuildMapCoordSummary(FindMemberValue(child, "coord", "Coord", "_coord"))))
            .Where(nodeIdValue => !string.IsNullOrWhiteSpace(nodeIdValue))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new Dictionary<string, object?>
        {
            ["node_id"] = nodeId,
            ["column"] = ReadDictionaryInt(coord, "column"),
            ["row"] = ReadDictionaryInt(coord, "row"),
            ["room_type"] = ReadString(point, "PointType", "pointType", "_pointType"),
            ["state"] = ReadString(node, "State", "state", "_state"),
            ["is_enabled"] = ReadBool(node, "IsEnabled", "isEnabled", "_isEnabled"),
            ["child_node_ids"] = childNodeIds,
            ["type_name"] = point?.GetType().FullName ?? point?.GetType().Name,
            ["node_type_name"] = node?.GetType().FullName ?? node?.GetType().Name
        };
    }

    private static Dictionary<string, object?> BuildMapCoordSummary(object? coord)
    {
        int? column = ReadInt(coord, "col", "Col", "column", "Column");
        int? row = ReadInt(coord, "row", "Row");
        return new Dictionary<string, object?>
        {
            ["column"] = column,
            ["row"] = row
        };
    }

    private static string BuildMapNodeId(Dictionary<string, object?> coord)
    {
        int? column = ReadDictionaryInt(coord, "column");
        int? row = ReadDictionaryInt(coord, "row");
        return column is null || row is null ? "map_unknown" : $"map_r{row.Value}_c{column.Value}";
    }

    private static List<Dictionary<string, object?>> BuildMapLegalActions(List<Dictionary<string, object?>> availableNextNodes)
    {
        List<Dictionary<string, object?>> actions = new();
        foreach (Dictionary<string, object?> node in availableNextNodes)
        {
            string nodeId = ReadDictionaryString(node, "node_id") ?? "map_unknown";
            string roomType = ReadDictionaryString(node, "room_type") ?? "unknown";
            int? row = ReadDictionaryInt(node, "row");
            int? column = ReadDictionaryInt(node, "column");
            actions.Add(new Dictionary<string, object?>
            {
                ["action_id"] = SanitizeActionId($"choose_{nodeId}"),
                ["type"] = "choose_map_node",
                ["node_id"] = nodeId,
                ["row"] = row,
                ["column"] = column,
                ["room_type"] = roomType,
                ["summary"] = $"지도에서 {roomType} 노드({row?.ToString() ?? "?"}, {column?.ToString() ?? "?"})를 선택한다.",
                ["validation_note"] = "현재 지도 화면에서 바로 선택 가능한 노드입니다."
            });
        }

        return actions;
    }

    private static Dictionary<string, object?> BuildMapDebug(object mapScreen, object? runState, object? map, ObjectGraph graph)
    {
        object? mapPointDictionary = FindMemberValue(mapScreen, "_mapPointDictionary", "mapPointDictionary", "MapPointDictionary");
        return new Dictionary<string, object?>
        {
            ["current_root_type"] = mapScreen.GetType().FullName ?? mapScreen.GetType().Name,
            ["run_state_type"] = runState?.GetType().FullName ?? runState?.GetType().Name,
            ["map_type"] = map?.GetType().FullName ?? map?.GetType().Name,
            ["map_point_dictionary_type"] = mapPointDictionary?.GetType().FullName ?? mapPointDictionary?.GetType().Name,
            ["observed_types"] = LastObservedTypes.ToArray(),
            ["graph_node_count"] = graph.Nodes.Count
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

        try
        {
            string? text = ReadObjectString(value);
            return string.IsNullOrWhiteSpace(text)
                ? type.FullName ?? type.Name
                : text;
        }
        catch (Exception exception)
        {
            string typeName = type.FullName ?? type.Name;
            return $"{typeName} [debug_string_unavailable:{exception.GetType().Name}]";
        }
    }

    private static string ComputeStateFingerprint(Dictionary<string, object?> state)
    {
        Dictionary<string, object?> comparableState = state
            .Where(pair => pair.Key is not ("state_id" or "exported_at_ms" or "debug" or "runtime_phase"))
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
        Dictionary<string, object?> potionSlotState = BuildPotionSlotState(player, recentPlayer);

        return new Dictionary<string, object?>
        {
            ["id"] = "player_0",
            ["hp"] = ReadFirstInt(new[] { player, recentPlayer, runtimeCreature }, "hp", "_hp", "currentHp", "_currentHp", "currentHealth", "_currentHealth", "health", "_health"),
            ["max_hp"] = ReadFirstInt(new[] { player, recentPlayer, runtimeCreature }, "maxHp", "_maxHp", "maxHealth", "_maxHealth"),
            ["block"] = ReadFirstInt(new[] { player, recentPlayer, runtimeCreature }, "block", "_block", "currentBlock", "_currentBlock", "shield", "_shield"),
            ["combat_id"] = ReadFirstInt(new[] { runtimeCreature, player, recentPlayer }, "CombatId", "combatId", "_combatId"),
            ["energy"] = ReadFirstInt(new[] { playerCombatState, player, recentPlayer }, "energy", "_energy", "currentEnergy", "_currentEnergy"),
            ["max_energy"] = ReadFirstInt(new[] { playerCombatState, player, recentPlayer }, "maxEnergy", "_maxEnergy", "energyMax", "_energyMax"),
            ["gold"] = ReadFirstInt(new[] { player, recentPlayer }, "gold", "_gold", "currentGold", "_currentGold"),
            ["potion_slots"] = potionSlotState["potion_slots"],
            ["filled_potion_slots"] = potionSlotState["filled_potion_slots"],
            ["max_potion_slots"] = potionSlotState["max_potion_slots"],
            ["has_open_potion_slots"] = potionSlotState["has_open_potion_slots"],
            ["buffs"] = powerGroups.Buffs,
            ["debuffs"] = powerGroups.Debuffs,
            ["powers_unknown"] = powerGroups.Unknown
        };
    }

    private static Dictionary<string, object?> BuildPotionSlotState(params object?[] sources)
    {
        object? slotsSource = sources
            .Select(source => FindMemberValue(
                source,
                "PotionSlots",
                "potionSlots",
                "_potionSlots",
                "Potions",
                "potions",
                "_potions"))
            .FirstOrDefault(value => value is not null);

        List<Dictionary<string, object?>> slots = new();
        if (slotsSource is IEnumerable enumerable && slotsSource is not string)
        {
            int index = 0;
            foreach (object? slot in enumerable)
            {
                object? potionModel = ExtractPotionModel(slot);
                Dictionary<string, object?>? potion = BuildPotionSummary(potionModel);
                slots.Add(new Dictionary<string, object?>
                {
                    ["slot_index"] = index,
                    ["empty"] = potion is null,
                    ["potion"] = potion
                });
                index++;
            }
        }

        int maxSlots = slots.Count;
        int filledSlots = slots.Count(slot => ReadDictionaryBool(slot, "empty") == false);
        return new Dictionary<string, object?>
        {
            ["potion_slots"] = slots,
            ["filled_potion_slots"] = maxSlots == 0 ? null : filledSlots,
            ["max_potion_slots"] = maxSlots == 0 ? null : maxSlots,
            ["has_open_potion_slots"] = maxSlots == 0 ? null : filledSlots < maxSlots
        };
    }

    private static object? ExtractPotionModel(object? source)
    {
        if (source is null)
        {
            return null;
        }

        object? nested = FindMemberValue(
            source,
            "Potion",
            "potion",
            "_potion",
            "PotionModel",
            "potionModel",
            "_potionModel",
            "Model",
            "model",
            "_model");
        if (nested is not null)
        {
            return nested;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        return typeName.Contains("Potion", StringComparison.OrdinalIgnoreCase)
            && !typeName.Contains("Slot", StringComparison.OrdinalIgnoreCase)
            ? source
            : null;
    }

    private static Dictionary<string, object?>? BuildPotionSummary(object? source)
    {
        Dictionary<string, object?>? summary = BuildItemSummary(source);
        if (summary is null)
        {
            return null;
        }

        string? targetType = ReadFirstString(new[] { source }, "TargetType", "targetType", "_targetType", "Target", "target", "_target");
        string targetKind = ResolvePotionTargetKind(targetType);
        bool requiresTarget = PotionRequiresTarget(targetType);
        bool? isQueued = ReadBool(source, "IsQueued", "isQueued", "_isQueued");
        bool? rawUsable = ReadBool(source, "IsUsable", "isUsable", "_isUsable", "CanUse", "canUse", "_canUse");
        bool? passesCustomUsabilityCheck = ReadBool(source, "PassesCustomUsabilityCheck", "passesCustomUsabilityCheck", "_passesCustomUsabilityCheck");
        string? usage = ReadFirstString(new[] { source }, "Usage", "usage", "_usage");
        summary["potion_id"] = summary["id"];
        summary["target_type"] = targetType;
        summary["target_kind"] = targetKind;
        summary["usage"] = usage;
        summary["requires_target"] = requiresTarget;
        summary["is_queued"] = isQueued;
        summary["passes_custom_usability_check"] = passesCustomUsabilityCheck;
        summary["is_usable_now"] = isQueued == true ? false : (rawUsable ?? true) && (passesCustomUsabilityCheck ?? true);
        return summary;
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
            StableCardIdentity stableIdentity = BuildStableCardIdentity(pileName, index, cardName, card, cardInfo, cardModel);

            int? resolvedCost = TryInvokeInt(energyCost, "GetAmountToSpend")
                ?? TryInvokeInt(energyCost, "GetResolved")
                ?? TryInvokeInt(energyCost, "GetWithModifiers", "All")
                ?? ReadFirstInt(new[] { card, cardModel, cardStats, cardInfo, energyCost }, "cost", "_cost", "currentCost", "_currentCost", "energyCost", "_energyCost", "EnergyCost", "CanonicalEnergyCost", "canonicalEnergyCost", "_canonicalEnergyCost", "calculatedEnergy", "_calculatedEnergy", "calculatedEnergyKey", "_calculatedEnergyKey");
            int? baseCost = ReadInt(energyCost, "Canonical")
                ?? ReadFirstInt(new[] { card, cardModel, cardStats, cardInfo, energyCost }, "baseCost", "_baseCost", "baseEnergyCost", "_baseEnergyCost", "energyCost", "_energyCost", "EnergyCost", "CanonicalEnergyCost", "canonicalEnergyCost", "_canonicalEnergyCost");
            bool? exportedPlayable = ReadBool(card, "playable", "canPlay", "isPlayable");
            bool? runtimeCanPlayNoTarget = pileName.Equals("hand", StringComparison.OrdinalIgnoreCase)
                ? TryInvokeBoolMethod(card, "CanPlayTargeting", new object?[] { null })
                : null;

            cards.Add(new Dictionary<string, object?>
            {
                ["instance_id"] = stableIdentity.InstanceId,
                ["combat_card_id"] = stableIdentity.CombatCardId,
                ["instance_id_source"] = stableIdentity.Source,
                ["fallback_instance_id"] = stableIdentity.FallbackInstanceId,
                ["card_id"] = ReadFirstString(new[] { card, cardModel, cardInfo }, "id", "_id", "cardId", "_cardId", "key", "_key") ?? fallbackName,
                ["name"] = cardName,
                ["type"] = ReadFirstString(new[] { card, cardModel, cardInfo, cardStats }, "type", "_type", "cardType", "_cardType"),
                ["cost"] = resolvedCost,
                ["base_cost"] = baseCost,
                ["upgraded"] = ReadBool(card, "upgraded", "isUpgraded"),
                ["playable"] = exportedPlayable,
                ["runtime_can_play_no_target"] = runtimeCanPlayNoTarget,
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

    private static StableCardIdentity BuildStableCardIdentity(string pileName, int index, string cardName, params object?[] sources)
    {
        string fallbackInstanceId = BuildFallbackCardInstanceId(pileName, index, cardName, sources);
        uint? combatCardId = TryReadCombatCardId(sources);
        if (combatCardId is not null)
        {
            return new StableCardIdentity(
                SanitizeActionId($"combat_card_{combatCardId.Value}"),
                combatCardId.Value,
                "net_combat_card_db",
                fallbackInstanceId);
        }

        return new StableCardIdentity(
            fallbackInstanceId,
            null,
            "fallback",
            fallbackInstanceId);
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

    private static string BuildFallbackCardInstanceId(string pileName, int index, string cardName, params object?[] sources)
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

    private static uint? TryReadCombatCardId(params object?[] sources)
    {
        object? runtimeCard = FindRuntimeCardModel(sources);
        if (runtimeCard is null)
        {
            return null;
        }

        bool? isMutable = ReadBool(runtimeCard, "IsMutable", "isMutable", "_isMutable");
        if (isMutable == false)
        {
            return null;
        }

        Type? databaseType = FindTypeByFullName("MegaCrit.Sts2.Core.GameActions.Multiplayer.NetCombatCardDb");
        if (databaseType is null)
        {
            return null;
        }

        object? database = databaseType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
        if (database is null)
        {
            return null;
        }

        MethodInfo? tryGetCardId = databaseType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                if (!method.Name.Equals("TryGetCardId", StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType.IsAssignableFrom(runtimeCard.GetType());
            });
        if (tryGetCardId is null)
        {
            return null;
        }

        object?[] args = { runtimeCard, null };
        try
        {
            object? result = tryGetCardId.Invoke(database, args);
            if (result is true && args[1] is not null)
            {
                return Convert.ToUInt32(args[1]);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static object? FindRuntimeCardModel(IEnumerable<object?> sources)
    {
        foreach (object? source in sources)
        {
            if (IsRuntimeCardModel(source))
            {
                return source;
            }
        }

        foreach (object? source in sources)
        {
            object? model = FindMemberValue(source, "Model", "model", "_model", "cardModel", "_cardModel");
            if (IsRuntimeCardModel(model))
            {
                return model;
            }
        }

        return null;
    }

    private static bool IsRuntimeCardModel(object? value)
    {
        if (value is null)
        {
            return false;
        }

        Type type = value.GetType();
        string typeName = type.FullName ?? type.Name;
        return typeName.Equals("MegaCrit.Sts2.Core.Models.CardModel", StringComparison.Ordinal)
            || typeName.Contains(".Cards.", StringComparison.Ordinal)
            || IsAssignableToTypeName(type, "MegaCrit.Sts2.Core.Models.CardModel");
    }

    private static bool IsAssignableToTypeName(Type type, string expectedFullName)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.FullName, expectedFullName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (Type interfaceType in type.GetInterfaces())
        {
            if (string.Equals(interfaceType.FullName, expectedFullName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Type? FindTypeByFullName(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type? type = assembly.GetType(fullName, throwOnError: false);
                if (type is not null)
                {
                    return type;
                }
            }
            catch
            {
                continue;
            }
        }

        return null;
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

    private static string? ReadFormattedText(object? source)
    {
        if (source is null)
        {
            return null;
        }

        if (source is string text)
        {
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        string? formatted = TryInvokeMethod(source, "GetFormattedText") as string;
        if (!string.IsNullOrWhiteSpace(formatted))
        {
            return formatted;
        }

        string? raw = TryInvokeMethod(source, "GetRawText") as string;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        return ReadObjectString(source);
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

    private static List<string> ReadDictionaryStringList(Dictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out object? value) || value is null)
        {
            return new List<string>();
        }

        if (value is List<string> stringList)
        {
            return stringList;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            return enumerable
                .Cast<object?>()
                .Select(item => item?.ToString())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Cast<string>()
                .ToList();
        }

        string? singleValue = value.ToString();
        return string.IsNullOrWhiteSpace(singleValue)
            ? new List<string>()
            : new List<string> { singleValue };
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
            object? enemyCreature = FindMemberValue(enemy, "Creature", "creature");
            int? combatId = ReadFirstInt(new[] { enemy, enemyCreature }, "CombatId", "combatId", "_combatId");
            int? hp = ReadInt(enemy, "hp", "currentHp", "currentHealth", "health");
            int? maxHp = ReadInt(enemy, "maxHp", "maxHealth");
            if (!IsRuntimeEnemyCandidate(enemy, combatId, hp, maxHp))
            {
                continue;
            }

            PowerGroups powerGroups = BuildPowerGroups(enemy, enemyCreature);

            result.Add(new Dictionary<string, object?>
            {
                ["id"] = ReadString(enemy, "id", "enemyId", "monsterId") ?? $"enemy_{index}",
                ["combat_id"] = combatId,
                ["name"] = ReadString(enemy, "name", "displayName") ?? GetReadableName(enemy),
                ["hp"] = hp,
                ["max_hp"] = maxHp,
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

    private static bool IsRuntimeEnemyCandidate(object enemy, int? combatId, int? hp, int? maxHp)
    {
        if (combatId is not null)
        {
            return true;
        }

        if (hp is not null || maxHp is not null)
        {
            return true;
        }

        string typeName = enemy.GetType().FullName ?? enemy.GetType().Name;
        if (ContainsAny(typeName, "List", "Tuple", "Dictionary", "Enumerable", "Collection"))
        {
            return false;
        }

        return false;
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

    private static bool? TryInvokeBoolMethod(object? source, string methodName)
    {
        object? value = TryInvokeMethod(source, methodName);
        return value is bool result ? result : null;
    }

    private static bool? TryInvokeBoolMethod(object? source, string methodName, params object?[] args)
    {
        object? value = TryInvokeMethod(source, methodName, args);
        return value is bool result ? result : null;
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
            Dictionary<string, object?> summary = BuildItemSummary(relic)
                ?? new Dictionary<string, object?>();
            result.Add(new Dictionary<string, object?>
            {
                ["id"] = ReadDictionaryString(summary, "id") ?? GetReadableName(relic),
                ["name"] = ReadDictionaryString(summary, "name") ?? GetReadableName(relic),
                ["description"] = ReadDictionaryString(summary, "description"),
                ["rarity"] = ReadDictionaryString(summary, "rarity"),
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

        try
        {
            string? text = value.ToString();
            string typeName = value.GetType().FullName ?? value.GetType().Name;
            return string.Equals(text, typeName, StringComparison.Ordinal)
                ? null
                : text;
        }
        catch
        {
            return null;
        }
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

    private static IEnumerable<object> EnumerateNodeDescendants(object? node)
    {
        if (node is null)
        {
            yield break;
        }

        Queue<object> queue = new();
        queue.Enqueue(node);
        int visitedCount = 0;
        while (queue.Count > 0 && visitedCount < 512)
        {
            object current = queue.Dequeue();
            visitedCount++;
            yield return current;

            object? children = TryInvokeMethod(current, "GetChildren") ?? TryInvokeMethod(current, "GetChildren", false);
            foreach (object child in EnumerateObjects(children).Where(value => !IsScalar(value.GetType())))
            {
                queue.Enqueue(child);
            }
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

    private sealed record StableCardIdentity(
        string InstanceId,
        uint? CombatCardId,
        string Source,
        string FallbackInstanceId);

    private sealed record CardSelectionSourceHint(
        string? CardId,
        string? CardName,
        bool? Upgraded,
        long ObservedAtMs);
}
