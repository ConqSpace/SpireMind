using System.Collections;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace SpireMindMod;

internal static class CombatActionExecutor
{
    private const int ClaimCooldownMs = 500;
    private const int MaxExecutedHistory = 128;

    private static readonly SpireMindLogger Logger = new("SpireMind.R5.Execute");
    private static readonly object SyncRoot = new();
    private static readonly Queue<string> ExecutedOrder = new();
    private static readonly HashSet<string> ExecutedSubmissionIds = new(StringComparer.Ordinal);

    private static int claimInFlight;
    private static long lastClaimAttemptAtMs;
    private static PendingClaim? pendingClaim;

    public static void Tick()
    {
        TryExecutePendingClaim();
        TryStartClaimRequest();
    }

    private static void TryStartClaimRequest()
    {
        if (Volatile.Read(ref claimInFlight) == 1)
        {
            return;
        }

        long nowMs = Environment.TickCount64;
        if (nowMs - lastClaimAttemptAtMs < ClaimCooldownMs)
        {
            return;
        }

        CombatStateBridgePoster.PostedStateSnapshot? postedState = CombatStateBridgePoster.GetLatestPostedState();
        if (postedState is null)
        {
            return;
        }

        CombatActionContextSnapshot context = CombatActionRuntimeContext.GetSnapshot();
        if (context.CombatRoot is null || context.StateId != postedState.StateId)
        {
            return;
        }

        lastClaimAttemptAtMs = nowMs;
        Volatile.Write(ref claimInFlight, 1);

        _ = Task.Run(async () =>
        {
            try
            {
                using CancellationTokenSource cancellation = new(1500);
                ActionClaimResponse? response = await CombatActionBridgeClient.TryClaimAsync(
                    postedState,
                    cancellation.Token).ConfigureAwait(false);

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
                Volatile.Write(ref claimInFlight, 0);
            }
        });
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

        if (!legalAction.ActionType.Equals("end_turn", StringComparison.OrdinalIgnoreCase))
        {
            RememberExecuted(claim.Action.SubmissionId);
            ReportResult(claim, "unsupported", $"R5.1은 {legalAction.ActionType} 행동을 실행하지 않습니다.");
            return;
        }

        try
        {
            if (TryExecuteEndTurn(context.CombatRoot, out string detail))
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
            Type? localContextType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Context.LocalContext");
            object? player = localContextType is null
                ? null
                : AccessTools.Method(localContextType, "GetMe")?.Invoke(null, Array.Empty<object>());
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
