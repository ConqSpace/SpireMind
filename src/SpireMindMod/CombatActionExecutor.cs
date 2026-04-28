using System.Collections;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace SpireMindMod;

internal static class CombatActionExecutor
{
    private const int ClaimCooldownMs = 500;
    private const int BackgroundPollIntervalMs = 250;
    private const int DiagnosticLogIntervalMs = 5000;
    private const int ClaimInFlightWatchdogMs = 3000;
    private const int MaxExecutedHistory = 128;

    private static readonly SpireMindLogger Logger = new("SpireMind.R5.Execute");
    private static readonly object SyncRoot = new();
    private static readonly Queue<string> ExecutedOrder = new();
    private static readonly HashSet<string> ExecutedSubmissionIds = new(StringComparer.Ordinal);

    private static Timer? backgroundPollTimer;
    private static int claimInFlight;
    private static int backgroundPollingStarted;
    private static long lastClaimAttemptAtMs;
    private static long claimStartedAtMs;
    private static long lastDiagnosticLogAtMs;
    private static PendingClaim? pendingClaim;

    public static void StartBackgroundPolling()
    {
        if (Interlocked.Exchange(ref backgroundPollingStarted, 1) == 1)
        {
            return;
        }

        backgroundPollTimer = new Timer(
            _ => TickSafely(),
            null,
            BackgroundPollIntervalMs,
            BackgroundPollIntervalMs);

        Logger.Info("전투 액션 claim 백그라운드 확인을 시작했습니다.");
    }

    public static void StopBackgroundPolling()
    {
        if (Interlocked.Exchange(ref backgroundPollingStarted, 0) == 0)
        {
            return;
        }

        Timer? timer = Interlocked.Exchange(ref backgroundPollTimer, null);
        timer?.Dispose();
        Logger.Info("전투 액션 claim 백그라운드 확인을 중지했습니다.");
    }

    public static void Tick()
    {
        AutotestCommandChannel.Tick();
        TryStartClaimRequest();
    }

    public static void TickMainThread()
    {
        AutotestCommandChannel.TickMainThread();
        TryExecutePendingClaim();
        TryStartClaimRequest();
    }

    private static void TickSafely()
    {
        try
        {
            Tick();
        }
        catch (Exception exception)
        {
            Logger.Warning($"액션 확인 중 예외가 발생했습니다. 다음 주기에서 다시 시도합니다. {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void TryStartClaimRequest()
    {
        long nowMs = Environment.TickCount64;
        if (Volatile.Read(ref claimInFlight) == 1)
        {
            long startedAtMs = Interlocked.Read(ref claimStartedAtMs);
            if (startedAtMs > 0 && nowMs - startedAtMs > ClaimInFlightWatchdogMs)
            {
                Volatile.Write(ref claimInFlight, 0);
                Interlocked.Exchange(ref claimStartedAtMs, 0);
                Logger.Warning("이전 claim 요청이 너무 오래 끝나지 않아 대기 상태를 해제했습니다.");
            }

            return;
        }

        if (nowMs - lastClaimAttemptAtMs < ClaimCooldownMs)
        {
            return;
        }

        CombatStateBridgePoster.PostedStateSnapshot? postedState = CombatStateBridgePoster.GetLatestPostedState();
        if (postedState is null)
        {
            LogDiagnostic("claim 대기: 브리지에 성공적으로 게시된 전투 상태가 아직 없습니다.");
            return;
        }

        CombatActionContextSnapshot context = CombatActionRuntimeContext.GetSnapshot();
        if (context.CombatRoot is null || context.StateId != postedState.StateId)
        {
            if (context.CombatRoot is not null && !string.IsNullOrWhiteSpace(context.CombatStateJson))
            {
                CombatStateBridgePoster.ForcePost(context.CombatStateJson);
            }

            LogDiagnostic(
                $"claim 대기: 현재 전투 상태와 브리지 게시 상태가 다릅니다. 현재 상태를 다시 게시했습니다. current={context.StateId ?? "<none>"} posted={postedState.StateId}");
            return;
        }

        lastClaimAttemptAtMs = nowMs;
        Interlocked.Exchange(ref claimStartedAtMs, nowMs);
        Volatile.Write(ref claimInFlight, 1);

        _ = Task.Run(async () =>
        {
            try
            {
                using CancellationTokenSource cancellation = new(1500);
                ActionClaimResponse? response = await CombatActionBridgeClient.TryClaimAsync(
                    postedState,
                    cancellation.Token).ConfigureAwait(false);

                if (response is null)
                {
                    LogDiagnostic("claim 응답 없음: 브리지 설정이 비활성화되어 있거나 요청이 생략되었습니다.");
                }
                else if (response.Status == "none")
                {
                    LogDiagnostic($"claim 응답: 실행할 액션이 없습니다. state_version={postedState.StateVersion}");
                }

                if (response?.Status == "claimed" && response.Action is not null)
                {
                    lock (SyncRoot)
                    {
                        pendingClaim = new PendingClaim(response.Action, postedState);
                    }

                    Logger.Info($"행동 claim 성공: {response.Action.SelectedActionId} ({response.Action.SubmissionId})");
                }
                else if (response is not null && response.Status is "stale" or "unsupported")
                {
                    Logger.Info($"행동 claim이 실행 없이 종료되었습니다: {response.Status} {response.Reason}");
                }
            }
            catch (Exception exception)
            {
                Logger.Warning($"행동 claim 요청에 실패했습니다. 게임 진행은 멈추지 않습니다. {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref claimStartedAtMs, 0);
                Volatile.Write(ref claimInFlight, 0);
            }
        });
    }

    private static void LogDiagnostic(string message)
    {
        long nowMs = Environment.TickCount64;
        long previousMs = Interlocked.Read(ref lastDiagnosticLogAtMs);
        if (nowMs - previousMs < DiagnosticLogIntervalMs)
        {
            return;
        }

        Interlocked.Exchange(ref lastDiagnosticLogAtMs, nowMs);
        Logger.Info(message);
    }

    private static void TryExecutePendingClaim()
    {
        PendingClaim? claim;
        lock (SyncRoot)
        {
            claim = pendingClaim;
            pendingClaim = null;
        }

        if (claim is null)
        {
            return;
        }

        if (HasExecuted(claim.Action.SubmissionId))
        {
            ReportResult(claim, "ignored_duplicate", "이미 처리한 submission_id라 실행하지 않았습니다.");
            return;
        }

        CombatActionContextSnapshot context = CombatActionRuntimeContext.GetSnapshot();
        if (context.CombatRoot is null
            || context.StateId != claim.PostedState.StateId
            || claim.Action.StateId != claim.PostedState.StateId
            || claim.Action.StateVersion != claim.PostedState.StateVersion)
        {
            RememberExecuted(claim.Action.SubmissionId);
            ReportResult(claim, "stale", "실행 직전 상태가 claim된 상태와 다릅니다.");
            return;
        }

        LegalActionSnapshot? legalAction = context.FindAction(claim.Action.SelectedActionId);
        if (legalAction is null)
        {
            RememberExecuted(claim.Action.SubmissionId);
            ReportResult(claim, "stale", "현재 legal_actions에서 selected_action_id를 찾지 못했습니다.");
            return;
        }

        if (!legalAction.ActionType.Equals("end_turn", StringComparison.OrdinalIgnoreCase)
            && !legalAction.ActionType.Equals("play_card", StringComparison.OrdinalIgnoreCase))
        {
            RememberExecuted(claim.Action.SubmissionId);
            ReportResult(claim, "unsupported", $"{legalAction.ActionType} 행동은 아직 실행기가 지원하지 않습니다.");
            return;
        }

        try
        {
            string detail;
            bool applied = legalAction.ActionType.Equals("end_turn", StringComparison.OrdinalIgnoreCase)
                ? TryExecuteEndTurn(context.CombatRoot, out detail)
                : TryExecutePlayCard(legalAction, context.CombatRoot, out detail);

            if (applied)
            {
                RememberExecuted(claim.Action.SubmissionId);
                ReportResult(claim, "applied", detail);
                return;
            }

            RememberExecuted(claim.Action.SubmissionId);
            ReportResult(claim, "failed", detail);
        }
        catch (Exception exception)
        {
            RememberExecuted(claim.Action.SubmissionId);
            ReportResult(claim, "failed", $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static bool TryExecutePlayCard(LegalActionSnapshot legalAction, object combatRoot, out string detail)
    {
        if (legalAction.CombatCardId is null)
        {
            detail = "play_card 행동에 combat_card_id가 없습니다.";
            return false;
        }

        if (!TryGetCombatCard((uint)legalAction.CombatCardId.Value, out object? card, out detail))
        {
            return false;
        }

        if (card is null)
        {
            detail = $"NetCombatCardDb가 null 카드를 반환했습니다. combat_card_id={legalAction.CombatCardId.Value}";
            return false;
        }

        if (!IsCardInHand(card))
        {
            detail = $"카드가 현재 손패에 없습니다. combat_card_id={legalAction.CombatCardId.Value}";
            return false;
        }

        if (!TryResolveCardTarget(legalAction, card, combatRoot, out object? target, out detail))
        {
            return false;
        }

        if (TryCheckCanPlayTargeting(card, target, out bool canPlay) && !canPlay)
        {
            detail = $"카드를 현재 대상에 사용할 수 없습니다. combat_card_id={legalAction.CombatCardId.Value}, target_id={legalAction.TargetId ?? "<none>"}";
            return false;
        }

        if (!TryInvokeTryManualPlay(card, target, out bool enqueued, out detail))
        {
            return false;
        }

        if (!enqueued)
        {
            detail = $"TryManualPlay가 false를 반환했습니다. combat_card_id={legalAction.CombatCardId.Value}, target_id={legalAction.TargetId ?? "<none>"}";
            return false;
        }

        string cardName = ReadNamedMember(card, "Title")?.ToString()
            ?? ReadNamedMember(card, "Id")?.ToString()
            ?? $"combat_card_{legalAction.CombatCardId.Value}";
        string targetText = target is null
            ? "대상 없음"
            : ReadNamedMember(target, "LogName")?.ToString()
                ?? ReadNamedMember(target, "Name")?.ToString()
                ?? legalAction.TargetId
                ?? "대상";
        detail = $"PlayCardAction 입력 성공: card={cardName}, combat_card_id={legalAction.CombatCardId.Value}, target={targetText}";
        Logger.Info(detail);
        return true;
    }

    private static bool TryGetCombatCard(uint combatCardId, out object? card, out string detail)
    {
        card = null;
        Type? databaseType = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.Multiplayer.NetCombatCardDb");
        if (databaseType is null)
        {
            detail = "NetCombatCardDb 타입을 찾지 못했습니다.";
            return false;
        }

        object? database = ReadStaticNamedMember(databaseType, "Instance");
        if (database is null)
        {
            detail = "NetCombatCardDb.Instance를 찾지 못했습니다.";
            return false;
        }

        MethodInfo? tryGetCard = databaseType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
            {
                if (!method.Name.Equals("TryGetCard", StringComparison.Ordinal))
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 2
                    && parameters[0].ParameterType == typeof(uint);
            });
        if (tryGetCard is null)
        {
            detail = "NetCombatCardDb.TryGetCard(uint, out CardModel)를 찾지 못했습니다.";
            return false;
        }

        object?[] args = { combatCardId, null };
        try
        {
            object? result = tryGetCard.Invoke(database, args);
            if (result is true && args[1] is not null)
            {
                card = args[1];
                detail = "카드를 찾았습니다.";
                return true;
            }
        }
        catch (Exception exception)
        {
            detail = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }

        detail = $"NetCombatCardDb에서 combat_card_id={combatCardId} 카드를 찾지 못했습니다.";
        return false;
    }

    private static bool IsCardInHand(object? card)
    {
        object? pile = ReadNamedMember(card, "Pile");
        object? pileType = ReadNamedMember(pile, "Type");
        return pileType?.ToString()?.Contains("Hand", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool TryResolveCardTarget(
        LegalActionSnapshot legalAction,
        object card,
        object combatRoot,
        out object? target,
        out string detail)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(legalAction.TargetId))
        {
            detail = "대상이 필요 없는 카드입니다.";
            return true;
        }

        object? combatState = FindCombatState(card)
            ?? FindCombatState(combatRoot)
            ?? FindCombatState(CombatStateExporter.GetLatestRuntimePlayer());
        if (combatState is null)
        {
            detail = "카드 대상을 찾기 위한 CombatState를 찾지 못했습니다.";
            return false;
        }

        if (legalAction.TargetCombatId is not null)
        {
            target = FindCreatureByCombatId(combatState, legalAction.TargetCombatId.Value);
            if (target is not null)
            {
                detail = $"target_combat_id={legalAction.TargetCombatId.Value} 대상을 찾았습니다.";
                return true;
            }
        }

        if (TryParseEnemyIndex(legalAction.TargetId, out int enemyIndex))
        {
            target = EnumerateEnemies(combatState).Skip(enemyIndex).FirstOrDefault();
            if (target is not null)
            {
                detail = $"target_id={legalAction.TargetId} 순서 대상을 찾았습니다.";
                return true;
            }
        }

        detail = $"대상을 찾지 못했습니다. target_id={legalAction.TargetId}, target_combat_id={legalAction.TargetCombatId?.ToString() ?? "<none>"}";
        return false;
    }

    private static object? FindCombatState(object? source)
    {
        if (source is null)
        {
            return null;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        if (typeName.Equals("MegaCrit.Sts2.Core.Combat.CombatState", StringComparison.Ordinal)
            || typeName.EndsWith(".CombatState", StringComparison.Ordinal))
        {
            return source;
        }

        object? direct = ReadNamedMember(source, "CombatState");
        if (direct is not null)
        {
            return direct;
        }

        object? owner = ReadNamedMember(source, "Owner");
        object? creature = ReadNamedMember(owner, "Creature")
            ?? ReadNamedMember(source, "Creature");
        return ReadNamedMember(creature, "CombatState");
    }

    private static object? FindCreatureByCombatId(object combatState, int combatId)
    {
        foreach (object creature in EnumerateCreatures(combatState))
        {
            int? candidateId = ReadInt(creature, "CombatId");
            if (candidateId == combatId)
            {
                return creature;
            }
        }

        return null;
    }

    private static IEnumerable<object> EnumerateCreatures(object combatState)
    {
        return ExpandValue(ReadNamedMember(combatState, "Creatures"))
            .Where(value => !IsScalar(value.GetType()));
    }

    private static IEnumerable<object> EnumerateEnemies(object combatState)
    {
        return ExpandValue(ReadNamedMember(combatState, "Enemies"))
            .Where(value => !IsScalar(value.GetType()));
    }

    private static bool TryParseEnemyIndex(string? targetId, out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(targetId)
            || !targetId.StartsWith("enemy_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(targetId["enemy_".Length..], out index) && index >= 0;
    }

    private static bool TryCheckCanPlayTargeting(object card, object? target, out bool canPlay)
    {
        canPlay = false;
        MethodInfo? method = FindSingleArgumentMethod(card.GetType(), "CanPlayTargeting", target);
        if (method is null)
        {
            return false;
        }

        try
        {
            object? result = method.Invoke(card, new[] { target });
            if (result is bool boolean)
            {
                canPlay = boolean;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryInvokeTryManualPlay(object card, object? target, out bool enqueued, out string detail)
    {
        enqueued = false;
        MethodInfo? method = FindSingleArgumentMethod(card.GetType(), "TryManualPlay", target);
        if (method is null)
        {
            detail = "CardModel.TryManualPlay(Creature?)를 찾지 못했습니다.";
            return false;
        }

        try
        {
            object? result = method.Invoke(card, new[] { target });
            if (result is bool boolean)
            {
                enqueued = boolean;
                detail = $"TryManualPlay 반환값: {boolean}";
                return true;
            }

            detail = $"TryManualPlay 반환 타입이 bool이 아닙니다: {DescribeResult(result)}";
            return false;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            detail = $"{exception.InnerException.GetType().Name}: {exception.InnerException.Message}";
            return false;
        }
        catch (Exception exception)
        {
            detail = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static MethodInfo? FindSingleArgumentMethod(Type type, string methodName, object? argument)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return type.GetMethods(flags)
            .Where(method => method.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
            .Where(method => method.GetParameters().Length == 1)
            .FirstOrDefault(method => IsArgumentCompatible(method.GetParameters()[0].ParameterType, argument));
    }

    private static bool IsArgumentCompatible(Type parameterType, object? argument)
    {
        if (argument is null)
        {
            return !parameterType.IsValueType || Nullable.GetUnderlyingType(parameterType) is not null;
        }

        return parameterType.IsAssignableFrom(argument.GetType());
    }

    private static bool TryExecuteEndTurn(object combatRoot, out string detail)
    {
        if (TryEnqueueEndPlayerTurnAction(out detail))
        {
            return true;
        }

        Logger.Warning($"EndPlayerTurnAction 큐 입력에 실패했습니다. UI 핸들러 후보를 탐색합니다. {detail}");

        string[] candidateMethodNames =
        {
            "ReadyButtonPressed",
            "OnReadyButtonPressed",
            "OnEndTurnButtonPressed",
            "EndPlayerTurn",
            "EndPlayerTurnInternal",
            "EndPlayerTurnPhaseTwoInternal",
            "SwitchFromPlayerToEnemySide",
            "AfterAllPlayersReadyToBeginEnemyTurn",
            "EndTurn",
            "FinishTurn"
        };

        foreach (object candidate in EnumerateCandidateObjects(combatRoot))
        {
            Type type = candidate.GetType();
            foreach (string methodName in candidateMethodNames)
            {
                MethodInfo? method = FindNoArgMethod(type, methodName);
                if (method is null)
                {
                    continue;
                }

                object? result = method.Invoke(candidate, Array.Empty<object>());
                detail = $"턴 종료 후보 메서드 호출: {type.FullName}.{method.Name} 반환={DescribeResult(result)}";
                Logger.Info(detail);
                return true;
            }
        }

        detail = "좌표 클릭 없이 호출할 수 있는 턴 종료 후보 메서드를 찾지 못했습니다.";
        Logger.Warning(detail);
        return false;
    }

    private static bool TryEnqueueEndPlayerTurnAction(out string detail)
    {
        try
        {
            object? player = CombatStateExporter.GetLatestRuntimePlayer();
            if (player is null)
            {
                Type? localContextType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Context.LocalContext");
                player = localContextType is null
                    ? null
                    : InvokeStaticNoArgMethod(localContextType, "GetMe");
            }
            if (player is null)
            {
                detail = "LocalContext.GetMe()에서 플레이어를 찾지 못했습니다.";
                return false;
            }

            if (IsPlayerReadyToEndTurn(player))
            {
                detail = "플레이어가 이미 턴 종료 준비 상태입니다. 되돌리기 행동은 넣지 않습니다.";
                return true;
            }

            object? creature = ReadNamedMember(player, "Creature");
            object? combatState = ReadNamedMember(creature, "CombatState");
            int? roundNumber = ReadInt(combatState, "RoundNumber");
            if (roundNumber is null)
            {
                detail = "현재 전투 라운드 번호를 찾지 못했습니다.";
                return false;
            }

            Type? actionType = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            if (actionType is null)
            {
                detail = "EndPlayerTurnAction 타입을 찾지 못했습니다.";
                return false;
            }

            object? action = Activator.CreateInstance(actionType, player, roundNumber.Value);
            if (action is null)
            {
                detail = "EndPlayerTurnAction 인스턴스 생성에 실패했습니다.";
                return false;
            }

            Type? runManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager");
            object? runManager = ReadStaticNamedMember(runManagerType, "Instance");
            object? synchronizer = ReadNamedMember(runManager, "ActionQueueSynchronizer");
            if (synchronizer is null)
            {
                detail = "ActionQueueSynchronizer를 찾지 못했습니다.";
                return false;
            }

            MethodInfo? requestEnqueue = synchronizer.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name.Equals("RequestEnqueue", StringComparison.OrdinalIgnoreCase)
                    && method.GetParameters().Length == 1);
            if (requestEnqueue is null)
            {
                detail = "ActionQueueSynchronizer.RequestEnqueue 메서드를 찾지 못했습니다.";
                return false;
            }

            requestEnqueue.Invoke(synchronizer, new[] { action });
            detail = $"EndPlayerTurnAction 큐 입력 성공: round={roundNumber.Value}";
            Logger.Info(detail);
            return true;
        }
        catch (Exception exception)
        {
            detail = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static bool IsPlayerReadyToEndTurn(object player)
    {
        try
        {
            Type? combatManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Combat.CombatManager");
            object? combatManager = ReadStaticNamedMember(combatManagerType, "Instance");
            if (combatManager is null)
            {
                return false;
            }

            MethodInfo? method = combatManager.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => candidate.Name.Equals("IsPlayerReadyToEndTurn", StringComparison.OrdinalIgnoreCase)
                    && candidate.GetParameters().Length == 1);
            if (method is null)
            {
                return false;
            }

            return method.Invoke(combatManager, new[] { player }) is bool ready && ready;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<object> EnumerateCandidateObjects(object root)
    {
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        Queue<(object Value, int Depth)> queue = new();
        queue.Enqueue((root, 0));
        visited.Add(root);

        while (queue.Count > 0 && visited.Count <= 80)
        {
            (object value, int depth) = queue.Dequeue();
            yield return value;

            if (depth >= 2)
            {
                continue;
            }

            foreach (object child in ReadInterestingChildren(value))
            {
                if (visited.Contains(child))
                {
                    continue;
                }

                visited.Add(child);
                queue.Enqueue((child, depth + 1));
            }
        }
    }

    private static IEnumerable<object> ReadInterestingChildren(object source)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (MemberInfo member in source.GetType().GetMembers(flags))
        {
            if (member is not FieldInfo and not PropertyInfo)
            {
                continue;
            }

            if (member is PropertyInfo property && property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            string name = member.Name;
            if (!ContainsAny(name, "button", "turn", "combat", "room", "action", "input", "controller"))
            {
                continue;
            }

            object? value = ReadMember(source, member);
            foreach (object item in ExpandValue(value))
            {
                Type type = item.GetType();
                if (!IsScalar(type))
                {
                    yield return item;
                }
            }
        }
    }

    private static IEnumerable<object> ExpandValue(object? value)
    {
        if (value is null || value is string)
        {
            yield break;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (object? item in enumerable)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }
            yield break;
        }

        yield return value;
    }

    private static MethodInfo? FindNoArgMethod(Type type, string methodName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return type.GetMethods(flags)
            .Where(method => method.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
            .Where(method => method.GetParameters().Length == 0)
            .FirstOrDefault(method => method.ReturnType == typeof(void)
                || method.ReturnType == typeof(bool)
                || typeof(Task).IsAssignableFrom(method.ReturnType));
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

    private static object? ReadNamedMember(object? source, string memberName)
    {
        if (source is null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MemberInfo? member = source.GetType()
            .GetMember(memberName, flags)
            .FirstOrDefault(candidate => candidate is FieldInfo or PropertyInfo);
        return member is null ? null : ReadMember(source, member);
    }

    private static object? ReadStaticNamedMember(Type? type, string memberName)
    {
        if (type is null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        MemberInfo? member = type
            .GetMember(memberName, flags)
            .FirstOrDefault(candidate => candidate is FieldInfo or PropertyInfo);
        if (member is null)
        {
            return null;
        }

        try
        {
            return member switch
            {
                FieldInfo field => field.GetValue(null),
                PropertyInfo property when property.GetMethod is not null => property.GetValue(null),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? InvokeStaticNoArgMethod(Type type, string methodName)
    {
        MethodInfo? method = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(candidate => candidate.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => candidate.GetParameters().Length == 0)
            .OrderBy(candidate => candidate.ReturnType == typeof(void))
            .FirstOrDefault();
        if (method is null)
        {
            return null;
        }

        try
        {
            return method.Invoke(null, Array.Empty<object>());
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadInt(object? source, string memberName)
    {
        object? value = ReadNamedMember(source, memberName);
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

    private static bool ContainsAny(string text, params string[] parts)
    {
        return parts.Any(part => text.Contains(part, StringComparison.OrdinalIgnoreCase));
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

    private static string DescribeResult(object? result)
    {
        return result switch
        {
            null => "null",
            Task => "Task",
            bool boolean => boolean.ToString(),
            _ => result.GetType().Name
        };
    }

    private static bool HasExecuted(string submissionId)
    {
        lock (SyncRoot)
        {
            return ExecutedSubmissionIds.Contains(submissionId);
        }
    }

    private static void RememberExecuted(string submissionId)
    {
        lock (SyncRoot)
        {
            if (!ExecutedSubmissionIds.Add(submissionId))
            {
                return;
            }

            ExecutedOrder.Enqueue(submissionId);
            while (ExecutedOrder.Count > MaxExecutedHistory)
            {
                string oldSubmissionId = ExecutedOrder.Dequeue();
                ExecutedSubmissionIds.Remove(oldSubmissionId);
            }
        }
    }

    private static void ReportResult(PendingClaim claim, string result, string note)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using CancellationTokenSource cancellation = new(1500);
                await CombatActionBridgeClient.ReportResultAsync(
                    claim.Action,
                    result,
                    claim.PostedState,
                    note,
                    cancellation.Token).ConfigureAwait(false);
                Logger.Info($"행동 실행 결과 보고: {result} ({claim.Action.SubmissionId})");
            }
            catch (Exception exception)
            {
                Logger.Warning($"행동 실행 결과 보고에 실패했습니다. {exception.GetType().Name}: {exception.Message}");
            }
        });
    }

    private sealed record PendingClaim(
        ClaimedAction Action,
        CombatStateBridgePoster.PostedStateSnapshot PostedState);
}
