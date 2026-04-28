using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using HarmonyLib;

namespace SpireMindMod;

internal static class AutotestCommandChannel
{
    private const int PollIntervalMs = 250;
    private const int MaxExecutedHistory = 128;

    private static readonly SpireMindLogger Logger = new("SpireMind.Autotest");
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly Queue<string> ExecutedOrder = new();
    private static readonly HashSet<string> ExecutedCommandIds = new(StringComparer.Ordinal);
    private static long lastPollAtMs;
    private static int mainThreadId;
    private static int commandInFlight;
    private static int continueRunInFlight;
    private static ContinueRunOperation? continueRunOperation;

    public static void Tick()
    {
        long nowMs = Environment.TickCount64;
        if (nowMs - lastPollAtMs < PollIntervalMs)
        {
            return;
        }

        lastPollAtMs = nowMs;
        // 배경 스레드에서는 파일만 확인한다. Godot 객체를 만지는 continue_run 진행은
        // TickMainThread에서만 처리해 스레드 위반과 재진입 위험을 줄인다.
        TryProcessCommandFile();
    }

    public static void TickMainThread()
    {
        int currentThreadId = Thread.CurrentThread.ManagedThreadId;
        if (Interlocked.CompareExchange(ref mainThreadId, currentThreadId, 0) == 0)
        {
            Logger.Info($"autotest 메인 스레드 기준을 기록했습니다. thread_id={currentThreadId}");
        }

        TryAdvanceContinueRunOperation();
    }

    private static void TryProcessCommandFile()
    {
        string commandPath = GetCommandPath();
        try
        {
            if (!File.Exists(commandPath))
            {
                return;
            }

            string json = File.ReadAllText(commandPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            AutotestCommand? command = JsonSerializer.Deserialize<AutotestCommand>(json, JsonOptions);
            if (command is null || string.IsNullOrWhiteSpace(command.Id))
            {
                WriteResult(new AutotestCommandResult(
                    Id: command?.Id ?? string.Empty,
                    Action: command?.Action ?? string.Empty,
                    Status: "failed",
                    Message: "紐낅졊 id ?먮뒗 action???쎌쓣 ???놁뒿?덈떎.",
                    Timestamp: DateTimeOffset.UtcNow));
                return;
            }

            if (HasExecuted(command.Id))
            {
                return;
            }

            if (Interlocked.CompareExchange(ref commandInFlight, 1, 0) != 0)
            {
                return;
            }

            RememberExecuted(command.Id);
            ExecuteCommand(command);
        }
        catch (JsonException exception)
        {
            WriteResult(new AutotestCommandResult(
                Id: string.Empty,
                Action: string.Empty,
                Status: "failed",
                Message: $"紐낅졊 ?뚯씪 JSON ?뚯떛 ?ㅽ뙣: {exception.Message}",
                Timestamp: DateTimeOffset.UtcNow));
            Logger.Warning($"autotest_command.json ?뚯떛 ?ㅽ뙣: {exception.Message}");
        }
        catch (Exception exception)
        {
            WriteResult(new AutotestCommandResult(
                Id: string.Empty,
                Action: string.Empty,
                Status: "failed",
                Message: $"{exception.GetType().Name}: {exception.Message}",
                Timestamp: DateTimeOffset.UtcNow));
            Logger.Warning($"autotest 紐낅졊 ?뺤씤 以??덉쇅媛 諛쒖깮?덉뒿?덈떎. 寃뚯엫? 怨꾩냽 吏꾪뻾?⑸땲?? {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void ExecuteCommand(AutotestCommand command)
    {
        try
        {
            if (command.Action.Equals("enter_combat_debug", StringComparison.OrdinalIgnoreCase))
            {
                TryEnterCombatDebug(command);
                return;
            }

            if (command.Action.Equals("continue_run", StringComparison.OrdinalIgnoreCase))
            {
                TryContinueRun(command);
                return;
            }

            WriteCommandResult(command, "rejected", $"吏?먰븯吏 ?딅뒗 action?낅땲?? {command.Action}");
            Interlocked.Exchange(ref commandInFlight, 0);
            return;
        }
        catch (Exception exception)
        {
            WriteCommandResult(command, "failed", $"{exception.GetType().Name}: {exception.Message}");
            Interlocked.Exchange(ref commandInFlight, 0);
        }
    }

    private static void TryContinueRun(AutotestCommand command)
    {
        Dictionary<string, object?> diagnostics = CreateContinueRunDiagnostics();

        if (Interlocked.CompareExchange(ref continueRunInFlight, 1, 0) != 0)
        {
            WriteCommandResult(command, "rejected", "continue_run???대? ?ㅽ뻾 以묒엯?덈떎.", diagnostics);
            Interlocked.Exchange(ref commandInFlight, 0);
            return;
        }

        try
        {
            diagnostics["continue_stage"] = "defer_continue_button_click";
            diagnostics["continue_mode"] = "deferred_continue_button";
            diagnostics["state_machine"] = "tick";
            diagnostics["direct_continue_method_used"] = false;
            diagnostics["direct_load_run_used"] = false;
            diagnostics["direct_run_manager_assembly_used"] = false;
            diagnostics["custom_sync_context_used"] = false;
            diagnostics["task_pump_used"] = false;
            diagnostics["timeout_ms"] = ReadInt(command.Params, "timeout_ms") ?? ContinueRunOperation.DefaultTimeoutMs;
            diagnostics["ready_timeout_ms"] = ReadInt(command.Params, "ready_timeout_ms") ?? ContinueRunOperation.DefaultReadyTimeoutMs;

            continueRunOperation = new ContinueRunOperation(command, diagnostics);
            WriteCommandResult(command, "started", "메인 메뉴 Continue 버튼 deferred 클릭을 준비합니다.", diagnostics);
            Logger.Info($"continue_run deferred Continue 버튼 경로 시작: id={command.Id}");
            Interlocked.Exchange(ref commandInFlight, 0);
            return;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            diagnostics["exception_type"] = exception.InnerException.GetType().Name;
            WriteCommandResult(command, "failed", $"{exception.InnerException.GetType().Name}: {exception.InnerException.Message}", diagnostics);
            Logger.Warning($"continue_run ?몄텧 ?ㅽ뙣: {exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
            ReleaseContinueRunCommand();
        }
        catch (Exception exception)
        {
            diagnostics["exception_type"] = exception.GetType().Name;
            WriteCommandResult(command, "failed", $"{exception.GetType().Name}: {exception.Message}", diagnostics);
            Logger.Warning($"continue_run 泥섎━ 以??덉쇅媛 諛쒖깮?덉뒿?덈떎. 寃뚯엫? 怨꾩냽 吏꾪뻾?⑸땲?? {exception.GetType().Name}: {exception.Message}");
            ReleaseContinueRunCommand();
        }
    }

    private static bool TryAdvanceContinueRunOperation()
    {
        ContinueRunOperation? operation = continueRunOperation;
        if (operation is null)
        {
            return false;
        }

        operation.CaptureCommandFileDiagnostics();
        bool completed = operation.Tick();
        if (completed)
        {
            continueRunOperation = null;
        }

        return true;
    }

    private static void ReleaseContinueRunCommand()
    {
        Interlocked.Exchange(ref continueRunInFlight, 0);
        Interlocked.Exchange(ref commandInFlight, 0);
    }

    private static void TryEnterCombatDebug(AutotestCommand command)
    {
        Type? runManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager");
        object? runManager = ReadStaticMember(runManagerType, "Instance");
        if (runManager is null)
        {
            WriteCommandResult(command, "failed", "RunManager.Instance瑜?李얠쓣 ???놁뒿?덈떎.");
            Interlocked.Exchange(ref commandInFlight, 0);
            return;
        }

        MethodInfo? getState = FindMethod(runManager.GetType(), "DebugOnlyGetState", 0);
        object? runState = getState?.Invoke(runManager, Array.Empty<object>());
        if (runState is null)
        {
            WriteCommandResult(command, "failed", "?꾩옱 RunState媛 ?놁뒿?덈떎. 癒쇱? ?곗뿉 吏꾩엯?댁빞 ?⑸땲??");
            Interlocked.Exchange(ref commandInFlight, 0);
            return;
        }

        if (IsCombatInProgress())
        {
            WriteCommandResult(command, "rejected", "?대? ?꾪닾 以묒씠誘濡?enter_combat_debug瑜??ㅽ뻾?섏? ?딆븯?듬땲??");
            Interlocked.Exchange(ref commandInFlight, 0);
            return;
        }

        string? encounterId = ReadString(command.Params, "encounter_id");
        if (!string.IsNullOrWhiteSpace(encounterId))
        {
            WriteCommandResult(command, "rejected", "encounter_id 吏?뺤? ?꾩쭅 ?덉젙 吏?먰븯吏 ?딆뒿?덈떎. null???ъ슜?섏꽭??");
            Interlocked.Exchange(ref commandInFlight, 0);
            return;
        }

        Type? roomTypeType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Rooms.RoomType");
        Type? mapPointTypeType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Map.MapPointType");
        if (roomTypeType is null || mapPointTypeType is null)
        {
            WriteCommandResult(command, "failed", "RoomType ?먮뒗 MapPointType ??낆쓣 李얠쓣 ???놁뒿?덈떎.");
            Interlocked.Exchange(ref commandInFlight, 0);
            return;
        }

        object roomType = ParseEnumOrDefault(roomTypeType, ReadString(command.Params, "room_type"), "Monster");
        object mapPointType = ParseEnumOrDefault(mapPointTypeType, ReadString(command.Params, "map_point_type"), "Monster");
        bool showTransition = ReadBool(command.Params, "show_transition") ?? false;

        MethodInfo? enterRoomDebug = runManager.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                if (!method.Name.Equals("EnterRoomDebug", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 4
                    && parameters[0].ParameterType == roomTypeType
                    && parameters[1].ParameterType == mapPointTypeType;
            });

        if (enterRoomDebug is null)
        {
            WriteCommandResult(command, "failed", "RunManager.EnterRoomDebug(roomType, pointType, model, showTransition)瑜?李얠쓣 ???놁뒿?덈떎.");
            Interlocked.Exchange(ref commandInFlight, 0);
            return;
        }

        object? task = enterRoomDebug.Invoke(runManager, new[] { roomType, mapPointType, null, showTransition });
        if (task is Task runningTask)
        {
            _ = CompleteEnterCombatCommandAsync(command, runningTask);
            return;
        }

        WriteCommandResult(command, "applied", "debug combat entry requested");
        Logger.Info($"enter_combat_debug 紐낅졊 ?곸슜: id={command.Id}");
        Interlocked.Exchange(ref commandInFlight, 0);
    }

    private static async Task CompleteEnterCombatCommandAsync(AutotestCommand command, Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            WriteCommandResult(command, "applied", "debug combat entry requested");
            Logger.Info($"enter_combat_debug 紐낅졊 ?곸슜: id={command.Id}");
        }
        catch (Exception exception)
        {
            WriteCommandResult(command, "failed", $"{exception.GetType().Name}: {exception.Message}");
            Logger.Warning($"enter_combat_debug 紐낅졊 ?ㅽ뻾 ?ㅽ뙣: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref commandInFlight, 0);
        }
    }

    private static bool IsCombatInProgress()
    {
        Type? combatManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Combat.CombatManager");
        object? combatManager = ReadStaticMember(combatManagerType, "Instance");
        object? value = ReadInstanceMember(combatManager, "IsInProgress");
        return value is bool isInProgress && isInProgress;
    }

    private static bool IsCombatPlayPhase()
    {
        Type? combatManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Combat.CombatManager");
        object? combatManager = ReadStaticMember(combatManagerType, "Instance");
        object? value = ReadInstanceMember(combatManager, "IsPlayPhase");
        return value is bool isPlayPhase && isPlayPhase;
    }

    private static void CaptureCombatManagerDiagnostics(Dictionary<string, object?> diagnostics)
    {
        Type? combatManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Combat.CombatManager");
        object? combatManager = ReadStaticMember(combatManagerType, "Instance");
        object? combatState = TryInvokeNoArgMethod(combatManager, "DebugOnlyGetState");
        object? runManager = ReadStaticMember(AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance");
        object? currentRoom = ReadInstanceMember(runManager, "CurrentRoom")
            ?? ReadInstanceMember(runManager, "_currentRoom")
            ?? ReadInstanceMember(combatState, "Room");
        object? currentRoomType = ReadInstanceMember(currentRoom, "RoomType")
            ?? ReadInstanceMember(currentRoom, "Type");

        diagnostics["combat_manager_found"] = combatManager is not null;
        diagnostics["combat_manager_type"] = combatManager?.GetType().FullName;
        diagnostics["combat_manager_is_in_progress"] = ReadInstanceMember(combatManager, "IsInProgress") is bool isInProgress && isInProgress;
        diagnostics["combat_manager_is_play_phase"] = ReadInstanceMember(combatManager, "IsPlayPhase") is bool isPlayPhase && isPlayPhase;
        diagnostics["combat_manager_state_found"] = combatState is not null;
        diagnostics["combat_manager_state_type"] = combatState?.GetType().FullName;
        diagnostics["current_room_found"] = currentRoom is not null;
        diagnostics["current_room_type_name"] = currentRoom?.GetType().FullName;
        diagnostics["current_room_kind"] = currentRoomType?.ToString();
    }

    private static Dictionary<string, object?> CreateContinueRunDiagnostics()
    {
        Dictionary<string, object?> diagnostics = new(StringComparer.Ordinal)
        {
            ["n_game_found"] = false,
            ["main_menu_found"] = false,
            ["run_manager_found"] = false,
            ["is_in_progress"] = false,
            ["combat_in_progress"] = false
        };
        RefreshContinueRunDiagnostics(diagnostics);
        return diagnostics;
    }

    private static void RefreshContinueRunDiagnostics(Dictionary<string, object?> diagnostics)
    {
        object? nGame = ReadStaticMember(AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.NGame"), "Instance");
        object? runManager = ReadStaticMember(AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance");
        object? mainMenu = ReadInstanceMember(nGame, "MainMenu");
        RefreshContinueRunDiagnostics(diagnostics, nGame, mainMenu, runManager);
    }

    private static void RefreshContinueRunDiagnostics(
        Dictionary<string, object?> diagnostics,
        object? nGame,
        object? mainMenu,
        object? runManager)
    {
        diagnostics["n_game_found"] = nGame is not null;
        diagnostics["main_menu_found"] = mainMenu is not null;
        diagnostics["run_manager_found"] = runManager is not null;
        diagnostics["is_in_progress"] = ReadInstanceMember(runManager, "IsInProgress") is bool isInProgress && isInProgress;
        diagnostics["run_manager_is_in_progress"] = diagnostics["is_in_progress"];
        diagnostics["combat_in_progress"] = IsCombatInProgress();
        diagnostics["combat_play_phase"] = IsCombatPlayPhase();
        diagnostics["n_run_instance_found"] = ReadStaticMember(AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.NRun"), "Instance") is not null;
        diagnostics["n_map_screen_instance_found"] = TryReadStaticMember(
            AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen"),
            "Instance",
            out object? nMapScreen,
            out _) && nMapScreen is not null;
        CaptureMapScreenDiagnostics(diagnostics, nMapScreen);
    }

    private static bool IsGameMainThread()
    {
        int recordedMainThreadId = Volatile.Read(ref mainThreadId);
        if (recordedMainThreadId != 0 && Thread.CurrentThread.ManagedThreadId == recordedMainThreadId)
        {
            return true;
        }

        Type? nGameType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.NGame");
        MethodInfo? isMainThread = nGameType?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name.Equals("IsMainThread", StringComparison.OrdinalIgnoreCase)
                && method.GetParameters().Length == 0);
        try
        {
            return isMainThread?.Invoke(null, Array.Empty<object>()) is bool value && value;
        }
        catch
        {
            return false;
        }
    }

    private static void CaptureThreadDiagnostics(Dictionary<string, object?> diagnostics, bool requiresMainThread)
    {
        int recordedMainThreadId = Volatile.Read(ref mainThreadId);
        bool executedOnMainThread = IsGameMainThread();
        diagnostics["autotest_tick_thread_id"] = Thread.CurrentThread.ManagedThreadId;
        diagnostics["main_thread_id"] = recordedMainThreadId == 0 ? null : recordedMainThreadId;
        diagnostics["stage_requires_main_thread"] = requiresMainThread;
        diagnostics["stage_queued_to_main_thread"] = requiresMainThread;
        diagnostics["stage_executed_on_main_thread"] = executedOnMainThread;
    }

    private static MapScreenReadyState GetMapScreenReadyState(object? nRun, object? mapScreen)
    {
        bool nRunInsideTree = InvokeBoolMethod(nRun, "IsInsideTree") ?? false;
        bool mapScreenInsideTree = InvokeBoolMethod(mapScreen, "IsInsideTree") ?? false;
        if (nRun is null)
        {
            return new MapScreenReadyState(false, "n_run_missing", nRunInsideTree, mapScreenInsideTree);
        }

        if (mapScreen is null)
        {
            return new MapScreenReadyState(false, "n_map_screen_missing", nRunInsideTree, mapScreenInsideTree);
        }

        if (!nRunInsideTree)
        {
            return new MapScreenReadyState(false, "n_run_not_inside_tree", nRunInsideTree, mapScreenInsideTree);
        }

        if (!mapScreenInsideTree)
        {
            return new MapScreenReadyState(false, "n_map_screen_not_inside_tree", nRunInsideTree, mapScreenInsideTree);
        }

        if (ReadInstanceMember(mapScreen, "_points") is null)
        {
            return new MapScreenReadyState(false, "map_points_container_missing", nRunInsideTree, mapScreenInsideTree);
        }

        if (ReadInstanceMember(mapScreen, "_marker") is null)
        {
            return new MapScreenReadyState(false, "map_marker_missing", nRunInsideTree, mapScreenInsideTree);
        }

        if (ReadInstanceMember(mapScreen, "Drawings") is null)
        {
            return new MapScreenReadyState(false, "map_drawings_missing", nRunInsideTree, mapScreenInsideTree);
        }

        if (ReadInstanceMember(mapScreen, "_mapBgContainer") is null)
        {
            return new MapScreenReadyState(false, "map_background_container_missing", nRunInsideTree, mapScreenInsideTree);
        }

        if (ReadInstanceMember(mapScreen, "_runState") is null)
        {
            return new MapScreenReadyState(false, "map_screen_run_state_missing", nRunInsideTree, mapScreenInsideTree);
        }

        return new MapScreenReadyState(true, "ready", nRunInsideTree, mapScreenInsideTree);
    }

    private static void CaptureMapScreenDiagnostics(Dictionary<string, object?> diagnostics)
    {
        TryReadStaticMember(
            AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen"),
            "Instance",
            out object? mapScreen,
            out Exception? exception);
        diagnostics["n_map_screen_instance_exception_type"] = exception?.GetType().Name;
        diagnostics["n_map_screen_instance_exception_message"] = exception?.Message;
        CaptureMapScreenDiagnostics(diagnostics, mapScreen);
    }

    private static void CaptureMapScreenDiagnostics(Dictionary<string, object?> diagnostics, object? mapScreen)
    {
        diagnostics["n_map_screen_instance_type"] = mapScreen?.GetType().FullName;
        if (mapScreen is null)
        {
            diagnostics["map_point_container_found"] = false;
            diagnostics["map_point_container_child_count"] = null;
            diagnostics["map_data_point_count"] = null;
            return;
        }

        object? pointsContainer = ReadInstanceMember(mapScreen, "_points");
        object? map = ReadInstanceMember(mapScreen, "_map");
        object? runState = ReadInstanceMember(mapScreen, "_runState");
        object? stateMap = ReadInstanceMember(runState, "Map");
        diagnostics["map_point_container_found"] = pointsContainer is not null;
        diagnostics["map_point_container_type"] = pointsContainer?.GetType().FullName;
        diagnostics["map_point_container_child_count"] = InvokeIntMethod(pointsContainer, "GetChildCount");
        diagnostics["map_marker_found"] = ReadInstanceMember(mapScreen, "_marker") is not null;
        diagnostics["map_drawings_found"] = ReadInstanceMember(mapScreen, "Drawings") is not null;
        diagnostics["map_background_container_found"] = ReadInstanceMember(mapScreen, "_mapBgContainer") is not null;
        diagnostics["map_screen_run_state_found"] = runState is not null;
        diagnostics["map_screen_map_found"] = map is not null;
        diagnostics["run_state_map_found"] = stateMap is not null;
        diagnostics["map_data_point_count"] = CountMapPoints(map ?? stateMap);
        diagnostics["map_row_count"] = InvokeIntMethod(map ?? stateMap, "GetRowCount");
        diagnostics["map_column_count"] = InvokeIntMethod(map ?? stateMap, "GetColumnCount");
    }

    private static int? InvokeIntMethod(object? source, string methodName)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            object? result = FindMethod(source.GetType(), methodName, 0)?.Invoke(source, Array.Empty<object>());
            return result is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? CountMapPoints(object? map)
    {
        if (map is null)
        {
            return null;
        }

        try
        {
            object? allMapPoints = FindMethod(map.GetType(), "GetAllMapPoints", 0)?.Invoke(map, Array.Empty<object>());
            return allMapPoints is System.Collections.IEnumerable enumerable
                ? enumerable.Cast<object>().Count()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsContinueButtonUsable(object mainMenu, Dictionary<string, object?> diagnostics)
    {
        object? continueButton = ReadInstanceMember(mainMenu, "_continueButton");
        object? readRunSaveResult = ReadInstanceMember(mainMenu, "_readRunSaveResult");
        bool buttonFound = continueButton is not null;
        bool buttonEnabled = ReadInstanceMember(continueButton, "IsEnabled") is bool enabled && enabled;
        bool buttonVisible = InvokeBoolMethod(continueButton, "IsVisible") ?? false;
        bool buttonVisibleInTree = InvokeBoolMethod(continueButton, "IsVisibleInTree") ?? false;
        bool readSaveFound = readRunSaveResult is not null;
        bool readSaveSuccess = ReadInstanceMember(readRunSaveResult, "Success") is bool success && success;
        bool saveDataFound = ReadInstanceMember(readRunSaveResult, "SaveData") is not null;

        diagnostics["continue_button_found"] = buttonFound;
        diagnostics["continue_button_type"] = continueButton?.GetType().FullName;
        diagnostics["continue_button_enabled"] = buttonEnabled;
        diagnostics["continue_button_visible"] = buttonVisible;
        diagnostics["continue_button_visible_in_tree"] = buttonVisibleInTree;
        diagnostics["read_run_save_result_found"] = readSaveFound;
        diagnostics["read_run_save_success"] = readSaveSuccess;
        diagnostics["save_data_found"] = saveDataFound;

        return buttonFound && buttonEnabled && buttonVisible && buttonVisibleInTree && readSaveFound && readSaveSuccess && saveDataFound;
    }

    private static bool TryCallDeferredNoArgs(
        object source,
        string methodName,
        Dictionary<string, object?> diagnostics,
        string diagnosticsPrefix,
        out string failureReason)
    {
        failureReason = string.Empty;
        MethodInfo? targetMethod = FindMethod(source.GetType(), methodName, 0);
        MethodInfo? callDeferred = source.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                if (!method.Name.Equals("CallDeferred", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 2 && parameters[1].ParameterType.IsArray;
            });

        diagnostics[$"{diagnosticsPrefix}_target_method_found"] = targetMethod is not null;
        diagnostics[$"{diagnosticsPrefix}_call_deferred_found"] = callDeferred is not null;
        diagnostics[$"{diagnosticsPrefix}_call_deferred_signature"] = DescribeMethod(callDeferred);

        if (targetMethod is null)
        {
            failureReason = $"{methodName} 메서드를 찾을 수 없습니다.";
            return false;
        }

        if (callDeferred is null)
        {
            failureReason = "Godot CallDeferred 메서드를 찾을 수 없습니다.";
            return false;
        }

        ParameterInfo[] callDeferredParameters = callDeferred.GetParameters();
        object? deferredMethodName = CreateGodotStringName(callDeferredParameters[0].ParameterType, methodName);
        Array emptyArguments = Array.CreateInstance(callDeferredParameters[1].ParameterType.GetElementType()!, 0);
        diagnostics[$"{diagnosticsPrefix}_string_name_created"] = deferredMethodName is not null;
        if (deferredMethodName is null)
        {
            failureReason = "Godot StringName 값을 만들 수 없습니다.";
            return false;
        }

        // 실제 클릭은 다음 Godot 프레임에서 게임의 정상 UI 신호 흐름으로 실행되게 한다.
        callDeferred.Invoke(source, new object[] { deferredMethodName, emptyArguments });
        diagnostics[$"{diagnosticsPrefix}_queued"] = true;
        return true;
    }

    private static object? CreateGodotStringName(Type targetType, string value)
    {
        if (targetType == typeof(string))
        {
            return value;
        }

        try
        {
            return Activator.CreateInstance(targetType, value);
        }
        catch
        {
            return null;
        }
    }

    private static object ParseEnumOrDefault(Type enumType, string? rawValue, string defaultValue)
    {
        string value = string.IsNullOrWhiteSpace(rawValue) ? defaultValue : rawValue;
        try
        {
            return Enum.Parse(enumType, value, ignoreCase: true);
        }
        catch
        {
            return Enum.Parse(enumType, defaultValue, ignoreCase: true);
        }
    }

    private static MethodInfo? FindMethod(Type type, string methodName, int parameterCount)
    {
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && method.GetParameters().Length == parameterCount);
    }

    private static MethodInfo? FindStaticMethod(Type? type, string methodName, int parameterCount)
    {
        if (type is null)
        {
            return null;
        }

        return type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && method.GetParameters().Length == parameterCount);
    }

    private static MethodInfo? FindCompatibleMethod(Type type, string methodName, Type?[] argumentTypes)
    {
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                if (!method.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != argumentTypes.Length)
                {
                    return false;
                }

                for (int index = 0; index < parameters.Length; index++)
                {
                    Type? argumentType = argumentTypes[index];
                    if (argumentType is not null && !parameters[index].ParameterType.IsAssignableFrom(argumentType))
                    {
                        return false;
                    }
                }

                return true;
            });
    }

    private static object? BuildCharacterEnumerable(object? players, Type? targetEnumerableType)
    {
        if (players is not System.Collections.IEnumerable playerEnumerable || targetEnumerableType is null)
        {
            return null;
        }

        Type? characterType = targetEnumerableType.IsGenericType
            ? targetEnumerableType.GetGenericArguments().FirstOrDefault()
            : null;
        if (characterType is null)
        {
            return null;
        }

        Array characterArray = Array.CreateInstance(characterType, playerEnumerable.Cast<object>()
            .Select(player => ReadInstanceMember(player, "Character"))
            .Where(character => character is not null)
            .Count());

        int index = 0;
        foreach (object player in playerEnumerable)
        {
            object? character = ReadInstanceMember(player, "Character");
            if (character is null)
            {
                continue;
            }

            characterArray.SetValue(character, index);
            index++;
        }

        return characterArray;
    }

    private static void TryLoadMapDrawings(object runManager, object mapDrawingsToLoad, Dictionary<string, object?> diagnostics)
    {
        try
        {
            Type? nRunType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.NRun");
            object? nRun = ReadStaticMember(nRunType, "Instance");
            object? globalUi = ReadInstanceMember(nRun, "GlobalUi");
            object? mapScreen = ReadInstanceMember(globalUi, "MapScreen");
            object? drawings = ReadInstanceMember(mapScreen, "Drawings");
            MethodInfo? loadDrawings = drawings is null ? null : FindMethod(drawings.GetType(), "LoadDrawings", 1);
            diagnostics["map_drawings_n_run_found"] = nRun is not null;
            diagnostics["map_drawings_global_ui_found"] = globalUi is not null;
            diagnostics["map_drawings_map_screen_found"] = mapScreen is not null;
            diagnostics["map_drawings_drawings_found"] = drawings is not null;
            diagnostics["map_drawings_load_drawings_found"] = loadDrawings is not null;
            diagnostics["map_drawings_load_drawings_signature"] = DescribeMethod(loadDrawings);
            loadDrawings?.Invoke(drawings, new[] { mapDrawingsToLoad });
            PropertyInfo? mapDrawingsProperty = runManager.GetType().GetProperty("MapDrawingsToLoad", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            mapDrawingsProperty?.SetValue(runManager, null);
            diagnostics["map_drawings_load_success"] = loadDrawings is not null;
        }
        catch (Exception exception)
        {
            diagnostics["map_drawings_load_success"] = false;
            diagnostics["map_drawings_load_exception_type"] = exception.GetType().Name;
            diagnostics["map_drawings_load_exception_message"] = exception.Message;
            Logger.Warning($"continue_run 지도 필기 복원 중 예외가 발생했지만 런 복원은 계속 진행합니다. {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string[] DescribeMethodCandidates(Type type, string methodName)
    {
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
            .Select(DescribeMethod)
            .Where(description => description is not null)
            .Cast<string>()
            .ToArray();
    }

    private static string? DescribeMethod(MethodInfo? method)
    {
        if (method is null)
        {
            return null;
        }

        string parameters = string.Join(", ", method.GetParameters().Select(parameter =>
            $"{parameter.ParameterType.FullName} {parameter.Name}"));
        return $"{method.ReturnType.FullName} {method.DeclaringType?.FullName}.{method.Name}({parameters})";
    }

    private static string? SummarizeStackTrace(Exception exception)
    {
        if (string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            return null;
        }

        return exception.StackTrace
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault();
    }

    private static void TryInvokeVoidMethod(object? source, string methodName)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            FindMethod(source.GetType(), methodName, 0)?.Invoke(source, Array.Empty<object>());
        }
        catch (Exception exception)
        {
            Logger.Warning($"{methodName} ?몄텧 以??덉쇅媛 諛쒖깮?덉?留?continue_run 吏꾨떒??怨꾩냽?⑸땲?? {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static object? TryInvokeNoArgMethod(object? source, string methodName)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            return FindMethod(source.GetType(), methodName, 0)?.Invoke(source, Array.Empty<object>());
        }
        catch
        {
            return null;
        }
    }

    private static bool? InvokeBoolMethod(object? source, string methodName)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            object? result = FindMethod(source.GetType(), methodName, 0)?.Invoke(source, Array.Empty<object>());
            return result is bool value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static object? ReadStaticMember(Type? type, string memberName)
    {
        if (type is null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        MemberInfo? member = type.GetMember(memberName, flags)
            .FirstOrDefault(candidate => candidate is FieldInfo or PropertyInfo);
        return ReadMember(null, member);
    }

    private static bool TryReadStaticMember(Type? type, string memberName, out object? value, out Exception? exception)
    {
        value = null;
        exception = null;
        if (type is null)
        {
            return false;
        }

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MemberInfo? member = type.GetMember(memberName, flags)
                .FirstOrDefault(candidate => candidate is FieldInfo or PropertyInfo);
            value = member switch
            {
                FieldInfo field => field.GetValue(null),
                PropertyInfo property when property.GetMethod is not null => property.GetValue(null),
                _ => null
            };
            return member is not null;
        }
        catch (TargetInvocationException targetException) when (targetException.InnerException is not null)
        {
            exception = targetException.InnerException;
            return false;
        }
        catch (Exception caughtException)
        {
            exception = caughtException;
            return false;
        }
    }

    private static object? ReadInstanceMember(object? source, string memberName)
    {
        if (source is null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MemberInfo? member = source.GetType().GetMember(memberName, flags)
            .FirstOrDefault(candidate => candidate is FieldInfo or PropertyInfo);
        return ReadMember(source, member);
    }

    private static object? ReadMember(object? source, MemberInfo? member)
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

    private static string? ReadString(Dictionary<string, JsonElement>? values, string propertyName)
    {
        if (values is null || !values.TryGetValue(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => value.ToString()
        };
    }

    private static bool? ReadBool(Dictionary<string, JsonElement>? values, string propertyName)
    {
        if (values is null || !values.TryGetValue(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
            _ => null
        };
    }

    private static int? ReadInt(Dictionary<string, JsonElement>? values, string propertyName)
    {
        if (values is null || !values.TryGetValue(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out int parsed) => parsed,
            _ => null
        };
    }

    private static bool HasExecuted(string commandId)
    {
        lock (SyncRoot)
        {
            return ExecutedCommandIds.Contains(commandId);
        }
    }

    private static void RememberExecuted(string commandId)
    {
        lock (SyncRoot)
        {
            if (!ExecutedCommandIds.Add(commandId))
            {
                return;
            }

            ExecutedOrder.Enqueue(commandId);
            while (ExecutedOrder.Count > MaxExecutedHistory)
            {
                string oldCommandId = ExecutedOrder.Dequeue();
                ExecutedCommandIds.Remove(oldCommandId);
            }
        }
    }

    private static void WriteCommandResult(AutotestCommand command, string status, string message)
    {
        WriteCommandResult(command, status, message, null);
    }

    private static void WriteCommandResult(
        AutotestCommand command,
        string status,
        string message,
        Dictionary<string, object?>? diagnostics)
    {
        WriteResult(new AutotestCommandResult(
            Id: command.Id,
            Action: command.Action,
            Status: status,
            Message: message,
            Timestamp: DateTimeOffset.UtcNow,
            Diagnostics: diagnostics));
    }

    private static void WriteResult(AutotestCommandResult result)
    {
        try
        {
            string resultPath = GetResultPath();
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            string json = JsonSerializer.Serialize(result, JsonOptions);
            File.WriteAllText(resultPath, json, Encoding.UTF8);
        }
        catch (Exception exception)
        {
            Logger.Warning($"autotest_result.json 湲곕줉 ?ㅽ뙣: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string GetCommandPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "SlayTheSpire2", "SpireMind", "autotest_command.json");
    }

    private static string GetResultPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "SlayTheSpire2", "SpireMind", "autotest_result.json");
    }

    private sealed class ContinueRunOperation
    {
        internal const int DefaultTimeoutMs = 180000;
        internal const int DefaultReadyTimeoutMs = 120000;
        private const int ExportReadyGraceMs = 30000;
        private const int LoadLatestMapCoordTimeoutMs = 12000;
        private const int ExportReadyForceExportIntervalMs = 2000;
        private const int ExportReadyStableCombatMs = 1000;
        private const int WaitStageDiagnosticsIntervalMs = 1500;
        private const int WaitStageResultIntervalMs = 1500;

        private readonly AutotestCommand command;
        private readonly Dictionary<string, object?> diagnostics;
        private readonly long startedAtMs = Environment.TickCount64;
        private readonly int timeoutMs;
        private readonly int readyTimeoutMs;
        private readonly bool forceExportRequested;

        private string stage = "defer_continue_button_click";
        private Task? pendingTask;
        private string? pendingTaskStage;
        private MainThreadContinuationContext? pendingContinuationContext;
        private MainThreadContinuationContext? retainedContinuationContext;
        private string? retainedContinuationStage;
        private long pendingTaskStartedAtMs;
        private long readyWaitStartedAtMs;
        private int readyWaitCount;
        private long exportReadyWaitStartedAtMs;
        private long exportReadyGraceStartedAtMs;
        private long exportReadyLastForceExportAtMs = -ExportReadyForceExportIntervalMs;
        private long exportReadyCombatInProgressSinceMs;
        private long lastWaitStageDiagnosticsAtMs;
        private long lastWaitStageResultAtMs;
        private int exportReadyWaitCount;
        private bool exportReadyGraceStarted;
        private object? nGame;
        private object? runManager;
        private object? mainMenu;
        private object? saveData;
        private object? runState;
        private object? preFinishedRoom;
        private object? runNode;
        private object? room;

        internal ContinueRunOperation(AutotestCommand command, Dictionary<string, object?> diagnostics)
        {
            this.command = command;
            this.diagnostics = diagnostics;
            timeoutMs = ReadInt(command.Params, "timeout_ms") ?? DefaultTimeoutMs;
            readyTimeoutMs = ReadInt(command.Params, "ready_timeout_ms") ?? DefaultReadyTimeoutMs;
            forceExportRequested = ReadBool(command.Params, "force_export") == true;
            diagnostics["force_export_requested"] = forceExportRequested;
        }

        internal void CaptureCommandFileDiagnostics()
        {
            diagnostics["active_command_id"] = command.Id;
            diagnostics["active_command_action"] = command.Action;

            try
            {
                string commandPath = GetCommandPath();
                diagnostics["active_command_file_exists"] = File.Exists(commandPath);
                if (!File.Exists(commandPath))
                {
                    return;
                }

                string json = File.ReadAllText(commandPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    diagnostics["active_command_file_empty"] = true;
                    return;
                }

                using JsonDocument document = JsonDocument.Parse(json);
                string? fileCommandId = ReadJsonString(document.RootElement, "id");
                string? fileCommandAction = ReadJsonString(document.RootElement, "action");
                diagnostics["active_command_file_id"] = fileCommandId;
                diagnostics["active_command_file_action"] = fileCommandAction;
                diagnostics["active_command_file_differs_from_active"] =
                    !string.IsNullOrWhiteSpace(fileCommandId)
                    && !fileCommandId.Equals(command.Id, StringComparison.Ordinal);
            }
            catch (Exception exception)
            {
                diagnostics["active_command_file_read_exception_type"] = exception.GetType().Name;
                diagnostics["active_command_file_read_exception_message"] = exception.Message;
            }
        }

        internal bool Tick()
        {
            try
            {
                diagnostics["continue_stage"] = stage;
                diagnostics["continue_mode"] = "deferred_continue_button";
                diagnostics["direct_continue_method_used"] = false;
                diagnostics["direct_load_run_used"] = false;
                diagnostics["direct_run_manager_assembly_used"] = false;
                diagnostics["custom_sync_context_used"] = false;
                diagnostics["task_pump_used"] = false;
                diagnostics["elapsed_ms"] = Environment.TickCount64 - startedAtMs;
                if (timeoutMs > 0 && Environment.TickCount64 - startedAtMs > timeoutMs)
                {
                    if (CanUseExportReadyGrace("overall_timeout"))
                    {
                        diagnostics["overall_timeout_deferred_by_export_ready_grace"] = true;
                    }
                    else
                    {
                        diagnostics["timed_out"] = true;
                        diagnostics["timeout_stage"] = pendingTaskStage ?? stage;
                        ClearRetainedContinuationContext("command_timeout");
                        Fail($"continue_run이 {timeoutMs}ms 안에 완료되지 않았습니다.");
                        return true;
                    }
                }

                bool requiresMainThread = StageRequiresMainThread(pendingTaskStage ?? stage);
                CaptureThreadDiagnostics(diagnostics, requiresMainThread);
                RefreshContinueRunDiagnosticsIfDue(force: stage != "wait_combat_export_ready");
                if (requiresMainThread && !IsGameMainThread())
                {
                    diagnostics["stage_queued_to_main_thread"] = true;
                    WriteRunningResultIfDue($"{stage} 단계가 메인 스레드 실행을 기다리는 중입니다.");
                    return false;
                }

                if (pendingTask is not null)
                {
                    return TickPendingTask();
                }

                switch (stage)
                {
                    case "defer_continue_button_click":
                        return DeferContinueButtonClick();
                    case "wait_combat_export_ready":
                        return WaitCombatExportReady();
                    case "read_save":
                        ReadSave();
                        return Running("저장 데이터를 확인했습니다.");
                    case "create_run_state":
                        CreateRunState();
                        return Running("RunState를 만들었습니다.");
                    case "setup_saved_single_player":
                        SetupSavedSinglePlayer();
                        return Running("저장된 싱글 플레이어 런을 설정했습니다.");
                    case "initialize_networking":
                        InitializeNetworking();
                        return Running("네트워크 서비스를 초기화했습니다.");
                    case "preload_run_assets":
                        StartPreloadRunAssets();
                        return Running("런 자산 불러오기를 시작했습니다.");
                    case "preload_act_assets":
                        StartPreloadActAssets();
                        return Running("막 자산 불러오기를 시작했습니다.");
                    case "launch_run":
                        LaunchRun();
                        return Running("RunManager.Launch를 호출했습니다.");
                    case "create_nrun_scene":
                        CreateNRunScene();
                        return Running("NRun 씬을 현재 씬으로 설정했습니다.");
                    case "wait_nrun_ready":
                        return WaitNRunReady();
                    case "generate_map":
                        StartGenerateMap();
                        return Running("지도 생성을 시작했습니다.");
                    case "deserialize_prefinished_room":
                        DeserializePrefinishedRoom();
                        return Running("이전 방 데이터를 복원했습니다.");
                    case "load_latest_map_coord":
                        StartLoadLatestMapCoord();
                        return Running("최신 지도 좌표 불러오기를 시작했습니다.");
                    case "load_map_drawings":
                        LoadMapDrawings();
                        return Running("지도 필기 복원을 확인했습니다.");
                    case "done":
                        RefreshContinueRunDiagnostics(diagnostics);
                        diagnostics["load_run_success"] = true;
                        ClearRetainedContinuationContext("command_applied");
                        WriteCommandResult(command, "applied", "continue_run이 정상 UI 흐름으로 전투 또는 지도 상태까지 도달했습니다.", diagnostics);
                        Logger.Info($"continue_run deferred Continue 버튼 경로 완료: id={command.Id}");
                        Interlocked.Exchange(ref continueRunInFlight, 0);
                        return true;
                    default:
                        Fail($"알 수 없는 continue_run 단계입니다: {stage}");
                        return true;
                }
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                RecordException(exception.InnerException);
                Fail($"{exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
                return true;
            }
            catch (Exception exception)
            {
                RecordException(exception);
                Fail($"{exception.GetType().Name}: {exception.Message}");
                return true;
            }
        }

        private bool DeferContinueButtonClick()
        {
            nGame = ReadStaticMember(AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.NGame"), "Instance");
            runManager = ReadStaticMember(AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance");
            mainMenu = ReadInstanceMember(nGame, "MainMenu");
            RefreshContinueRunDiagnostics(diagnostics, nGame, mainMenu, runManager);

            diagnostics["deferred_click_invoked_on_main_thread"] = IsGameMainThread();
            diagnostics["direct_run_manager_assembly_used"] = false;
            diagnostics["direct_load_run_used"] = false;
            diagnostics["custom_sync_context_used"] = false;
            diagnostics["task_pump_used"] = false;

            if (diagnostics.TryGetValue("combat_in_progress", out object? combatValue) && combatValue is true)
            {
                exportReadyWaitStartedAtMs = Environment.TickCount64;
                exportReadyWaitCount = 0;
                stage = "wait_combat_export_ready";
                WriteCommandResult(command, "running", "이미 전투 중이므로 전투 상태 export 준비만 확인합니다.", diagnostics);
                return false;
            }

            if (nGame is null)
            {
                Fail("NGame.Instance를 찾을 수 없어 Continue 버튼을 누를 수 없습니다.");
                return true;
            }

            if (mainMenu is null)
            {
                Reject("현재 화면에서 메인 메뉴 객체를 찾을 수 없어 Continue 버튼을 누르지 않았습니다.");
                return true;
            }

            TryInvokeVoidMethod(mainMenu, "RefreshButtons");
            if (!IsContinueButtonUsable(mainMenu, diagnostics))
            {
                Reject("저장된 런이 없거나 Continue 버튼이 비활성/비표시 상태라서 실행하지 않았습니다.");
                return true;
            }

            object? continueButton = ReadInstanceMember(mainMenu, "_continueButton");
            if (continueButton is null)
            {
                Reject("Continue 버튼 객체를 찾을 수 없어 실행하지 않았습니다.");
                return true;
            }

            if (!TryCallDeferredNoArgs(
                    continueButton,
                    "ForceClick",
                    diagnostics,
                    "continue_button_force_click",
                    out string failureReason))
            {
                Fail($"Continue 버튼 deferred ForceClick 예약에 실패했습니다. {failureReason}");
                return true;
            }

            exportReadyWaitStartedAtMs = Environment.TickCount64;
            exportReadyWaitCount = 0;
            stage = "wait_combat_export_ready";
            WriteCommandResult(command, "running", "Continue 버튼 deferred ForceClick을 예약했습니다. 이제 게임 정상 흐름이 전투를 만들 때까지 기다립니다.", diagnostics);
            return false;
        }

        private bool TickPendingTask()
        {
            PumpPendingContinuations();
            Task task = pendingTask ?? throw new InvalidOperationException("대기 중인 작업 정보가 없습니다.");
            string taskStage = pendingTaskStage ?? stage;
            long pendingElapsedMs = Environment.TickCount64 - pendingTaskStartedAtMs;
            diagnostics["continue_stage"] = taskStage;
            diagnostics[$"{taskStage}_task_completed"] = task.IsCompleted;
            diagnostics[$"{taskStage}_task_elapsed_ms"] = pendingElapsedMs;
            diagnostics[$"{taskStage}_task_status"] = task.Status.ToString();
            CapturePendingTaskDiagnostics(taskStage);
            if (timeoutMs > 0 && Environment.TickCount64 - startedAtMs > timeoutMs)
            {
                diagnostics["timed_out"] = true;
                diagnostics["timeout_stage"] = taskStage;
                Fail($"{taskStage} 작업이 완료되지 않아 continue_run을 실패로 종료합니다. timeout_ms={timeoutMs}");
                return true;
            }

            int stageTimeoutMs = GetStageTimeoutMs(taskStage);
            if (stageTimeoutMs > 0 && pendingElapsedMs > stageTimeoutMs)
            {
                diagnostics["timed_out"] = true;
                diagnostics["stage_timed_out"] = true;
                diagnostics["timeout_stage"] = taskStage;
                diagnostics["stage_timeout_ms"] = stageTimeoutMs;
                Fail($"{taskStage} 작업이 {stageTimeoutMs}ms 안에 완료되지 않아 continue_run을 실패로 종료합니다.");
                return true;
            }

            if (!task.IsCompleted)
            {
                WriteCommandResult(command, "running", $"{taskStage} 단계가 진행 중입니다.", diagnostics);
                return false;
            }

            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                RecordException(exception);
                Fail($"{exception.GetType().Name}: {exception.Message}");
                return true;
            }

            diagnostics[$"{taskStage}_success"] = true;
            RetainContinuationContextForExportWait(taskStage, pendingContinuationContext);
            pendingTask = null;
            pendingTaskStage = null;
            pendingContinuationContext = null;
            AdvanceAfterTask();
            WriteCommandResult(command, "running", $"{diagnostics["continue_stage"]} 단계로 이동했습니다.", diagnostics);
            return false;
        }

        private void ReadSave()
        {
            nGame = ReadStaticMember(AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.NGame"), "Instance");
            runManager = ReadStaticMember(AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance");
            mainMenu = ReadInstanceMember(nGame, "MainMenu");
            RefreshContinueRunDiagnostics(diagnostics, nGame, mainMenu, runManager);

            if (nGame is null)
            {
                throw new InvalidOperationException("NGame.Instance를 찾을 수 없습니다.");
            }

            if (runManager is null)
            {
                throw new InvalidOperationException("RunManager.Instance를 찾을 수 없습니다.");
            }

            if (diagnostics.TryGetValue("combat_in_progress", out object? combatValue) && combatValue is true)
            {
                throw new InvalidOperationException("전투 중에는 continue_run을 다시 실행하지 않습니다.");
            }

            if (diagnostics.TryGetValue("is_in_progress", out object? runValue) && runValue is true)
            {
                stage = "done";
                diagnostics["already_in_run"] = true;
                return;
            }

            if (mainMenu is null)
            {
                throw new InvalidOperationException("현재 화면에서 메인 메뉴 객체를 찾을 수 없습니다.");
            }

            TryInvokeVoidMethod(mainMenu, "RefreshButtons");
            diagnostics["continue_method_found"] = FindMethod(mainMenu.GetType(), "OnContinueButtonPressedAsync", 0) is not null;
            if (!IsContinueButtonUsable(mainMenu, diagnostics))
            {
                throw new InvalidOperationException("저장된 런이 없거나 Continue 버튼이 사용할 수 없는 상태입니다.");
            }

            object? readRunSaveResult = ReadInstanceMember(mainMenu, "_readRunSaveResult");
            saveData = ReadInstanceMember(readRunSaveResult, "SaveData");
            if (saveData is null)
            {
                throw new InvalidOperationException("저장된 런 데이터를 찾을 수 없습니다.");
            }

            object? continueButton = ReadInstanceMember(mainMenu, "_continueButton");
            TryInvokeVoidMethod(continueButton, "Disable");
            diagnostics["disable_continue_button_success"] = true;
            stage = "create_run_state";
        }

        private void CreateRunState()
        {
            Type? runStateType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunState");
            MethodInfo? fromSerializable = FindStaticMethod(runStateType, "FromSerializable", 1);
            diagnostics["run_state_from_serializable_found"] = fromSerializable is not null;
            if (fromSerializable is null)
            {
                throw new MissingMethodException("RunState.FromSerializable(save)를 찾을 수 없습니다.");
            }

            runState = fromSerializable.Invoke(null, new[] { saveData });
            diagnostics["run_state_from_serializable_success"] = runState is not null;
            if (runState is null)
            {
                throw new InvalidOperationException("RunState.FromSerializable(save)가 null을 반환했습니다.");
            }

            preFinishedRoom = ReadInstanceMember(saveData, "PreFinishedRoom");
            diagnostics["prefinished_room_found"] = preFinishedRoom is not null;
            stage = "setup_saved_single_player";
        }

        private void SetupSavedSinglePlayer()
        {
            if (runManager is null || runState is null || saveData is null)
            {
                throw new InvalidOperationException("런 설정에 필요한 객체가 준비되지 않았습니다.");
            }

            MethodInfo? setUpSavedSinglePlayer = FindCompatibleMethod(
                runManager.GetType(),
                "SetUpSavedSinglePlayer",
                new[] { runState.GetType(), saveData.GetType() });
            diagnostics["setup_saved_single_player_found"] = setUpSavedSinglePlayer is not null;
            if (setUpSavedSinglePlayer is null)
            {
                throw new MissingMethodException("RunManager.SetUpSavedSinglePlayer(runState, save)를 찾을 수 없습니다.");
            }

            setUpSavedSinglePlayer.Invoke(runManager, new[] { runState, saveData });
            diagnostics["setup_saved_single_player_success"] = true;
            diagnostics["run_manager_in_progress_after_setup"] =
                ReadInstanceMember(runManager, "IsInProgress") is bool inProgressAfterSetup && inProgressAfterSetup;
            object? runManagerNetService = ReadInstanceMember(runManager, "NetService");
            diagnostics["run_manager_net_service_found_after_setup"] = runManagerNetService is not null;
            diagnostics["run_manager_net_service_type"] = runManagerNetService?.GetType().FullName;
            stage = "initialize_networking";
        }

        private void InitializeNetworking()
        {
            if (nGame is null || runManager is null)
            {
                throw new InvalidOperationException("네트워크 초기화에 필요한 객체가 준비되지 않았습니다.");
            }

            object? reactionContainer = ReadInstanceMember(nGame, "ReactionContainer");
            object? runManagerNetService = ReadInstanceMember(runManager, "NetService");
            Type? netSingleplayerGameServiceType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.NetSingleplayerGameService");
            Type? oldNamespaceNetSingleplayerGameServiceType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.NetSingleplayerGameService");
            MethodInfo? initializeNetworking = reactionContainer is null
                ? null
                : FindMethod(reactionContainer.GetType(), "InitializeNetworking", 1);
            diagnostics["reaction_container_found"] = reactionContainer is not null;
            diagnostics["net_singleplayer_game_service_found"] = netSingleplayerGameServiceType is not null;
            diagnostics["net_singleplayer_game_service_old_namespace_found"] = oldNamespaceNetSingleplayerGameServiceType is not null;
            diagnostics["initialize_networking_found"] = initializeNetworking is not null;
            diagnostics["initialize_networking_signature"] = DescribeMethod(initializeNetworking);
            if (reactionContainer is not null && initializeNetworking is not null)
            {
                Type initializeNetServiceType = initializeNetworking.GetParameters()[0].ParameterType;
                object? netService = initializeNetServiceType.IsInstanceOfType(runManagerNetService)
                    ? runManagerNetService
                    : netSingleplayerGameServiceType is null
                        ? null
                        : Activator.CreateInstance(netSingleplayerGameServiceType);
                diagnostics["initialize_networking_arg_type"] = netService?.GetType().FullName;
                diagnostics["initialize_networking_used_run_manager_net_service"] = ReferenceEquals(netService, runManagerNetService);
                if (netService is null)
                {
                    throw new MissingMethodException("ReactionContainer.InitializeNetworking에 넘길 NetSingleplayerGameService를 찾을 수 없습니다.");
                }

                initializeNetworking.Invoke(reactionContainer, new[] { netService });
                diagnostics["initialize_networking_success"] = true;
            }
            else
            {
                diagnostics["initialize_networking_success"] = false;
            }

            stage = "preload_run_assets";
        }

        private void StartPreloadRunAssets()
        {
            Type? preloadManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Assets.PreloadManager");
            object? players = ReadInstanceMember(runState, "Players");
            MethodInfo? loadRunAssets = FindStaticMethod(preloadManagerType, "LoadRunAssets", 1);
            diagnostics["preload_manager_found"] = preloadManagerType is not null;
            diagnostics["run_state_players_found"] = players is not null;
            diagnostics["preload_run_assets_found"] = loadRunAssets is not null;
            diagnostics["preload_run_assets_signature"] = DescribeMethod(loadRunAssets);
            object? characterEnumerable = BuildCharacterEnumerable(players, loadRunAssets?.GetParameters()[0].ParameterType);
            diagnostics["preload_run_assets_arg_type"] = characterEnumerable?.GetType().FullName;
            StartTask(loadRunAssets, null, new[] { characterEnumerable }, "preload_run_assets", "PreloadManager.LoadRunAssets");
        }

        private void StartPreloadActAssets()
        {
            Type? preloadManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Assets.PreloadManager");
            object? act = ReadInstanceMember(runState, "Act");
            MethodInfo? loadActAssets = FindStaticMethod(preloadManagerType, "LoadActAssets", 1);
            diagnostics["run_state_act_found"] = act is not null;
            diagnostics["preload_act_assets_found"] = loadActAssets is not null;
            diagnostics["preload_act_assets_signature"] = DescribeMethod(loadActAssets);
            StartTask(loadActAssets, null, new[] { act }, "preload_act_assets", "PreloadManager.LoadActAssets");
        }

        private void LaunchRun()
        {
            if (runManager is null)
            {
                throw new InvalidOperationException("RunManager가 준비되지 않았습니다.");
            }

            MethodInfo? launch = FindMethod(runManager.GetType(), "Launch", 0);
            diagnostics["run_manager_launch_found"] = launch is not null;
            diagnostics["run_manager_launch_signature"] = DescribeMethod(launch);
            object? launchedState = launch?.Invoke(runManager, Array.Empty<object>());
            diagnostics["run_manager_launch_success"] = launchedState is not null;
            stage = "create_nrun_scene";
        }

        private void CreateNRunScene()
        {
            if (nGame is null || runState is null)
            {
                throw new InvalidOperationException("NRun 씬 생성에 필요한 객체가 준비되지 않았습니다.");
            }

            Type? nRunType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.NRun");
            MethodInfo? createRun = FindStaticMethod(nRunType, "Create", 1);
            diagnostics["n_run_type_found"] = nRunType is not null;
            diagnostics["n_run_create_found"] = createRun is not null;
            diagnostics["n_run_create_signature"] = DescribeMethod(createRun);
            runNode = createRun?.Invoke(null, new[] { runState });
            diagnostics["n_run_create_success"] = runNode is not null;
            if (runNode is null)
            {
                throw new InvalidOperationException("NRun.Create(runState)가 null을 반환했습니다.");
            }

            object? rootSceneContainer = ReadInstanceMember(nGame, "RootSceneContainer");
            MethodInfo? setCurrentScene = rootSceneContainer is null
                ? null
                : FindCompatibleMethod(rootSceneContainer.GetType(), "SetCurrentScene", new[] { runNode.GetType() });
            diagnostics["root_scene_container_found"] = rootSceneContainer is not null;
            diagnostics["set_current_scene_found"] = setCurrentScene is not null;
            diagnostics["set_current_scene_signature"] = DescribeMethod(setCurrentScene);
            if (setCurrentScene is null)
            {
                throw new MissingMethodException("RootSceneContainer.SetCurrentScene(runNode)를 찾을 수 없습니다.");
            }

            setCurrentScene.Invoke(rootSceneContainer, new[] { runNode });
            diagnostics["set_current_scene_success"] = true;
            readyWaitStartedAtMs = Environment.TickCount64;
            readyWaitCount = 0;
            stage = "wait_nrun_ready";
        }

        private bool WaitNRunReady()
        {
            PumpPendingContinuations();
            readyWaitCount++;
            long elapsedMs = Environment.TickCount64 - readyWaitStartedAtMs;
            object? nRun = ReadStaticMember(AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.NRun"), "Instance");
            bool nMapMemberFound = TryReadStaticMember(
                AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen"),
                "Instance",
                out object? nMapScreen,
                out Exception? nMapException);

            diagnostics["wait_nrun_ready_count"] = readyWaitCount;
            diagnostics["wait_nrun_ready_elapsed_ms"] = elapsedMs;
            diagnostics["n_run_instance_found"] = nRun is not null;
            diagnostics["n_map_screen_instance_member_found"] = nMapMemberFound;
            diagnostics["n_map_screen_instance_found"] = nMapScreen is not null;
            diagnostics["n_map_screen_instance_exception_type"] = nMapException?.GetType().Name;
            diagnostics["n_map_screen_instance_exception_message"] = nMapException?.Message;
            CaptureMapScreenDiagnostics(diagnostics, nMapScreen);

            MapScreenReadyState mapReadyState = GetMapScreenReadyState(nRun, nMapScreen);
            diagnostics["wait_nrun_ready_reason"] = mapReadyState.Reason;
            diagnostics["n_run_is_inside_tree"] = mapReadyState.NRunInsideTree;
            diagnostics["n_map_screen_is_inside_tree"] = mapReadyState.MapScreenInsideTree;
            diagnostics["n_map_screen_ready_for_generate_map"] = mapReadyState.IsReady;

            if (nRun is not null && nMapScreen is not null && mapReadyState.IsReady)
            {
                stage = "generate_map";
                WriteCommandResult(command, "running", "NRun과 지도 화면 준비를 확인했습니다.", diagnostics);
                return false;
            }

            if (elapsedMs > readyTimeoutMs)
            {
                Fail($"NRun 또는 지도 화면 준비를 {readyTimeoutMs}ms 안에 확인하지 못했습니다. reason={mapReadyState.Reason}");
                return true;
            }

            WriteCommandResult(command, "running", "NRun과 지도 화면 준비를 기다리는 중입니다.", diagnostics);
            return false;
        }

        private void StartGenerateMap()
        {
            if (runManager is null)
            {
                throw new InvalidOperationException("RunManager가 준비되지 않았습니다.");
            }

            MethodInfo? generateMap = FindMethod(runManager.GetType(), "GenerateMap", 0);
            diagnostics["generate_map_found"] = generateMap is not null;
            diagnostics["generate_map_signature"] = DescribeMethod(generateMap);
            diagnostics["generate_map_invoked_on_main_thread"] = IsGameMainThread();
            CaptureMapScreenDiagnostics(diagnostics);
            StartTask(generateMap, runManager, Array.Empty<object?>(), "generate_map", "RunManager.GenerateMap");
        }

        private void DeserializePrefinishedRoom()
        {
            Type? abstractRoomType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Rooms.AbstractRoom");
            MethodInfo? roomFromSerializable = FindStaticMethod(abstractRoomType, "FromSerializable", 2);
            diagnostics["abstract_room_type_found"] = abstractRoomType is not null;
            diagnostics["abstract_room_from_serializable_found"] = roomFromSerializable is not null;
            diagnostics["abstract_room_from_serializable_signature"] = DescribeMethod(roomFromSerializable);
            room = roomFromSerializable?.Invoke(null, new[] { preFinishedRoom, runState });
            diagnostics["abstract_room_from_serializable_result_type"] = room?.GetType().FullName;
            stage = "load_latest_map_coord";
        }

        private void StartLoadLatestMapCoord()
        {
            if (runManager is null)
            {
                throw new InvalidOperationException("RunManager가 준비되지 않았습니다.");
            }

            MethodInfo? loadIntoLatestMapCoord = FindMethod(runManager.GetType(), "LoadIntoLatestMapCoord", 1);
            diagnostics["load_into_latest_map_coord_found"] = loadIntoLatestMapCoord is not null;
            diagnostics["load_into_latest_map_coord_signature"] = DescribeMethod(loadIntoLatestMapCoord);
            diagnostics["load_latest_map_coord_invoked_on_main_thread"] = IsGameMainThread();
            StartTaskWithoutCustomSyncContext(loadIntoLatestMapCoord, runManager, new[] { room }, "load_latest_map_coord", "RunManager.LoadIntoLatestMapCoord");
        }

        private void LoadMapDrawings()
        {
            if (runManager is null)
            {
                throw new InvalidOperationException("RunManager가 준비되지 않았습니다.");
            }

            object? mapDrawingsToLoad = ReadInstanceMember(runManager, "MapDrawingsToLoad");
            diagnostics["map_drawings_to_load_found"] = mapDrawingsToLoad is not null;
            if (mapDrawingsToLoad is not null)
            {
                TryLoadMapDrawings(runManager, mapDrawingsToLoad, diagnostics);
            }

            exportReadyWaitStartedAtMs = Environment.TickCount64;
            exportReadyWaitCount = 0;
            stage = "wait_combat_export_ready";
        }

        private bool WaitCombatExportReady()
        {
            CaptureRetainedContinuationDiagnostics();
            exportReadyWaitCount++;
            long elapsedMs = Environment.TickCount64 - exportReadyWaitStartedAtMs;
            CombatStateExporter.CombatExportProbe fileProbe = CombatStateExporter.ReadLatestStateFileProbe("continue_run");
            string? latestFilePhase = CombatStateExporter.ReadLatestStateFilePhase("continue_run");
            bool exportPending = CombatStateExporter.HasPendingExport;
            bool combatInProgress = IsCombatInProgress();
            bool combatPlayPhase = IsCombatPlayPhase();
            bool stableCombatInProgress = ObserveStableCombatInProgress(combatInProgress);
            bool canForceExport = ShouldForceExportDuringReadyWait(stableCombatInProgress, elapsedMs);
            CombatStateExporter.CombatExportProbe probe = canForceExport
                ? CombatStateExporter.ForceExportFromCombatManager("continue_run")
                : new CombatStateExporter.CombatExportProbe(combatInProgress, false, false, false, false, null, null, 0, 0, "force_export_throttled");
            diagnostics["export_ready_wait_count"] = exportReadyWaitCount;
            diagnostics["export_ready_wait_elapsed_ms"] = elapsedMs;
            diagnostics["export_ready_timeout_ms"] = readyTimeoutMs;
            diagnostics["export_ready_force_export_requested"] = forceExportRequested;
            diagnostics["export_ready_force_export_attempted"] = canForceExport;
            diagnostics["export_ready_force_export_interval_ms"] = ExportReadyForceExportIntervalMs;
            diagnostics["export_ready_stable_combat_ms"] = ExportReadyStableCombatMs;
            diagnostics["export_ready_combat_in_progress_stable"] = stableCombatInProgress;
            diagnostics["export_ready_combat_play_phase"] = combatPlayPhase;
            diagnostics["export_ready_state_found"] = probe.StateFound;
            diagnostics["export_ready_has_player_vitals"] = probe.HasPlayerVitals;
            diagnostics["export_ready_has_hand_cards"] = probe.HasHandCards;
            diagnostics["export_ready_has_enemies"] = probe.HasEnemies;
            diagnostics["export_ready_player_hp"] = probe.PlayerHp;
            diagnostics["export_ready_player_energy"] = probe.PlayerEnergy;
            diagnostics["export_ready_hand_count"] = probe.HandCount;
            diagnostics["export_ready_enemy_count"] = probe.EnemyCount;
            diagnostics["export_ready_reason"] = probe.Reason;
            diagnostics["export_ready_is_stable"] = probe.IsStable;
            diagnostics["export_ready_file_state_found"] = fileProbe.StateFound;
            diagnostics["export_ready_file_has_player_vitals"] = fileProbe.HasPlayerVitals;
            diagnostics["export_ready_file_has_hand_cards"] = fileProbe.HasHandCards;
            diagnostics["export_ready_file_has_enemies"] = fileProbe.HasEnemies;
            diagnostics["export_ready_file_player_hp"] = fileProbe.PlayerHp;
            diagnostics["export_ready_file_player_energy"] = fileProbe.PlayerEnergy;
            diagnostics["export_ready_file_hand_count"] = fileProbe.HandCount;
            diagnostics["export_ready_file_enemy_count"] = fileProbe.EnemyCount;
            diagnostics["export_ready_file_reason"] = fileProbe.Reason;
            diagnostics["export_ready_file_is_stable"] = fileProbe.IsStable;
            diagnostics["export_ready_file_phase"] = latestFilePhase;
            diagnostics["export_ready_pending_export"] = exportPending;
            diagnostics["export_ready_pending_export_count"] = CombatStateExporter.PendingExportCount;
            diagnostics["export_ready_pending_export_age_ms"] = CombatStateExporter.PendingExportAgeMs;
            combatInProgress = combatInProgress || probe.IsInProgress;
            diagnostics["export_ready_combat_in_progress_for_file_fallback"] = combatInProgress;

            if (!exportPending && string.Equals(latestFilePhase, "map", StringComparison.OrdinalIgnoreCase))
            {
                stage = "done";
                diagnostics["export_ready_success_source"] = "latest_state_file";
                diagnostics["export_ready_success_phase"] = "map";
                ClearRetainedContinuationContext("export_ready_map");
                WriteCommandResult(command, "running", "지도 상태 파일이 안정된 것을 확인했습니다.", diagnostics);
                return false;
            }

            // 대기 단계에서는 파일 기반 판정을 우선합니다.
            // 강제 export는 전투 준비가 안정적으로 보일 때만 낮은 빈도로 보조 확인합니다.
            if (combatPlayPhase && !exportPending && (probe.IsStable || (combatInProgress && fileProbe.IsStable)))
            {
                stage = "done";
                diagnostics["export_ready_success_source"] = probe.IsStable ? "combat_manager_force_export" : "latest_state_file";
                diagnostics["export_ready_success_phase"] = "combat_turn";
                ClearRetainedContinuationContext("export_ready");
                WriteCommandResult(command, "running", "전투 상태 export 또는 최신 상태 파일이 안정된 것을 확인했습니다.", diagnostics);
                return false;
            }

            if (elapsedMs > readyTimeoutMs)
            {
                if (CanUseExportReadyGrace("ready_timeout"))
                {
                    WriteRunningResultIfDue("전투가 진행 중이므로 전투 상태 export 준비를 유예 시간 동안 더 확인합니다.");
                    return false;
                }

                diagnostics["export_ready_timed_out"] = true;
                diagnostics["timed_out"] = true;
                diagnostics["timeout_stage"] = stage;
                diagnostics["export_ready_failed_reason"] = "combat_export_not_ready";
                Logger.Warning("continue_run 이후 전투 상태 export 안정 대기 시간이 초과되었습니다.");
                ClearRetainedContinuationContext("export_ready_timeout");
                Fail("전투 상태 export 준비 시간이 초과되어 continue_run을 실패로 종료합니다.");
                return true;
            }

            if (!combatInProgress)
            {
                diagnostics["export_ready_waiting_reason"] = "combat_not_in_progress_yet";
                WriteRunningResultIfDue("게임 정상 흐름이 전투 상태를 만들 때까지 기다리는 중입니다.");
                return false;
            }

            if (!combatPlayPhase)
            {
                diagnostics["export_ready_waiting_reason"] = "combat_not_in_play_phase_yet";
                WriteRunningResultIfDue("전투가 플레이 입력 가능 단계로 들어가기를 기다리는 중입니다.");
                return false;
            }

            if (exportPending)
            {
                diagnostics["export_ready_waiting_reason"] = "pending_export_flush";
                WriteRunningResultIfDue("마지막 전투 상태 변경이 combat_state.json에 반영되기를 기다리는 중입니다.");
                return false;
            }

            diagnostics["export_ready_waiting_reason"] = "state_not_stable_yet";
            WriteRunningResultIfDue("전투 상태 export가 안정되기를 기다리는 중입니다.");
            return false;
        }

        private bool ShouldForceExportDuringReadyWait(bool stableCombatInProgress, long elapsedMs)
        {
            long nowMs = Environment.TickCount64;
            bool intervalPassed = nowMs - exportReadyLastForceExportAtMs >= ExportReadyForceExportIntervalMs;
            diagnostics["export_ready_force_export_next_allowed_in_ms"] = intervalPassed
                ? 0
                : ExportReadyForceExportIntervalMs - (nowMs - exportReadyLastForceExportAtMs);

            if (!intervalPassed)
            {
                return false;
            }

            bool allowed = forceExportRequested || stableCombatInProgress;
            diagnostics["export_ready_force_export_allowed_reason"] = forceExportRequested
                ? "explicit_param"
                : stableCombatInProgress
                    ? "stable_combat_in_progress"
                    : "not_allowed";
            if (!allowed)
            {
                return false;
            }

            exportReadyLastForceExportAtMs = nowMs;
            diagnostics["export_ready_last_force_export_elapsed_ms"] = elapsedMs;
            return true;
        }

        private bool ObserveStableCombatInProgress(bool combatInProgress)
        {
            long nowMs = Environment.TickCount64;
            if (!combatInProgress)
            {
                exportReadyCombatInProgressSinceMs = 0;
                diagnostics["export_ready_combat_in_progress_seen_ms"] = 0;
                return false;
            }

            if (exportReadyCombatInProgressSinceMs == 0)
            {
                exportReadyCombatInProgressSinceMs = nowMs;
            }

            long seenMs = nowMs - exportReadyCombatInProgressSinceMs;
            diagnostics["export_ready_combat_in_progress_seen_ms"] = seenMs;
            return seenMs >= ExportReadyStableCombatMs;
        }

        private bool CanUseExportReadyGrace(string reason)
        {
            if (stage != "wait_combat_export_ready")
            {
                return false;
            }

            bool runInProgress = IsRunInProgress();
            bool combatInProgress = IsCombatInProgress();
            diagnostics["is_in_progress"] = runInProgress;
            diagnostics["run_manager_is_in_progress"] = runInProgress;
            diagnostics["combat_in_progress"] = combatInProgress;
            if (!runInProgress && !combatInProgress)
            {
                return false;
            }

            long nowMs = Environment.TickCount64;
            if (!exportReadyGraceStarted)
            {
                exportReadyGraceStarted = true;
                exportReadyGraceStartedAtMs = nowMs;
                diagnostics["export_ready_grace_started"] = true;
                diagnostics["export_ready_grace_reason"] = reason;
                diagnostics["export_ready_grace_elapsed_ms"] = 0;
                diagnostics["export_ready_grace_ms"] = ExportReadyGraceMs;
                Logger.Info($"continue_run 전투 상태 export 준비 유예 시작: id={command.Id}, reason={reason}");
                return true;
            }

            long graceElapsedMs = nowMs - exportReadyGraceStartedAtMs;
            diagnostics["export_ready_grace_started"] = true;
            diagnostics["export_ready_grace_elapsed_ms"] = graceElapsedMs;
            diagnostics["export_ready_grace_ms"] = ExportReadyGraceMs;
            if (graceElapsedMs <= ExportReadyGraceMs)
            {
                return true;
            }

            diagnostics["export_ready_grace_expired"] = true;
            return false;
        }

        private static bool IsRunInProgress()
        {
            object? runManager = ReadStaticMember(AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager"), "Instance");
            object? value = ReadInstanceMember(runManager, "IsInProgress");
            return value is bool isInProgress && isInProgress;
        }

        private void StartTask(MethodInfo? method, object? instance, object?[] arguments, string taskStage, string label)
        {
            if (method is null)
            {
                throw new MissingMethodException($"{label} 메서드를 찾을 수 없습니다.");
            }

            MainThreadContinuationContext continuationContext = new();
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            object? result;
            try
            {
                SynchronizationContext.SetSynchronizationContext(continuationContext);
                diagnostics[$"{taskStage}_sync_context_installed"] = true;
                result = method.Invoke(instance, arguments);
                continuationContext.Pump(diagnostics, taskStage);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }

            if (result is Task task)
            {
                pendingTask = task;
                pendingTaskStage = taskStage;
                pendingContinuationContext = continuationContext;
                pendingTaskStartedAtMs = Environment.TickCount64;
                diagnostics[$"{taskStage}_task_started_at_ms"] = pendingTaskStartedAtMs;
                return;
            }

            diagnostics[$"{taskStage}_success"] = true;
            stage = NextStage(taskStage);
        }

        private void StartTaskWithoutCustomSyncContext(MethodInfo? method, object? instance, object?[] arguments, string taskStage, string label)
        {
            if (method is null)
            {
                throw new MissingMethodException($"{label} 메서드를 찾을 수 없습니다.");
            }

            SynchronizationContext? currentContext = SynchronizationContext.Current;
            diagnostics[$"{taskStage}_custom_sync_context_used"] = false;
            diagnostics[$"{taskStage}_sync_context_installed"] = false;
            diagnostics[$"{taskStage}_previous_sync_context_type"] = currentContext?.GetType().FullName;

            // 이 단계의 await continuation에서 Godot PackedScene.Instantiate가 실행될 수 있다.
            // Harmony Postfix 안에서 직접 Pump하지 않기 위해 custom context를 만들지 않는다.
            object? result = method.Invoke(instance, arguments);
            if (result is Task task)
            {
                pendingTask = task;
                pendingTaskStage = taskStage;
                pendingContinuationContext = null;
                pendingTaskStartedAtMs = Environment.TickCount64;
                diagnostics[$"{taskStage}_task_started_at_ms"] = pendingTaskStartedAtMs;
                diagnostics[$"{taskStage}_task_status"] = task.Status.ToString();
                diagnostics[$"{taskStage}_task_completed"] = task.IsCompleted;
                return;
            }

            diagnostics[$"{taskStage}_task_status"] = "no_task";
            diagnostics[$"{taskStage}_success"] = true;
            stage = NextStage(taskStage);
        }

        private void PumpPendingContinuations()
        {
            if (pendingContinuationContext is null || pendingTaskStage is null)
            {
                return;
            }

            SynchronizationContext? previousContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(pendingContinuationContext);
                pendingContinuationContext.Pump(diagnostics, pendingTaskStage);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }

        private void RetainContinuationContextForExportWait(string finishedStage, MainThreadContinuationContext? context)
        {
            if (finishedStage != "load_latest_map_coord" || context is null)
            {
                return;
            }

            // 보존 컨텍스트를 계속 실행하면 Godot 씬 생성이 Harmony Postfix 안에서 직접 실행될 수 있다.
            // 크래시 방지를 우선해 지금은 실행하지 않고, 다음 접근을 위한 진단 정보만 남긴다.
            retainedContinuationContext = null;
            retainedContinuationStage = null;
            diagnostics["retained_sync_context_active"] = false;
            diagnostics["retained_sync_context_disabled"] = true;
            diagnostics["retained_sync_context_source_stage"] = finishedStage;
            context.Capture(diagnostics, "retained_candidate");
        }

        private void CaptureRetainedContinuationDiagnostics()
        {
            if (retainedContinuationContext is null || retainedContinuationStage is null)
            {
                diagnostics["retained_sync_context_active"] = false;
                return;
            }

            retainedContinuationContext.Capture(diagnostics, "retained");
            diagnostics["retained_sync_context_active"] = false;
            diagnostics["retained_sync_context_disabled"] = true;
            diagnostics["retained_sync_context_source_stage"] = retainedContinuationStage;
        }

        private void ClearRetainedContinuationContext(string reason)
        {
            if (retainedContinuationContext is not null)
            {
                retainedContinuationContext.Capture(diagnostics, "retained");
            }

            diagnostics["retained_sync_context_active"] = false;
            if (!diagnostics.ContainsKey("retained_sync_context_clear_reason"))
            {
                diagnostics["retained_sync_context_clear_reason"] = reason;
            }

            retainedContinuationContext = null;
            retainedContinuationStage = null;
        }

        private void AdvanceAfterTask()
        {
            stage = NextStage((string)diagnostics["continue_stage"]!);
            diagnostics["continue_stage"] = stage;
        }

        private static string NextStage(string finishedStage)
        {
            return finishedStage switch
            {
                "preload_run_assets" => "preload_act_assets",
                "preload_act_assets" => "launch_run",
                "generate_map" => "deserialize_prefinished_room",
                "load_latest_map_coord" => "load_map_drawings",
                "load_map_drawings" => "wait_combat_export_ready",
                _ => "done"
            };
        }

        private static bool StageRequiresMainThread(string stageName)
        {
            return stageName is "defer_continue_button_click"
                or "generate_map"
                or "load_latest_map_coord"
                or "load_map_drawings"
                or "wait_combat_export_ready";
        }

        private static int GetStageTimeoutMs(string stageName)
        {
            return stageName switch
            {
                "load_latest_map_coord" => LoadLatestMapCoordTimeoutMs,
                _ => 0
            };
        }

        private static string? ReadJsonString(JsonElement root, string propertyName)
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(propertyName, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }

        private void CapturePendingTaskDiagnostics(string taskStage)
        {
            pendingContinuationContext?.Capture(diagnostics, taskStage);
            if (taskStage == "load_latest_map_coord")
            {
                CaptureCombatManagerDiagnostics(diagnostics);
            }
        }

        private bool Running(string message)
        {
            diagnostics["continue_stage"] = stage;
            RefreshContinueRunDiagnostics(diagnostics);
            WriteCommandResult(command, "running", message, diagnostics);
            return false;
        }

        private void RefreshContinueRunDiagnosticsIfDue(bool force = false)
        {
            long nowMs = Environment.TickCount64;
            if (!force && nowMs - lastWaitStageDiagnosticsAtMs < WaitStageDiagnosticsIntervalMs)
            {
                diagnostics["continue_diagnostics_throttled"] = true;
                diagnostics["continue_diagnostics_interval_ms"] = WaitStageDiagnosticsIntervalMs;
                return;
            }

            lastWaitStageDiagnosticsAtMs = nowMs;
            diagnostics["continue_diagnostics_throttled"] = false;
            diagnostics["continue_diagnostics_interval_ms"] = WaitStageDiagnosticsIntervalMs;
            RefreshContinueRunDiagnostics(diagnostics);
        }

        private void WriteRunningResultIfDue(string message, bool force = false)
        {
            long nowMs = Environment.TickCount64;
            if (!force && nowMs - lastWaitStageResultAtMs < WaitStageResultIntervalMs)
            {
                diagnostics["result_write_throttled"] = true;
                diagnostics["result_write_interval_ms"] = WaitStageResultIntervalMs;
                return;
            }

            lastWaitStageResultAtMs = nowMs;
            diagnostics["result_write_throttled"] = false;
            diagnostics["result_write_interval_ms"] = WaitStageResultIntervalMs;
            WriteCommandResult(command, "running", message, diagnostics);
        }

        private void Fail(string message)
        {
            ClearRetainedContinuationContext("command_failed");
            diagnostics["continue_stage"] = stage;
            RefreshContinueRunDiagnostics(diagnostics);
            WriteCommandResult(command, "failed", message, diagnostics);
            Logger.Warning($"continue_run 단계별 복원 실패: id={command.Id}, stage={stage}, {message}");
            Interlocked.Exchange(ref continueRunInFlight, 0);
        }

        private void Reject(string message)
        {
            ClearRetainedContinuationContext("command_rejected");
            diagnostics["continue_stage"] = stage;
            RefreshContinueRunDiagnostics(diagnostics);
            WriteCommandResult(command, "rejected", message, diagnostics);
            Logger.Warning($"continue_run 실행 거부: id={command.Id}, stage={stage}, {message}");
            Interlocked.Exchange(ref continueRunInFlight, 0);
        }

        private void RecordException(Exception exception)
        {
            diagnostics["exception_stage"] = stage;
            diagnostics["exception_type"] = exception.GetType().Name;
            diagnostics["exception_message"] = exception.Message;
            diagnostics["load_run_exception_stack"] = SummarizeStackTrace(exception);
            CaptureMapScreenDiagnostics(diagnostics);
        }
    }

    private sealed class MainThreadContinuationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> queue = new();
        private int postCount;
        private int pumpCount;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (queue)
            {
                queue.Enqueue((callback, state));
                postCount++;
            }
        }

        public override void Send(SendOrPostCallback callback, object? state)
        {
            callback(state);
        }

        public void Pump(Dictionary<string, object?> diagnostics, string stage)
        {
            int processed = 0;
            while (true)
            {
                SendOrPostCallback callback;
                object? state;
                lock (queue)
                {
                    if (queue.Count == 0)
                    {
                        break;
                    }

                    (callback, state) = queue.Dequeue();
                }

                callback(state);
                processed++;
            }

            if (processed > 0)
            {
                pumpCount++;
            }

            diagnostics[$"{stage}_sync_context_post_count"] = postCount;
            diagnostics[$"{stage}_sync_context_pump_count"] = pumpCount;
            diagnostics[$"{stage}_sync_context_last_processed"] = processed;
            diagnostics[$"{stage}_sync_context_remaining_count"] = QueueCount;
        }

        public void Capture(Dictionary<string, object?> diagnostics, string prefix)
        {
            diagnostics[$"{prefix}_sync_context_post_count"] = postCount;
            diagnostics[$"{prefix}_sync_context_pump_count"] = pumpCount;
            diagnostics[$"{prefix}_sync_context_remaining_count"] = QueueCount;
        }

        private int QueueCount
        {
            get
            {
                lock (queue)
                {
                    return queue.Count;
                }
            }
        }
    }

    private readonly record struct MapScreenReadyState(
        bool IsReady,
        string Reason,
        bool NRunInsideTree,
        bool MapScreenInsideTree);

    private sealed record AutotestCommand
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; init; } = string.Empty;

        [JsonPropertyName("params")]
        public Dictionary<string, JsonElement>? Params { get; init; }
    }

    private sealed record AutotestCommandResult(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("action")] string Action,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
        [property: JsonPropertyName("diagnostics")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        Dictionary<string, object?>? Diagnostics = null);
}
