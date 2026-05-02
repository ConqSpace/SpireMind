using System.Collections;
using System.Diagnostics;
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
    private const int RewardCardSelectionTimeoutMs = 5000;
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
    private static PendingRewardCardSelection? pendingRewardCardSelection;

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
        TryExecutePendingRewardCardSelection();
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

        if (!IsSupportedActionType(legalAction.ActionType))
        {
            RememberExecuted(claim.Action.SubmissionId);
            ReportResult(claim, "unsupported", $"{legalAction.ActionType} 행동은 아직 실행기가 지원하지 않습니다.");
            return;
        }

        try
        {
            string detail;
            bool applied;
            if (legalAction.ActionType.Equals("end_turn", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteEndTurn(context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("play_card", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecutePlayCard(legalAction, context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("use_potion", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteUsePotion(legalAction, context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("choose_event_option", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteEventOption(legalAction, context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("choose_rest_site_option", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteRestSiteOption(legalAction, context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("proceed_rest_site", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteRestSiteProceed(context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("proceed_shop", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteShopProceed(context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("open_treasure_chest", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteOpenTreasureChest(context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("claim_treasure_relic", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteClaimTreasureRelic(legalAction, context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("proceed_treasure", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteTreasureProceed(context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("buy_shop_item", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteBuyShopItem(legalAction, context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("remove_card_at_shop", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryOpenShopCardRemoval(legalAction, context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("choose_card_selection", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteCardSelectionChoice(legalAction, context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("confirm_card_selection", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteCardSelectionConfirm(context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("cancel_card_selection", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteCardSelectionCancel(context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("choose_map_node", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteMapNodeSelection(legalAction, context.CombatRoot, out detail);
            }
            else
            {
                applied = TryExecuteRewardAction(legalAction, context.CombatRoot, claim, out detail, out bool resultDeferred);
                if (applied && resultDeferred)
                {
                    Logger.Info(detail);
                    return;
                }
            }

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

    private static bool IsSupportedActionType(string actionType)
    {
        return actionType.Equals("end_turn", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("play_card", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("use_potion", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("claim_gold_reward", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("claim_relic_reward", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("claim_potion_reward", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("choose_card_reward", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("skip_card_reward", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("proceed_reward_screen", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("choose_map_node", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("choose_event_option", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("choose_rest_site_option", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("proceed_rest_site", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("proceed_shop", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("open_treasure_chest", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("claim_treasure_relic", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("proceed_treasure", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("buy_shop_item", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("remove_card_at_shop", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("choose_card_selection", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("confirm_card_selection", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("cancel_card_selection", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExecuteEventOption(
        LegalActionSnapshot legalAction,
        object eventRoot,
        out string detail)
    {
        object? eventRoom = ResolveEventRoom(eventRoot);
        if (eventRoom is null)
        {
            string rootTypeName = eventRoot.GetType().FullName ?? eventRoot.GetType().Name;
            detail = $"Event option cannot run on this screen. root={rootTypeName}";
            return false;
        }

        if (legalAction.EventOptionIndex is null)
        {
            detail = "choose_event_option requires event_option_index.";
            return false;
        }

        object? optionButton = FindEventOptionButton(eventRoom, legalAction.EventOptionIndex.Value);
        if (optionButton is null)
        {
            detail = $"Event option button was not found. event_option_id={legalAction.EventOptionId ?? "<none>"}, index={legalAction.EventOptionIndex.Value}";
            return false;
        }

        object? option = ReadNamedMember(optionButton, "Option");
        if (ReadNamedMember(option, "IsLocked") is bool isLocked && isLocked)
        {
            detail = $"Event option is locked. event_option_id={legalAction.EventOptionId ?? "<none>"}, index={legalAction.EventOptionIndex.Value}";
            return false;
        }

        if (!TryInvokeMethod(optionButton, "OnRelease", out _))
        {
            detail = $"Event option button release failed. event_option_id={legalAction.EventOptionId ?? "<none>"}, index={legalAction.EventOptionIndex.Value}";
            return false;
        }

        detail = $"Event option selected. event_option_id={legalAction.EventOptionId ?? "<none>"}, index={legalAction.EventOptionIndex.Value}";
        Logger.Info(detail);
        return true;
    }

    private static bool TryExecuteRestSiteOption(
        LegalActionSnapshot legalAction,
        object restSiteRoot,
        out string detail)
    {
        object? restSiteRoom = ResolveRestSiteRoom(restSiteRoot);
        if (restSiteRoom is null)
        {
            string rootTypeName = restSiteRoot.GetType().FullName ?? restSiteRoot.GetType().Name;
            detail = $"모닥불 선택지는 현재 화면에서 실행할 수 없습니다. root={rootTypeName}";
            return false;
        }

        if (legalAction.RestOptionIndex is null)
        {
            detail = "choose_rest_site_option에는 rest_option_index가 필요합니다.";
            return false;
        }

        object? optionButton = FindRestSiteOptionButton(restSiteRoom, legalAction.RestOptionIndex.Value);
        if (optionButton is null)
        {
            detail = $"모닥불 선택 버튼을 찾지 못했습니다. rest_option_id={legalAction.RestOptionId ?? "<none>"}, index={legalAction.RestOptionIndex.Value}";
            return false;
        }

        object? option = ReadNamedMember(optionButton, "Option");
        if (ReadNamedMember(option, "IsEnabled") is bool isEnabled && !isEnabled)
        {
            detail = $"모닥불 선택지가 비활성 상태입니다. rest_option_id={legalAction.RestOptionId ?? "<none>"}, index={legalAction.RestOptionIndex.Value}";
            return false;
        }

        if (!TryInvokeMethod(optionButton, "OnRelease", out _))
        {
            detail = $"모닥불 선택 버튼 호출에 실패했습니다. rest_option_id={legalAction.RestOptionId ?? "<none>"}, index={legalAction.RestOptionIndex.Value}";
            return false;
        }

        detail = $"모닥불 선택지를 골랐습니다. rest_option_id={legalAction.RestOptionId ?? "<none>"}, index={legalAction.RestOptionIndex.Value}";
        Logger.Info(detail);
        return true;
    }

    private static bool TryExecuteRestSiteProceed(object restSiteRoot, out string detail)
    {
        object? restSiteRoom = ResolveRestSiteRoom(restSiteRoot);
        if (restSiteRoom is null)
        {
            string rootTypeName = restSiteRoot.GetType().FullName ?? restSiteRoot.GetType().Name;
            detail = $"모닥불 진행 버튼은 현재 화면에서 실행할 수 없습니다. root={rootTypeName}";
            return false;
        }

        object? proceedButton = ReadNamedMember(restSiteRoom, "ProceedButton")
            ?? ReadNamedMember(restSiteRoom, "_proceedButton");
        if (proceedButton is null)
        {
            detail = "모닥불 진행 버튼을 찾지 못했습니다.";
            return false;
        }

        if (ReadNamedMember(proceedButton, "IsEnabled") is bool isEnabled && !isEnabled)
        {
            detail = "모닥불 진행 버튼이 아직 비활성 상태입니다.";
            return false;
        }

        if (!TryInvokeMethod(restSiteRoom, "OnProceedButtonReleased", out _, proceedButton))
        {
            detail = "모닥불 진행 버튼 호출에 실패했습니다.";
            return false;
        }

        detail = "모닥불 진행 버튼을 눌렀습니다.";
        Logger.Info(detail);
        return true;
    }

    private static bool TryExecuteShopProceed(object shopRoot, out string detail)
    {
        object? shopRoom = ResolveShopRoom(shopRoot);
        if (shopRoom is null)
        {
            string rootTypeName = shopRoot.GetType().FullName ?? shopRoot.GetType().Name;
            detail = $"상점 진행 버튼은 현재 화면에서 실행할 수 없습니다. root={rootTypeName}";
            return false;
        }

        object? proceedButton = ReadNamedMember(shopRoom, "ProceedButton")
            ?? ReadNamedMember(shopRoom, "_proceedButton")
            ?? ReadNamedMember(shopRoom, "ContinueButton")
            ?? ReadNamedMember(shopRoom, "_continueButton");
        if (proceedButton is null)
        {
            detail = "상점 진행 버튼을 찾지 못했습니다.";
            return false;
        }

        if (ReadNamedMember(proceedButton, "IsEnabled") is bool isEnabled && !isEnabled)
        {
            detail = "상점 진행 버튼이 아직 비활성 상태입니다.";
            return false;
        }

        if (TryCallDeferredNoArgs(proceedButton, "ForceClick", out string deferredFailureReason))
        {
            if (WaitForShopProceedTransitionToMap())
            {
                detail = "상점 진행 버튼을 눌렀고 지도 화면 진입을 확인했습니다. method=button.CallDeferred(ForceClick)";
                Logger.Info(detail);
                return true;
            }
        }

        List<(string Label, object? Source, string MethodName, object?[] Args)> candidates = new()
        {
            ("button.ForceClick", proceedButton, "ForceClick", Array.Empty<object?>()),
            ("button.OnRelease", proceedButton, "OnRelease", Array.Empty<object?>()),
            ("button.OnPressed", proceedButton, "OnPressed", Array.Empty<object?>()),
            ("room.OnProceedButtonReleased(button)", shopRoom, "OnProceedButtonReleased", new object?[] { proceedButton }),
            ("room.OnProceedPressed(button)", shopRoom, "OnProceedPressed", new object?[] { proceedButton }),
            ("room.Proceed", shopRoom, "Proceed", Array.Empty<object?>()),
            ("button.EmitSignalPressed(button)", proceedButton, "EmitSignalPressed", new object?[] { proceedButton })
        };

        List<string> invokedCandidates = new();
        foreach ((string label, object? source, string methodName, object?[] args) in candidates)
        {
            if (!TryInvokeMethod(source, methodName, out _, args))
            {
                continue;
            }

            invokedCandidates.Add(label);
            if (WaitForShopProceedTransitionToMap())
            {
                detail = $"상점 진행 버튼을 눌렀고 지도 화면 진입을 확인했습니다. method={label}";
                Logger.Info(detail);
                return true;
            }
        }

        string currentScreenTypeName = GetCurrentScreenTypeName();
        if (deferredFailureReason.Length > 0)
        {
            invokedCandidates.Insert(0, $"button.CallDeferred(ForceClick): {deferredFailureReason}");
        }
        else
        {
            invokedCandidates.Insert(0, "button.CallDeferred(ForceClick)");
        }

        string tried = invokedCandidates.Count == 0 ? "<none>" : string.Join(", ", invokedCandidates);
        detail = $"상점 진행 버튼 호출 뒤에도 지도 화면에 도달하지 못했습니다. tried={tried}, current_screen={currentScreenTypeName}";
        return false;
    }

    private static bool TryExecuteOpenTreasureChest(object treasureRoot, out string detail)
    {
        object? treasureRoom = ResolveTreasureRoom(treasureRoot);
        if (treasureRoom is null)
        {
            string rootTypeName = treasureRoot.GetType().FullName ?? treasureRoot.GetType().Name;
            detail = $"보물상자 열기는 현재 화면에서 실행할 수 없습니다. root={rootTypeName}";
            return false;
        }

        if (IsTreasureChestOpened(treasureRoom))
        {
            detail = "보물상자는 이미 열린 상태입니다.";
            return true;
        }

        object? chestButton = FindTreasureChestButton(treasureRoom);
        if (chestButton is null)
        {
            detail = "보물상자 Chest 버튼을 찾지 못했습니다.";
            return false;
        }

        if (ReadNamedMember(chestButton, "IsEnabled") is bool isEnabled && !isEnabled)
        {
            detail = "보물상자 Chest 버튼이 아직 비활성 상태입니다.";
            return false;
        }

        List<string> invokedCandidates = new();
        if (TryCallDeferredNoArgs(chestButton, "ForceClick", out string deferredFailureReason))
        {
            if (WaitForTreasureChestOpen(treasureRoom))
            {
                detail = "보물상자를 열고 보상 UI 진입을 확인했습니다. method=button.CallDeferred(ForceClick)";
                Logger.Info(detail);
                return true;
            }

            invokedCandidates.Add("button.CallDeferred(ForceClick)");
        }
        else if (deferredFailureReason.Length > 0)
        {
            invokedCandidates.Add($"button.CallDeferred(ForceClick): {deferredFailureReason}");
        }

        List<(string Label, object? Source, string MethodName, object?[] Args)> candidates = new()
        {
            ("room.OnChestButtonReleased(button)", treasureRoom, "OnChestButtonReleased", new object?[] { chestButton }),
            ("button.ForceClick", chestButton, "ForceClick", Array.Empty<object?>()),
            ("button.OnRelease", chestButton, "OnRelease", Array.Empty<object?>()),
            ("button.OnPressed", chestButton, "OnPressed", Array.Empty<object?>()),
            ("button.EmitSignalPressed(button)", chestButton, "EmitSignalPressed", new object?[] { chestButton })
        };

        foreach ((string label, object? source, string methodName, object?[] args) in candidates)
        {
            if (!TryInvokeMethod(source, methodName, out _, args))
            {
                continue;
            }

            invokedCandidates.Add(label);
            if (WaitForTreasureChestOpen(treasureRoom))
            {
                detail = $"보물상자를 열고 보상 UI 진입을 확인했습니다. method={label}";
                Logger.Info(detail);
                return true;
            }
        }

        string currentScreenTypeName = GetCurrentScreenTypeName();
        string tried = invokedCandidates.Count == 0 ? "<none>" : string.Join(", ", invokedCandidates);
        detail = $"보물상자 버튼 호출 뒤 보상 UI 진입을 확인하지 못했습니다. tried={tried}, current_screen={currentScreenTypeName}, opened={IsTreasureChestOpened(treasureRoom)}";
        return false;
    }

    private static bool TryExecuteClaimTreasureRelic(
        LegalActionSnapshot legalAction,
        object treasureRoot,
        out string detail)
    {
        object? treasureRoom = ResolveTreasureRoom(treasureRoot);
        if (treasureRoom is null)
        {
            string rootTypeName = treasureRoot.GetType().FullName ?? treasureRoot.GetType().Name;
            detail = $"보물방 유물 획득은 현재 화면에서 실행할 수 없습니다. root={rootTypeName}";
            return false;
        }

        if (!IsTreasureRelicCollectionOpen(treasureRoom))
        {
            detail = "보물방 유물 선택 UI가 아직 열려 있지 않습니다.";
            return false;
        }

        if (legalAction.TreasureRelicIndex is null)
        {
            detail = "claim_treasure_relic에는 treasure_relic_index가 필요합니다.";
            return false;
        }

        object? relicCollection = FindTreasureRelicCollection(treasureRoom);
        if (relicCollection is null)
        {
            detail = "보물방 유물 컬렉션 노드를 찾지 못했습니다.";
            return false;
        }

        object? holder = FindTreasureRelicHolder(relicCollection, legalAction.TreasureRelicIndex.Value);
        if (holder is null)
        {
            detail = $"보물방 유물 홀더를 찾지 못했습니다. index={legalAction.TreasureRelicIndex.Value}, relic_id={legalAction.TreasureRelicId ?? "<none>"}";
            return false;
        }

        if (ReadNamedMember(holder, "IsEnabled") is bool isEnabled && !isEnabled)
        {
            detail = $"보물방 유물 홀더가 비활성 상태입니다. index={legalAction.TreasureRelicIndex.Value}";
            return false;
        }

        List<string> invokedCandidates = new();
        List<(string Label, object? Source, string MethodName, object?[] Args)> candidates = new()
        {
            ("collection.PickRelic(holder)", relicCollection, "PickRelic", new object?[] { holder }),
            ("holder.ForceClick", holder, "ForceClick", Array.Empty<object?>()),
            ("holder.OnRelease", holder, "OnRelease", Array.Empty<object?>()),
            ("holder.OnPressed", holder, "OnPressed", Array.Empty<object?>())
        };

        foreach ((string label, object? source, string methodName, object?[] args) in candidates)
        {
            if (!TryInvokeMethod(source, methodName, out _, args))
            {
                continue;
            }

            invokedCandidates.Add(label);
            if (WaitForTreasureRelicClaim(treasureRoom))
            {
                detail = $"보물방 유물을 획득했고 진행 버튼 활성화를 확인했습니다. method={label}, index={legalAction.TreasureRelicIndex.Value}, relic_id={legalAction.TreasureRelicId ?? "<none>"}";
                Logger.Info(detail);
                return true;
            }
        }

        string tried = invokedCandidates.Count == 0 ? "<none>" : string.Join(", ", invokedCandidates);
        detail = $"보물방 유물 선택 호출 뒤 완료 상태를 확인하지 못했습니다. tried={tried}, index={legalAction.TreasureRelicIndex.Value}, relic_open={IsTreasureRelicCollectionOpen(treasureRoom)}, proceed_enabled={IsTreasureProceedEnabled(treasureRoom)}";
        return false;
    }

    private static bool TryExecuteTreasureProceed(object treasureRoot, out string detail)
    {
        object? treasureRoom = ResolveTreasureRoom(treasureRoot);
        if (treasureRoom is null)
        {
            string rootTypeName = treasureRoot.GetType().FullName ?? treasureRoot.GetType().Name;
            detail = $"보물방 진행은 현재 화면에서 실행할 수 없습니다. root={rootTypeName}";
            return false;
        }

        object? proceedButton = ReadNamedMember(treasureRoom, "ProceedButton")
            ?? ReadNamedMember(treasureRoom, "_proceedButton")
            ?? ReadNamedMember(treasureRoom, "proceedButton");
        if (proceedButton is null)
        {
            detail = "보물방 진행 버튼을 찾지 못했습니다.";
            return false;
        }

        if (ReadNamedMember(proceedButton, "IsEnabled") is bool isEnabled && !isEnabled)
        {
            detail = "보물방 진행 버튼이 아직 비활성 상태입니다.";
            return false;
        }

        List<string> invokedCandidates = new();
        if (TryCallDeferredNoArgs(proceedButton, "ForceClick", out string deferredFailureReason))
        {
            if (WaitForTreasureProceedTransitionToMap())
            {
                detail = "보물방 진행 버튼을 눌렀고 지도 화면 진입을 확인했습니다. method=button.CallDeferred(ForceClick)";
                Logger.Info(detail);
                return true;
            }

            invokedCandidates.Add("button.CallDeferred(ForceClick)");
        }
        else if (deferredFailureReason.Length > 0)
        {
            invokedCandidates.Add($"button.CallDeferred(ForceClick): {deferredFailureReason}");
        }

        List<(string Label, object? Source, string MethodName, object?[] Args)> candidates = new()
        {
            ("room.OnProceedButtonPressed(button)", treasureRoom, "OnProceedButtonPressed", new object?[] { proceedButton }),
            ("room.OnProceedButtonReleased(button)", treasureRoom, "OnProceedButtonReleased", new object?[] { proceedButton }),
            ("button.ForceClick", proceedButton, "ForceClick", Array.Empty<object?>()),
            ("button.OnRelease", proceedButton, "OnRelease", Array.Empty<object?>()),
            ("button.OnPressed", proceedButton, "OnPressed", Array.Empty<object?>())
        };

        foreach ((string label, object? source, string methodName, object?[] args) in candidates)
        {
            if (!TryInvokeMethod(source, methodName, out _, args))
            {
                continue;
            }

            invokedCandidates.Add(label);
            if (WaitForTreasureProceedTransitionToMap())
            {
                detail = $"보물방 진행 버튼을 눌렀고 지도 화면 진입을 확인했습니다. method={label}";
                Logger.Info(detail);
                return true;
            }
        }

        string currentScreenTypeName = GetCurrentScreenTypeName();
        string tried = invokedCandidates.Count == 0 ? "<none>" : string.Join(", ", invokedCandidates);
        detail = $"보물방 진행 버튼 호출 뒤 지도 화면 진입을 확인하지 못했습니다. tried={tried}, current_screen={currentScreenTypeName}";
        return false;
    }

    private static bool TryExecuteBuyShopItem(
        LegalActionSnapshot legalAction,
        object shopRoot,
        out string detail)
    {
        object? shopRoom = ResolveShopRoom(shopRoot);
        if (shopRoom is null)
        {
            string rootTypeName = shopRoot.GetType().FullName ?? shopRoot.GetType().Name;
            detail = $"상점 구매는 현재 화면에서 실행할 수 없습니다. root={rootTypeName}";
            return false;
        }

        object? runtimeInventory = ResolveRuntimeMerchantInventory(shopRoom)
            ?? ResolveRuntimeMerchantInventoryFromRunManager();
        if (runtimeInventory is null)
        {
            detail = "상점 구매에 필요한 MerchantInventory를 찾지 못했습니다.";
            return false;
        }

        ShopPurchaseCandidate? candidate = FindShopPurchaseCandidate(runtimeInventory, legalAction, out string findDetail);
        if (candidate is null)
        {
            detail = findDetail;
            return false;
        }

        if (!candidate.IsStocked)
        {
            detail = $"상점 물품이 이미 품절입니다. model_id={candidate.ModelId}, cost={candidate.Cost?.ToString() ?? "<unknown>"}";
            return false;
        }

        if (!candidate.IsAffordable)
        {
            detail = $"상점 물품을 살 골드가 부족합니다. model_id={candidate.ModelId}, cost={candidate.Cost?.ToString() ?? "<unknown>"}";
            return false;
        }

        if (!TryInvokePurchaseWrapper(candidate.Entry, runtimeInventory, waitForCompletion: true, out object? purchaseResult, out string purchaseDetail))
        {
            detail = purchaseDetail;
            return false;
        }

        detail = $"상점 구매 완료: kind={candidate.Kind}, model_id={candidate.ModelId}, cost={candidate.Cost?.ToString() ?? "<unknown>"} result={DescribeResult(purchaseResult)}";
        Logger.Info(detail);
        return true;
    }

    private static bool TryOpenShopCardRemoval(
        LegalActionSnapshot legalAction,
        object shopRoot,
        out string detail)
    {
        object? shopRoom = ResolveShopRoom(shopRoot);
        if (shopRoom is null)
        {
            string rootTypeName = shopRoot.GetType().FullName ?? shopRoot.GetType().Name;
            detail = $"카드 제거 서비스는 현재 화면에서 실행할 수 없습니다. root={rootTypeName}";
            return false;
        }

        object? runtimeInventory = ResolveRuntimeMerchantInventory(shopRoom)
            ?? ResolveRuntimeMerchantInventoryFromRunManager();
        if (runtimeInventory is null)
        {
            detail = "카드 제거 서비스에 필요한 MerchantInventory를 찾지 못했습니다.";
            return false;
        }

        ShopPurchaseCandidate? candidate = FindShopCardRemovalCandidate(runtimeInventory, legalAction, out string findDetail);
        if (candidate is null)
        {
            detail = findDetail;
            return false;
        }

        if (!candidate.IsStocked)
        {
            detail = $"카드 제거 서비스가 이미 사용됐거나 비활성화됐습니다. cost={candidate.Cost?.ToString() ?? "<unknown>"}";
            return false;
        }

        if (!candidate.IsAffordable)
        {
            detail = $"카드 제거 서비스를 이용할 골드가 부족합니다. cost={candidate.Cost?.ToString() ?? "<unknown>"}";
            return false;
        }

        if (!TryInvokePurchaseWrapper(candidate.Entry, runtimeInventory, waitForCompletion: false, out object? purchaseResult, out string purchaseDetail))
        {
            detail = purchaseDetail;
            return false;
        }

        detail = $"카드 제거 서비스 선택 완료: cost={candidate.Cost?.ToString() ?? "<unknown>"} result={DescribeResult(purchaseResult)}. 다음 상태에서 제거할 카드를 choose_card_selection으로 선택해야 합니다.";
        Logger.Info(detail);
        return true;
    }

    private static object? ResolveRuntimeMerchantInventory(object shopRoom)
    {
        object? directInventory = ReadNamedMember(shopRoom, "Inventory")
            ?? ReadNamedMember(shopRoom, "_inventory")
            ?? ReadNamedMember(shopRoom, "MerchantInventory")
            ?? ReadNamedMember(shopRoom, "_merchantInventory");
        object? runtimeInventory = NormalizeRuntimeMerchantInventory(directInventory);
        if (runtimeInventory is not null)
        {
            return runtimeInventory;
        }

        return EnumerateNodeDescendants(shopRoom)
            .Select(NormalizeRuntimeMerchantInventory)
            .FirstOrDefault(candidate => candidate is not null);
    }

    private static object? ResolveRuntimeMerchantInventoryFromRunManager()
    {
        Type? runManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager");
        object? runManager = ReadStaticNamedMember(runManagerType, "Instance");
        object? currentRoom = ReadNamedMember(runManager, "CurrentRoom")
            ?? ReadNamedMember(runManager, "_currentRoom")
            ?? ReadNamedMember(ReadNamedMember(runManager, "Run"), "CurrentRoom")
            ?? ReadNamedMember(ReadNamedMember(runManager, "CurrentRun"), "CurrentRoom");
        object? roomInventory = ReadNamedMember(currentRoom, "Inventory")
            ?? ReadNamedMember(currentRoom, "_inventory")
            ?? ReadNamedMember(currentRoom, "MerchantInventory")
            ?? ReadNamedMember(currentRoom, "_merchantInventory");
        return NormalizeRuntimeMerchantInventory(roomInventory);
    }

    private static object? NormalizeRuntimeMerchantInventory(object? source)
    {
        if (source is null)
        {
            return null;
        }

        if (HasShopEntryMembers(source))
        {
            return source;
        }

        object? nestedInventory = ReadNamedMember(source, "Inventory")
            ?? ReadNamedMember(source, "_inventory");
        if (!ReferenceEquals(nestedInventory, source) && HasShopEntryMembers(nestedInventory))
        {
            return nestedInventory;
        }

        return null;
    }

    private static bool HasShopEntryMembers(object? source)
    {
        return source is not null
            && (ReadNamedMember(source, "CharacterCardEntries") is not null
                || ReadNamedMember(source, "ColorlessCardEntries") is not null
                || ReadNamedMember(source, "RelicEntries") is not null
                || ReadNamedMember(source, "PotionEntries") is not null);
    }

    private static ShopPurchaseCandidate? FindShopPurchaseCandidate(
        object runtimeInventory,
        LegalActionSnapshot legalAction,
        out string detail)
    {
        List<ShopPurchaseCandidate> candidates = EnumerateShopPurchaseCandidates(runtimeInventory).ToList();
        IEnumerable<ShopPurchaseCandidate> matches = candidates;

        if (!string.IsNullOrWhiteSpace(legalAction.ShopKind))
        {
            matches = matches.Where(candidate => candidate.Kind.Equals(legalAction.ShopKind, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(legalAction.ShopModelId))
        {
            matches = matches.Where(candidate => IsSameShopModelId(candidate.ModelId, legalAction.ShopModelId));
        }

        if (legalAction.ShopCost is not null)
        {
            matches = matches.Where(candidate => candidate.Cost == legalAction.ShopCost.Value);
        }

        List<ShopPurchaseCandidate> exactMatches = matches.ToList();
        if (exactMatches.Count == 0)
        {
            string candidateSummary = string.Join(", ", candidates.Take(8).Select(candidate =>
                $"{candidate.Kind}:{candidate.ModelId}:{candidate.Cost?.ToString() ?? "<null>"}:{candidate.SlotIndex}"));
            detail = $"상점 구매 후보를 찾지 못했습니다. kind={legalAction.ShopKind ?? "<none>"}, model_id={legalAction.ShopModelId ?? "<none>"}, cost={legalAction.ShopCost?.ToString() ?? "<none>"}, candidates=[{candidateSummary}]";
            return null;
        }

        if (exactMatches.Count == 1)
        {
            detail = "상점 구매 후보를 찾았습니다.";
            return exactMatches[0];
        }

        if (legalAction.ShopSlotIndex is not null)
        {
            ShopPurchaseCandidate? slotMatch = exactMatches.FirstOrDefault(candidate => candidate.SlotIndex == legalAction.ShopSlotIndex.Value);
            if (slotMatch is not null)
            {
                detail = "상점 구매 후보를 슬롯 번호로 확정했습니다.";
                return slotMatch;
            }
        }

        detail = $"상점 구매 후보가 여러 개입니다. model_id={legalAction.ShopModelId ?? "<none>"}, count={exactMatches.Count}";
        return null;
    }

    private static IEnumerable<ShopPurchaseCandidate> EnumerateShopPurchaseCandidates(object runtimeInventory)
    {
        int cardIndex = 0;
        List<ShopPurchaseCandidate> characterCards = EnumerateCardPurchaseCandidates(runtimeInventory, "CharacterCardEntries", "character_card", cardIndex).ToList();
        foreach (ShopPurchaseCandidate candidate in characterCards)
        {
            yield return candidate;
        }
        cardIndex += characterCards.Count;

        foreach (ShopPurchaseCandidate candidate in EnumerateCardPurchaseCandidates(runtimeInventory, "ColorlessCardEntries", "colorless_card", cardIndex))
        {
            yield return candidate;
        }

        foreach (ShopPurchaseCandidate candidate in EnumerateModelPurchaseCandidates(runtimeInventory, "RelicEntries", "relic"))
        {
            yield return candidate;
        }

        foreach (ShopPurchaseCandidate candidate in EnumerateModelPurchaseCandidates(runtimeInventory, "PotionEntries", "potion"))
        {
            yield return candidate;
        }
    }

    private static ShopPurchaseCandidate? FindShopCardRemovalCandidate(
        object runtimeInventory,
        LegalActionSnapshot legalAction,
        out string detail)
    {
        object? entry = ReadNamedMember(runtimeInventory, "CardRemovalEntry")
            ?? ReadNamedMember(runtimeInventory, "cardRemovalEntry")
            ?? ReadNamedMember(runtimeInventory, "_cardRemovalEntry");
        if (entry is null)
        {
            detail = "상점 카드 제거 엔트리를 찾지 못했습니다.";
            return null;
        }

        ShopPurchaseCandidate candidate = BuildShopPurchaseCandidate(
            entry,
            kind: "service",
            modelId: "CARD_REMOVAL",
            slotGroup: "service",
            slotIndex: 0,
            rawSlotGroup: "service",
            rawSlotIndex: 0);

        if (legalAction.ShopCost is not null && candidate.Cost != legalAction.ShopCost.Value)
        {
            detail = $"카드 제거 서비스 가격이 현재 상태와 다릅니다. expected={legalAction.ShopCost.Value}, actual={candidate.Cost?.ToString() ?? "<unknown>"}";
            return null;
        }

        detail = "상점 카드 제거 엔트리를 찾았습니다.";
        return candidate;
    }

    private static IEnumerable<ShopPurchaseCandidate> EnumerateCardPurchaseCandidates(
        object runtimeInventory,
        string entriesMemberName,
        string rawSlotGroup,
        int startSlotIndex)
    {
        object? entries = ReadNamedMember(runtimeInventory, entriesMemberName)
            ?? ReadNamedMember(runtimeInventory, "_" + entriesMemberName)
            ?? ReadNamedMember(runtimeInventory, ToCamelCase(entriesMemberName));
        int rawIndex = 0;
        int visibleIndex = 0;
        foreach (object entry in ExpandValue(entries))
        {
            object? creationResult = ReadNamedMember(entry, "CreationResult")
                ?? ReadNamedMember(entry, "creationResult")
                ?? ReadNamedMember(entry, "_creationResult");
            object? card = ReadNamedMember(creationResult, "Card")
                ?? ReadNamedMember(creationResult, "card")
                ?? ReadNamedMember(creationResult, "_card");
            string? modelId = ReadObjectId(card);
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                yield return BuildShopPurchaseCandidate(
                    entry,
                    kind: "card",
                    modelId: modelId,
                    slotGroup: "card",
                    slotIndex: startSlotIndex + visibleIndex,
                    rawSlotGroup: rawSlotGroup,
                    rawSlotIndex: rawIndex);
                visibleIndex++;
            }

            rawIndex++;
        }
    }

    private static IEnumerable<ShopPurchaseCandidate> EnumerateModelPurchaseCandidates(
        object runtimeInventory,
        string entriesMemberName,
        string kind)
    {
        object? entries = ReadNamedMember(runtimeInventory, entriesMemberName)
            ?? ReadNamedMember(runtimeInventory, "_" + entriesMemberName)
            ?? ReadNamedMember(runtimeInventory, ToCamelCase(entriesMemberName));
        int index = 0;
        foreach (object entry in ExpandValue(entries))
        {
            object? model = ReadNamedMember(entry, "Model")
                ?? ReadNamedMember(entry, "model")
                ?? ReadNamedMember(entry, "_model");
            string? modelId = ReadObjectId(model);
            if (!string.IsNullOrWhiteSpace(modelId))
            {
                yield return BuildShopPurchaseCandidate(
                    entry,
                    kind,
                    modelId,
                    slotGroup: kind,
                    slotIndex: index,
                    rawSlotGroup: kind,
                    rawSlotIndex: index);
            }

            index++;
        }
    }

    private static ShopPurchaseCandidate BuildShopPurchaseCandidate(
        object entry,
        string kind,
        string modelId,
        string slotGroup,
        int slotIndex,
        string rawSlotGroup,
        int rawSlotIndex)
    {
        int? cost = ReadShopCost(entry);
        bool stocked = ReadBool(entry, "IsStocked") ?? ReadBool(entry, "isStocked") ?? ReadBool(entry, "_isStocked") ?? true;
        bool soldOut = ReadBool(entry, "SoldOut") ?? ReadBool(entry, "soldOut") ?? ReadBool(entry, "_soldOut") ?? false;
        bool affordable = ReadBool(entry, "EnoughGold")
            ?? ReadBool(entry, "enoughGold")
            ?? ReadBool(entry, "_enoughGold")
            ?? ReadBool(entry, "CanAfford")
            ?? ReadBool(entry, "canAfford")
            ?? true;

        return new ShopPurchaseCandidate(
            entry,
            kind,
            modelId,
            cost,
            slotGroup,
            slotIndex,
            rawSlotGroup,
            rawSlotIndex,
            stocked && !soldOut,
            affordable);
    }

    private static int? ReadShopCost(object? entry)
    {
        return ReadInt(entry, "price")
            ?? ReadInt(entry, "_price")
            ?? ReadInt(entry, "Price")
            ?? ReadInt(entry, "cost")
            ?? ReadInt(entry, "_cost")
            ?? ReadInt(entry, "Cost")
            ?? ReadInt(entry, "goldCost")
            ?? ReadInt(entry, "_goldCost")
            ?? ReadInt(entry, "GoldCost")
            ?? ReadInt(entry, "shopPrice")
            ?? ReadInt(entry, "_shopPrice")
            ?? ReadInt(entry, "ShopPrice");
    }

    private static bool TryInvokePurchaseWrapper(
        object entry,
        object runtimeInventory,
        bool waitForCompletion,
        out object? result,
        out string detail)
    {
        result = null;
        if (!TryInvokePurchaseMethod(entry, runtimeInventory, out object? invocationResult))
        {
            detail = $"상점 구매 메서드 호출에 실패했습니다. entry={entry.GetType().FullName ?? entry.GetType().Name}, methods=[{DescribePurchaseMethods(entry)}]";
            return false;
        }

        result = invocationResult;
        if (!waitForCompletion)
        {
            detail = "상점 구매/서비스 메서드를 호출했고 비동기 완료 대기는 생략했습니다.";
            return true;
        }

        if (invocationResult is Task<bool> booleanTask)
        {
            result = booleanTask.GetAwaiter().GetResult();
            if (result is bool ok && !ok)
            {
                detail = "상점 구매 메서드가 false를 반환했습니다.";
                return false;
            }
        }
        else if (invocationResult is Task task)
        {
            task.GetAwaiter().GetResult();
            result = task;
        }
        else if (invocationResult is bool ok && !ok)
        {
            detail = "상점 구매 메서드가 false를 반환했습니다.";
            return false;
        }

        detail = "상점 구매 메서드를 호출했습니다.";
        return true;
    }

    private static bool TryInvokePurchaseMethod(object entry, object runtimeInventory, out object? result)
    {
        if (TryInvokeMethod(entry, "OnTryPurchaseWrapper", out result, runtimeInventory, false)
            || TryInvokeMethod(entry, "OnTryPurchase", out result, runtimeInventory, false)
            || TryInvokeMethod(entry, "TryPurchase", out result, runtimeInventory, false)
            || TryInvokeMethod(entry, "Purchase", out result, runtimeInventory, false)
            || TryInvokeMethod(entry, "OnTryPurchaseWrapper", out result, runtimeInventory)
            || TryInvokeMethod(entry, "OnTryPurchase", out result, runtimeInventory)
            || TryInvokeMethod(entry, "TryPurchase", out result, runtimeInventory)
            || TryInvokeMethod(entry, "Purchase", out result, runtimeInventory)
            || TryInvokeMethod(entry, "OnTryPurchaseWrapper", out result)
            || TryInvokeMethod(entry, "OnTryPurchase", out result)
            || TryInvokeMethod(entry, "TryPurchase", out result)
            || TryInvokeMethod(entry, "Purchase", out result))
        {
            return true;
        }

        return false;
    }

    private static string DescribePurchaseMethods(object entry)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return string.Join("; ", entry.GetType()
            .GetMethods(flags)
            .Where(method => method.Name.Contains("Purchase", StringComparison.OrdinalIgnoreCase))
            .Select(method =>
            {
                string parameters = string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.Name));
                return $"{method.Name}({parameters}):{method.ReturnType.Name}";
            })
            .Take(12));
    }

    private static string? ReadObjectId(object? source)
    {
        object? id = ReadNamedMember(source, "Id")
            ?? ReadNamedMember(source, "id")
            ?? ReadNamedMember(source, "_id");
        if (id is null)
        {
            return null;
        }

        object? entry = ReadNamedMember(id, "Entry")
            ?? ReadNamedMember(id, "entry")
            ?? ReadNamedMember(id, "_entry");
        return entry?.ToString() ?? id.ToString();
    }

    private static bool IsSameShopModelId(string left, string right)
    {
        return left.Equals(right, StringComparison.OrdinalIgnoreCase)
            || StripShopModelPrefix(left).Equals(StripShopModelPrefix(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string StripShopModelPrefix(string value)
    {
        int dotIndex = value.LastIndexOf('.');
        return dotIndex >= 0 && dotIndex + 1 < value.Length
            ? value[(dotIndex + 1)..]
            : value;
    }

    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    private sealed record ShopPurchaseCandidate(
        object Entry,
        string Kind,
        string ModelId,
        int? Cost,
        string SlotGroup,
        int SlotIndex,
        string RawSlotGroup,
        int RawSlotIndex,
        bool IsStocked,
        bool IsAffordable);

    private static bool TryExecuteCardSelectionChoice(
        LegalActionSnapshot legalAction,
        object currentRoot,
        out string detail)
    {
        object? selectionScreen = ResolveCardSelectionScreen(currentRoot);
        if (selectionScreen is null)
        {
            string rootTypeName = currentRoot.GetType().FullName ?? currentRoot.GetType().Name;
            detail = $"카드 선택 화면을 찾지 못했습니다. root={rootTypeName}";
            return false;
        }

        if (legalAction.CardSelectionIndex is null)
        {
            detail = "choose_card_selection에는 card_selection_index가 필요합니다.";
            return false;
        }

        List<object> cardHolders = FindGridCardHolders(selectionScreen);
        int selectionIndex = legalAction.CardSelectionIndex.Value;
        if (selectionIndex < 0 || selectionIndex >= cardHolders.Count)
        {
            detail = $"카드 선택 인덱스가 화면의 카드 수를 벗어났습니다. index={selectionIndex}, count={cardHolders.Count}";
            return false;
        }

        object cardHolder = cardHolders[selectionIndex];
        object? card = ReadCardFromHolder(cardHolder);
        if (card is null)
        {
            detail = $"선택할 카드 모델을 찾지 못했습니다. index={selectionIndex}";
            return false;
        }

        if (!TryInvokeMethod(selectionScreen, "OnCardClicked", out _, card)
            && !TryInvokeMethod(cardHolder, "EmitSignalPressed", out _, cardHolder))
        {
            detail = $"카드 선택 신호를 보내지 못했습니다. card_selection_id={legalAction.CardSelectionId ?? "<none>"}, index={selectionIndex}";
            return false;
        }

        detail = $"카드 선택을 요청했습니다. card_selection_id={legalAction.CardSelectionId ?? "<none>"}, index={selectionIndex}";
        Logger.Info(detail);
        return true;
    }

    private static bool TryExecuteCardSelectionConfirm(object currentRoot, out string detail)
    {
        object? selectionScreen = ResolveCardSelectionScreen(currentRoot);
        if (selectionScreen is null)
        {
            string rootTypeName = currentRoot.GetType().FullName ?? currentRoot.GetType().Name;
            detail = $"카드 선택 확인을 실행할 화면을 찾지 못했습니다. root={rootTypeName}";
            return false;
        }

        if (TryInvokeMethod(selectionScreen, "CheckIfSelectionComplete", out _))
        {
            detail = "카드 선택 확인을 요청했습니다. method=CheckIfSelectionComplete";
            Logger.Info(detail);
            return true;
        }

        object? confirmButton = FindCardSelectionConfirmButton(selectionScreen);
        if (TryInvokeMethod(selectionScreen, "CompleteSelection", out _, confirmButton))
        {
            detail = "카드 선택 확인을 요청했습니다. method=CompleteSelection";
            Logger.Info(detail);
            return true;
        }

        if (TryInvokeMethod(selectionScreen, "ConfirmSelection", out _, confirmButton))
        {
            detail = "카드 선택 확인을 요청했습니다. method=ConfirmSelection";
            Logger.Info(detail);
            return true;
        }

        if (confirmButton is not null && TryInvokeMethod(confirmButton, "OnRelease", out _))
        {
            detail = "카드 선택 확인 버튼을 눌렀습니다.";
            Logger.Info(detail);
            return true;
        }

        detail = "카드 선택 확인 메서드와 확인 버튼을 찾지 못했습니다.";
        return false;
    }

    private static bool TryExecuteCardSelectionCancel(object currentRoot, out string detail)
    {
        object? selectionScreen = ResolveCardSelectionScreen(currentRoot);
        if (selectionScreen is null)
        {
            string rootTypeName = currentRoot.GetType().FullName ?? currentRoot.GetType().Name;
            detail = $"카드 선택 취소를 실행할 화면을 찾지 못했습니다. root={rootTypeName}";
            return false;
        }

        foreach (string methodName in new[]
        {
            "CancelSelection",
            "Cancel",
            "OnCancel",
            "OnCancelPressed",
            "OnBackButtonPressed",
            "Back",
            "Close",
            "Dismiss"
        })
        {
            if (TryInvokeMethod(selectionScreen, methodName, out _))
            {
                detail = $"카드 선택 취소를 요청했습니다. method={methodName}";
                Logger.Info(detail);
                return true;
            }
        }

        object? cancelButton = FindCardSelectionCancelButton(selectionScreen);
        if (cancelButton is not null && TryInvokeMethod(cancelButton, "OnRelease", out _))
        {
            detail = "카드 선택 취소 버튼을 눌렀습니다.";
            Logger.Info(detail);
            return true;
        }

        detail = "카드 선택 취소 메서드나 취소 버튼을 찾지 못했습니다.";
        return false;
    }

    private static bool TryExecuteMapNodeSelection(
        LegalActionSnapshot legalAction,
        object mapRoot,
        out string detail)
    {
        string rootTypeName = mapRoot.GetType().FullName ?? mapRoot.GetType().Name;
        if (!rootTypeName.Contains("NMapScreen", StringComparison.OrdinalIgnoreCase))
        {
            detail = $"지도 행동을 실행할 수 있는 화면이 아닙니다. root={rootTypeName}";
            return false;
        }

        if (ReadNamedMember(mapRoot, "IsTraveling") is bool isTraveling && isTraveling)
        {
            detail = "이미 지도 이동 중이라 새 지도 선택을 실행하지 않았습니다.";
            return false;
        }

        bool isTravelEnabled = ReadNamedMember(mapRoot, "IsTravelEnabled") is bool travelEnabled && travelEnabled;
        bool isDebugTravelEnabled = ReadNamedMember(mapRoot, "IsDebugTravelEnabled") is bool debugTravelEnabled && debugTravelEnabled;
        if (!isTravelEnabled && !isDebugTravelEnabled)
        {
            detail = "현재 지도에서 이동 선택이 활성화되어 있지 않습니다.";
            return false;
        }

        object? mapPoint = FindMapPointForAction(mapRoot, legalAction);
        if (mapPoint is null)
        {
            detail = $"선택할 지도 노드를 찾지 못했습니다. node_id={legalAction.NodeId ?? "<none>"}, row={legalAction.MapRow?.ToString() ?? "?"}, column={legalAction.MapColumn?.ToString() ?? "?"}";
            return false;
        }

        if (!IsMapPointSelectable(mapPoint))
        {
            detail = $"지도 노드가 현재 선택 가능 상태가 아닙니다. node_id={legalAction.NodeId ?? BuildMapNodeId(mapPoint)}";
            return false;
        }

        List<string> invokedCandidates = new();
        if (TryCallDeferredNoArgs(mapPoint, "ForceClick", out string deferredFailureReason))
        {
            invokedCandidates.Add("point.CallDeferred(ForceClick)");
            if (WaitForMapNodeSelection(mapRoot, legalAction))
            {
                detail = $"지도 노드 선택을 완료했습니다. method=point.CallDeferred(ForceClick), node_id={legalAction.NodeId ?? BuildMapNodeId(mapPoint)}, row={legalAction.MapRow?.ToString() ?? "?"}, column={legalAction.MapColumn?.ToString() ?? "?"}";
                Logger.Info(detail);
                return true;
            }
        }

        List<(string Label, object? Source, string MethodName, object?[] Args)> candidates = new()
        {
            ("point.ForceClick", mapPoint, "ForceClick", Array.Empty<object?>()),
            ("point.OnRelease", mapPoint, "OnRelease", Array.Empty<object?>()),
            ("point.OnPressed", mapPoint, "OnPressed", Array.Empty<object?>()),
            ("map.OnMapPointSelectedLocally(point)", mapRoot, "OnMapPointSelectedLocally", new object?[] { mapPoint })
        };

        foreach ((string label, object? source, string methodName, object?[] args) in candidates)
        {
            if (!TryInvokeMethod(source, methodName, out _, args))
            {
                continue;
            }

            invokedCandidates.Add(label);
            if (WaitForMapNodeSelection(mapRoot, legalAction))
            {
                detail = $"지도 노드 선택을 완료했습니다. method={label}, node_id={legalAction.NodeId ?? BuildMapNodeId(mapPoint)}, row={legalAction.MapRow?.ToString() ?? "?"}, column={legalAction.MapColumn?.ToString() ?? "?"}";
                Logger.Info(detail);
                return true;
            }
        }

        if (deferredFailureReason.Length > 0)
        {
            invokedCandidates.Insert(0, $"point.CallDeferred(ForceClick): {deferredFailureReason}");
        }

        string currentScreenTypeName = GetCurrentScreenTypeName();
        string tried = invokedCandidates.Count == 0 ? "<none>" : string.Join(", ", invokedCandidates);
        bool? isStillTraveling = ReadNamedMember(mapRoot, "IsTraveling") is bool stillTraveling
            ? stillTraveling
            : null;
        detail = $"지도 노드 선택 호출 뒤에도 목적지에 도달하지 못했습니다. tried={tried}, current_screen={currentScreenTypeName}, is_traveling={isStillTraveling?.ToString() ?? "<unknown>"}, node_id={legalAction.NodeId ?? BuildMapNodeId(mapPoint)}";
        return false;
    }

    private static bool TryExecuteRewardAction(
        LegalActionSnapshot legalAction,
        object rewardRoot,
        PendingClaim claim,
        out string detail,
        out bool resultDeferred)
    {
        resultDeferred = false;
        string rootTypeName = rewardRoot.GetType().FullName ?? rewardRoot.GetType().Name;
        if (!rootTypeName.Contains("NRewardsScreen", StringComparison.OrdinalIgnoreCase))
        {
            detail = $"보상 행동을 실행할 수 있는 화면이 아닙니다. root={rootTypeName}";
            return false;
        }

        if (legalAction.ActionType.Equals("proceed_reward_screen", StringComparison.OrdinalIgnoreCase))
        {
            return TryPressRewardProceedButton(rewardRoot, out detail);
        }

        if (!TryParseRewardIndex(legalAction.RewardId, out int rewardIndex))
        {
            detail = $"reward_id를 해석하지 못했습니다. reward_id={legalAction.RewardId ?? "<none>"}";
            return false;
        }

        object? rewardButton = FindRewardButtonByIndex(rewardRoot, rewardIndex);
        if (rewardButton is null)
        {
            detail = $"보상 버튼을 찾지 못했습니다. reward_id={legalAction.RewardId}, index={rewardIndex}";
            return false;
        }

        if (legalAction.ActionType.Equals("skip_card_reward", StringComparison.OrdinalIgnoreCase))
        {
            object? reward = ReadNamedMember(rewardButton, "Reward");
            _ = TryInvokeMethod(reward, "OnSkipped", out _);
            if (!TryInvokeMethod(rewardRoot, "RewardCollectedFrom", out _, rewardButton))
            {
                detail = $"카드 보상 건너뛰기 처리는 했지만 보상 화면에서 버튼 제거 호출에 실패했습니다. reward_id={legalAction.RewardId}";
                return false;
            }

            detail = $"카드 보상을 건너뛰었습니다. reward_id={legalAction.RewardId}";
            Logger.Info(detail);
            return true;
        }

        if (legalAction.ActionType.Equals("choose_card_reward", StringComparison.OrdinalIgnoreCase))
        {
            if (legalAction.CardRewardIndex is null)
            {
                detail = "choose_card_reward 행동에 card_reward_index가 없습니다.";
                return false;
            }

            if (!TryInvokeMethod(rewardButton, "OnRelease", out _))
            {
                detail = $"카드 보상 버튼을 누르지 못했습니다. reward_id={legalAction.RewardId}";
                return false;
            }

            pendingRewardCardSelection = new PendingRewardCardSelection(
                legalAction.CardRewardIndex.Value,
                claim,
                Environment.TickCount64);
            detail = $"카드 보상 화면을 열고 {legalAction.CardRewardIndex.Value}번 카드 선택을 예약했습니다. reward_id={legalAction.RewardId}";
            resultDeferred = true;
            return true;
        }

        if (!TryInvokeMethod(rewardButton, "OnRelease", out _))
        {
            detail = $"보상 버튼을 누르지 못했습니다. reward_id={legalAction.RewardId}, action_type={legalAction.ActionType}";
            return false;
        }

        detail = $"보상 버튼을 눌렀습니다. reward_id={legalAction.RewardId}, action_type={legalAction.ActionType}";
        Logger.Info(detail);
        return true;
    }

    private static void TryExecutePendingRewardCardSelection()
    {
        PendingRewardCardSelection? pending = pendingRewardCardSelection;
        if (pending is null)
        {
            return;
        }

        long nowMs = Environment.TickCount64;
        if (nowMs - pending.StartedAtMs > RewardCardSelectionTimeoutMs)
        {
            pendingRewardCardSelection = null;
            string detail = $"예약된 카드 보상 선택이 제한 시간 안에 완료되지 않았습니다. submission_id={pending.Claim.Action.SubmissionId}, card_reward_index={pending.CardRewardIndex}";
            Logger.Warning(detail);
            RememberExecuted(pending.Claim.Action.SubmissionId);
            ReportResult(pending.Claim, "failed", detail);
            return;
        }

        object? selectionScreen = FindTopOverlayByTypeName("NCardRewardSelectionScreen");
        if (selectionScreen is null)
        {
            return;
        }

        object? completionSource = ReadNamedMember(selectionScreen, "_completionSource");
        if (completionSource is null)
        {
            return;
        }

        if (IsCompletionSourceCompleted(completionSource))
        {
            pendingRewardCardSelection = null;
            string detail = $"카드 보상 선택이 이미 완료되어 예약을 종료했습니다. submission_id={pending.Claim.Action.SubmissionId}, card_reward_index={pending.CardRewardIndex}";
            Logger.Info(detail);
            RememberExecuted(pending.Claim.Action.SubmissionId);
            ReportResult(pending.Claim, "applied", detail);
            return;
        }

        List<object> cardHolders = FindCardHolders(selectionScreen);
        if (cardHolders.Count == 0)
        {
            return;
        }

        if (pending.CardRewardIndex < 0 || pending.CardRewardIndex >= cardHolders.Count)
        {
            pendingRewardCardSelection = null;
            Logger.Warning($"카드 보상 선택 인덱스가 화면의 카드 수를 벗어났습니다. index={pending.CardRewardIndex}, count={cardHolders.Count}");
            return;
        }

        object cardHolder = cardHolders[pending.CardRewardIndex];
        if (!TryInvokeMethod(selectionScreen, "SelectCard", out _, cardHolder))
        {
            if (IsCompletionSourceCompleted(completionSource))
            {
                pendingRewardCardSelection = null;
                string completedDetail = $"카드 보상 선택이 완료된 상태로 확인되었습니다. submission_id={pending.Claim.Action.SubmissionId}, card_reward_index={pending.CardRewardIndex}";
                Logger.Info(completedDetail);
                RememberExecuted(pending.Claim.Action.SubmissionId);
                ReportResult(pending.Claim, "applied", completedDetail);
            }

            return;
        }

        pendingRewardCardSelection = null;
        string successDetail = $"카드 보상 선택 신호를 보냈습니다. submission_id={pending.Claim.Action.SubmissionId}, card_reward_index={pending.CardRewardIndex}";
        Logger.Info(successDetail);
        RememberExecuted(pending.Claim.Action.SubmissionId);
        ReportResult(pending.Claim, "applied", successDetail);
    }

    private static bool TryPressRewardProceedButton(object rewardScreen, out string detail)
    {
        object? proceedButton = ReadNamedMember(rewardScreen, "_proceedButton");
        if (proceedButton is null)
        {
            detail = "보상 화면의 진행 버튼을 찾지 못했습니다.";
            return false;
        }

        if (!TryInvokeMethod(rewardScreen, "OnProceedButtonPressed", out _, proceedButton))
        {
            detail = "보상 화면 진행 버튼 호출에 실패했습니다.";
            return false;
        }

        detail = "보상 화면 진행 버튼을 눌렀습니다.";
        Logger.Info(detail);
        return true;
    }

    private static object? FindRewardButtonByIndex(object rewardScreen, int rewardIndex)
    {
        return ExpandValue(ReadNamedMember(rewardScreen, "_rewardButtons"))
            .Where(value => !IsScalar(value.GetType()))
            .Skip(rewardIndex)
            .FirstOrDefault();
    }

    private static object? ResolveEventRoom(object currentRoot)
    {
        string rootTypeName = currentRoot.GetType().FullName ?? currentRoot.GetType().Name;
        if (rootTypeName.Contains("NEventRoom", StringComparison.OrdinalIgnoreCase))
        {
            return currentRoot;
        }

        Type? eventRoomType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom");
        object? eventRoom = ReadStaticNamedMember(eventRoomType, "Instance");
        string eventRoomTypeName = eventRoom?.GetType().FullName ?? eventRoom?.GetType().Name ?? string.Empty;
        return eventRoomTypeName.Contains("NEventRoom", StringComparison.OrdinalIgnoreCase) ? eventRoom : null;
    }

    private static object? ResolveRestSiteRoom(object currentRoot)
    {
        string rootTypeName = currentRoot.GetType().FullName ?? currentRoot.GetType().Name;
        if (rootTypeName.Contains("NRestSiteRoom", StringComparison.OrdinalIgnoreCase))
        {
            return currentRoot;
        }

        Type? restSiteRoomType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom");
        object? restSiteRoom = ReadStaticNamedMember(restSiteRoomType, "Instance");
        string restSiteRoomTypeName = restSiteRoom?.GetType().FullName ?? restSiteRoom?.GetType().Name ?? string.Empty;
        return restSiteRoomTypeName.Contains("NRestSiteRoom", StringComparison.OrdinalIgnoreCase) ? restSiteRoom : null;
    }

    private static object? ResolveShopRoom(object currentRoot)
    {
        string rootTypeName = currentRoot.GetType().FullName ?? currentRoot.GetType().Name;
        if (rootTypeName.Contains("Merchant", StringComparison.OrdinalIgnoreCase)
            || rootTypeName.Contains("Shop", StringComparison.OrdinalIgnoreCase)
            || rootTypeName.Contains("Store", StringComparison.OrdinalIgnoreCase))
        {
            return currentRoot;
        }

        return EnumerateNodeDescendants(currentRoot)
            .FirstOrDefault(node =>
            {
                string typeName = node.GetType().FullName ?? node.GetType().Name;
                return typeName.Contains("Merchant", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Shop", StringComparison.OrdinalIgnoreCase)
                    || typeName.Contains("Store", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static object? ResolveTreasureRoom(object currentRoot)
    {
        string rootTypeName = currentRoot.GetType().FullName ?? currentRoot.GetType().Name;
        if (rootTypeName.Contains("NTreasureRoom", StringComparison.OrdinalIgnoreCase))
        {
            return currentRoot;
        }

        Type? runType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.NRun");
        object? run = ReadStaticNamedMember(runType, "Instance");
        object? treasureRoom = ReadNamedMember(run, "TreasureRoom")
            ?? ReadNamedMember(run, "_treasureRoom")
            ?? ReadNamedMember(run, "treasureRoom");
        string treasureRoomTypeName = treasureRoom?.GetType().FullName ?? treasureRoom?.GetType().Name ?? string.Empty;
        if (treasureRoomTypeName.Contains("NTreasureRoom", StringComparison.OrdinalIgnoreCase))
        {
            return treasureRoom;
        }

        return EnumerateNodeDescendants(currentRoot)
            .FirstOrDefault(node =>
            {
                string typeName = node.GetType().FullName ?? node.GetType().Name;
                return typeName.Contains("NTreasureRoom", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static object? ResolveCardSelectionScreen(object currentRoot)
    {
        if (IsCardSelectionScreen(currentRoot))
        {
            return currentRoot;
        }

        Type? overlayStackType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack");
        object? overlayStack = ReadStaticNamedMember(overlayStackType, "Instance");
        object? topOverlay = null;
        _ = TryInvokeMethod(overlayStack, "Peek", out topOverlay);
        if (IsCardSelectionScreen(topOverlay))
        {
            return topOverlay;
        }

        Type? screenContextType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext");
        object? screenContext = ReadStaticNamedMember(screenContextType, "Instance");
        object? currentScreen = null;
        _ = TryInvokeMethod(screenContext, "GetCurrentScreen", out currentScreen);
        return IsCardSelectionScreen(currentScreen) ? currentScreen : null;
    }

    private static bool WaitForShopProceedTransitionToMap()
    {
        const int timeoutMs = 5000;
        const int pollIntervalMs = 100;
        Stopwatch stopwatch = Stopwatch.StartNew();
        int stableMapPollCount = 0;
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(pollIntervalMs);
            object? currentScreen = GetCurrentScreen();
            if (currentScreen is not null && IsMapScreen(currentScreen))
            {
                stableMapPollCount++;
                if (stableMapPollCount >= 5)
                {
                    return true;
                }
            }
            else
            {
                stableMapPollCount = 0;
            }
        }

        return false;
    }

    private static bool WaitForTreasureChestOpen(object treasureRoom)
    {
        const int timeoutMs = 10000;
        const int pollIntervalMs = 100;
        Stopwatch stopwatch = Stopwatch.StartNew();
        int stableOpenPollCount = 0;
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(pollIntervalMs);
            if (IsTreasureChestOpened(treasureRoom))
            {
                stableOpenPollCount++;
                if (stableOpenPollCount >= 3)
                {
                    return true;
                }
            }
            else
            {
                stableOpenPollCount = 0;
            }
        }

        return false;
    }

    private static bool WaitForTreasureRelicClaim(object treasureRoom)
    {
        const int timeoutMs = 45000;
        const int pollIntervalMs = 100;
        Stopwatch stopwatch = Stopwatch.StartNew();
        int stableCompletionPollCount = 0;
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(pollIntervalMs);
            bool completed = !IsTreasureRelicCollectionOpen(treasureRoom) && IsTreasureProceedEnabled(treasureRoom);
            if (completed)
            {
                stableCompletionPollCount++;
                if (stableCompletionPollCount >= 3)
                {
                    return true;
                }
            }
            else
            {
                stableCompletionPollCount = 0;
            }
        }

        return false;
    }

    private static bool WaitForTreasureProceedTransitionToMap()
    {
        const int timeoutMs = 8000;
        const int pollIntervalMs = 100;
        Stopwatch stopwatch = Stopwatch.StartNew();
        int stableMapPollCount = 0;
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(pollIntervalMs);
            object? currentScreen = GetCurrentScreen();
            if (currentScreen is not null && IsMapScreen(currentScreen))
            {
                stableMapPollCount++;
                if (stableMapPollCount >= 5)
                {
                    return true;
                }
            }
            else
            {
                stableMapPollCount = 0;
            }
        }

        return false;
    }

    private static bool WaitForMapNodeSelection(object mapRoot, LegalActionSnapshot legalAction)
    {
        const int timeoutMs = 45000;
        const int pollIntervalMs = 100;
        Stopwatch stopwatch = Stopwatch.StartNew();
        int stableCompletionPollCount = 0;
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            Thread.Sleep(pollIntervalMs);
            object? currentScreen = GetCurrentScreen();
            bool screenLeftMap = currentScreen is not null && !IsMapScreen(currentScreen);
            bool traveling = ReadNamedMember(mapRoot, "IsTraveling") is bool isTraveling && isTraveling;
            bool arrivedAtTargetOnMap = !traveling && IsCurrentMapPointTarget(mapRoot, legalAction);
            if (screenLeftMap || arrivedAtTargetOnMap)
            {
                stableCompletionPollCount++;
                if (stableCompletionPollCount >= 5)
                {
                    return true;
                }
            }
            else
            {
                stableCompletionPollCount = 0;
            }
        }

        return false;
    }

    private static object? GetCurrentScreen()
    {
        Type? screenContextType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext.ActiveScreenContext");
        object? screenContext = ReadStaticNamedMember(screenContextType, "Instance");
        object? currentScreen = null;
        _ = TryInvokeMethod(screenContext, "GetCurrentScreen", out currentScreen);
        return currentScreen;
    }

    private static string GetCurrentScreenTypeName()
    {
        object? currentScreen = GetCurrentScreen();
        return currentScreen?.GetType().FullName ?? currentScreen?.GetType().Name ?? "<unknown>";
    }

    private static bool TryCallDeferredNoArgs(object source, string methodName, out string failureReason)
    {
        failureReason = string.Empty;
        MethodInfo? targetMethod = source.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
                method.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                && method.GetParameters().Length == 0);
        if (targetMethod is null)
        {
            failureReason = $"{methodName} 메서드를 찾지 못했습니다.";
            return false;
        }

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
        if (callDeferred is null)
        {
            failureReason = "Godot CallDeferred 메서드를 찾지 못했습니다.";
            return false;
        }

        ParameterInfo[] callDeferredParameters = callDeferred.GetParameters();
        object? deferredMethodName = CreateGodotStringName(callDeferredParameters[0].ParameterType, methodName);
        if (deferredMethodName is null)
        {
            failureReason = "Godot StringName 값을 만들 수 없습니다.";
            return false;
        }

        Type? argumentElementType = callDeferredParameters[1].ParameterType.GetElementType();
        if (argumentElementType is null)
        {
            failureReason = "CallDeferred 인자 배열 타입을 해석하지 못했습니다.";
            return false;
        }

        Array emptyArguments = Array.CreateInstance(argumentElementType, 0);
        try
        {
            callDeferred.Invoke(source, new object[] { deferredMethodName, emptyArguments });
            return true;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            failureReason = $"{exception.InnerException.GetType().Name}: {exception.InnerException.Message}";
            return false;
        }
        catch (Exception exception)
        {
            failureReason = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static object? CreateGodotStringName(Type targetType, string value)
    {
        if (targetType == typeof(string))
        {
            return value;
        }

        ConstructorInfo? constructor = targetType.GetConstructor(new[] { typeof(string) });
        if (constructor is not null)
        {
            return constructor.Invoke(new object[] { value });
        }

        MethodInfo? implicitOperator = targetType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
                method.Name == "op_Implicit"
                && method.ReturnType == targetType
                && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == typeof(string));
        return implicitOperator?.Invoke(null, new object[] { value });
    }

    private static bool IsShopScreen(object source)
    {
        string typeName = source.GetType().FullName ?? source.GetType().Name;
        return typeName.Contains("Merchant", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Shop", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("Store", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMapScreen(object source)
    {
        string typeName = source.GetType().FullName ?? source.GetType().Name;
        return typeName.Contains("NMapScreen", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains(".Map.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCurrentMapPointTarget(object mapRoot, LegalActionSnapshot legalAction)
    {
        object? runState = ReadNamedMember(mapRoot, "_runState")
            ?? ReadNamedMember(mapRoot, "RunState")
            ?? ReadNamedMember(mapRoot, "runState");
        object? currentMapPoint = ReadNamedMember(runState, "CurrentMapPoint")
            ?? ReadNamedMember(runState, "currentMapPoint")
            ?? ReadNamedMember(runState, "_currentMapPoint");
        if (currentMapPoint is null)
        {
            return false;
        }

        string nodeId = BuildMapNodeIdFromPoint(currentMapPoint);
        if (!string.IsNullOrWhiteSpace(legalAction.NodeId)
            && string.Equals(nodeId, legalAction.NodeId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        (int? row, int? column) = ReadMapCoord(currentMapPoint);
        return row is not null
            && column is not null
            && legalAction.MapRow == row
            && legalAction.MapColumn == column;
    }

    private static bool IsCardSelectionScreen(object? source)
    {
        if (source is null)
        {
            return false;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        return ContainsAny(
            typeName,
            "NDeckEnchantSelectScreen",
            "NDeckUpgradeSelectScreen",
            "NDeckTransformSelectScreen",
            "NDeckCardSelectScreen");
    }

    private static object? FindEventOptionButton(object eventRoom, int optionIndex)
    {
        object? layout = ReadNamedMember(eventRoom, "Layout");
        List<object> optionButtons = ExpandValue(ReadNamedMember(layout, "OptionButtons"))
            .Where(IsEventOptionButton)
            .ToList();

        if (optionButtons.Count == 0)
        {
            optionButtons = EnumerateNodeDescendants(eventRoom)
                .Where(IsEventOptionButton)
                .ToList();
        }

        return optionButtons.FirstOrDefault(button => ReadInt(button, "Index") == optionIndex)
            ?? optionButtons.Skip(optionIndex).FirstOrDefault();
    }

    private static object? FindRestSiteOptionButton(object restSiteRoom, int optionIndex)
    {
        return EnumerateNodeDescendants(restSiteRoom)
            .Where(IsRestSiteButton)
            .Skip(optionIndex)
            .FirstOrDefault();
    }

    private static bool IsEventOptionButton(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return typeName.Contains("NEventOptionButton", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRestSiteButton(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return typeName.Contains("NRestSiteButton", StringComparison.OrdinalIgnoreCase);
    }

    private static object? FindTreasureChestButton(object treasureRoom)
    {
        object? chestButton = ReadNamedMember(treasureRoom, "_chestButton")
            ?? ReadNamedMember(treasureRoom, "ChestButton")
            ?? ReadNamedMember(treasureRoom, "chestButton");
        if (chestButton is not null)
        {
            return chestButton;
        }

        return EnumerateNodeDescendants(treasureRoom)
            .FirstOrDefault(node =>
            {
                string typeName = node.GetType().FullName ?? node.GetType().Name;
                string nodeName = ReadNamedMember(node, "Name")?.ToString() ?? string.Empty;
                return typeName.Contains("NButton", StringComparison.OrdinalIgnoreCase)
                    && nodeName.Contains("Chest", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static object? FindTreasureRelicCollection(object treasureRoom)
    {
        object? relicCollection = ReadNamedMember(treasureRoom, "_relicCollection")
            ?? ReadNamedMember(treasureRoom, "RelicCollection")
            ?? ReadNamedMember(treasureRoom, "relicCollection");
        if (relicCollection is not null)
        {
            return relicCollection;
        }

        return EnumerateNodeDescendants(treasureRoom)
            .FirstOrDefault(node =>
            {
                string typeName = node.GetType().FullName ?? node.GetType().Name;
                return typeName.Contains("NTreasureRoomRelicCollection", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static object? FindTreasureRelicHolder(object relicCollection, int relicIndex)
    {
        List<object> holders = ExpandValue(ReadNamedMember(relicCollection, "_holdersInUse"))
            .Where(IsTreasureRelicHolder)
            .ToList();
        if (holders.Count == 0)
        {
            object? singleplayerHolder = ReadNamedMember(relicCollection, "SingleplayerRelicHolder");
            if (IsTreasureRelicHolder(singleplayerHolder))
            {
                holders.Add(singleplayerHolder!);
            }
        }

        if (holders.Count == 0)
        {
            holders = EnumerateNodeDescendants(relicCollection)
                .Where(IsTreasureRelicHolder)
                .ToList();
        }

        return holders.FirstOrDefault(holder => ReadInt(holder, "Index") == relicIndex)
            ?? holders.Skip(relicIndex).FirstOrDefault();
    }

    private static bool IsTreasureRelicHolder(object? value)
    {
        if (value is null)
        {
            return false;
        }

        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return typeName.Contains("NTreasureRoomRelicHolder", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTreasureChestOpened(object treasureRoom)
    {
        bool hasChestBeenOpened = ReadNamedMember(treasureRoom, "_hasChestBeenOpened") is bool opened && opened;
        bool isRelicCollectionOpen = ReadNamedMember(treasureRoom, "_isRelicCollectionOpen") is bool collectionOpen && collectionOpen;
        bool hasDefaultFocus = ReadNamedMember(treasureRoom, "DefaultFocusedControl") is not null;
        return hasChestBeenOpened || isRelicCollectionOpen || hasDefaultFocus;
    }

    private static bool IsTreasureRelicCollectionOpen(object treasureRoom)
    {
        return ReadNamedMember(treasureRoom, "_isRelicCollectionOpen") is bool collectionOpen && collectionOpen;
    }

    private static bool IsTreasureProceedEnabled(object treasureRoom)
    {
        object? proceedButton = ReadNamedMember(treasureRoom, "ProceedButton")
            ?? ReadNamedMember(treasureRoom, "_proceedButton")
            ?? ReadNamedMember(treasureRoom, "proceedButton");
        return ReadNamedMember(proceedButton, "IsEnabled") is bool isEnabled && isEnabled;
    }

    private static object? FindMapPointForAction(object mapScreen, LegalActionSnapshot legalAction)
    {
        object? mapPointDictionary = ReadNamedMember(mapScreen, "_mapPointDictionary");
        IEnumerable<object> mapPointCandidates = ExpandValue(ReadNamedMember(mapPointDictionary, "Values"))
            .Where(value => !IsScalar(value.GetType()));

        if (!mapPointCandidates.Any())
        {
            mapPointCandidates = EnumerateNodeDescendants(mapScreen)
                .Where(value => (value.GetType().FullName ?? value.GetType().Name).Contains("NMapPoint", StringComparison.OrdinalIgnoreCase));
        }

        return mapPointCandidates.FirstOrDefault(candidate => IsSameMapPoint(candidate, legalAction));
    }

    private static bool IsSameMapPoint(object mapPoint, LegalActionSnapshot legalAction)
    {
        string nodeId = BuildMapNodeId(mapPoint);
        if (!string.IsNullOrWhiteSpace(legalAction.NodeId)
            && string.Equals(nodeId, legalAction.NodeId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        (int? row, int? column) = ReadMapPointCoord(mapPoint);
        return row is not null
            && column is not null
            && legalAction.MapRow == row
            && legalAction.MapColumn == column;
    }

    private static bool IsMapPointSelectable(object mapPoint)
    {
        if (ReadNamedMember(mapPoint, "IsEnabled") is bool isEnabled)
        {
            return isEnabled;
        }

        string? state = ReadNamedMember(mapPoint, "State")?.ToString();
        return string.Equals(state, "Travelable", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildMapNodeId(object mapPoint)
    {
        (int? row, int? column) = ReadMapPointCoord(mapPoint);
        return row is null || column is null ? "map_unknown" : $"map_r{row.Value}_c{column.Value}";
    }

    private static string BuildMapNodeIdFromPoint(object point)
    {
        (int? row, int? column) = ReadMapCoord(point);
        return row is null || column is null ? "map_unknown" : $"map_r{row.Value}_c{column.Value}";
    }

    private static (int? row, int? column) ReadMapPointCoord(object mapPoint)
    {
        object? point = ReadNamedMember(mapPoint, "Point");
        return point is null ? (null, null) : ReadMapCoord(point);
    }

    private static (int? row, int? column) ReadMapCoord(object point)
    {
        object? coord = ReadNamedMember(point, "coord") ?? ReadNamedMember(point, "Coord");
        int? column = ReadInt(coord, "col") ?? ReadInt(coord, "Col") ?? ReadInt(coord, "column") ?? ReadInt(coord, "Column");
        int? row = ReadInt(coord, "row") ?? ReadInt(coord, "Row");
        return (row, column);
    }

    private static bool TryParseRewardIndex(string? rewardId, out int rewardIndex)
    {
        rewardIndex = -1;
        if (string.IsNullOrWhiteSpace(rewardId)
            || !rewardId.StartsWith("reward_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(rewardId["reward_".Length..], out rewardIndex) && rewardIndex >= 0;
    }

    private static object? FindTopOverlayByTypeName(string typeNamePart)
    {
        Type? overlayStackType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack");
        object? overlayStack = ReadStaticNamedMember(overlayStackType, "Instance");
        object? topOverlay = null;
        _ = TryInvokeMethod(overlayStack, "Peek", out topOverlay);
        string typeName = topOverlay?.GetType().FullName ?? topOverlay?.GetType().Name ?? string.Empty;
        return typeName.Contains(typeNamePart, StringComparison.OrdinalIgnoreCase) ? topOverlay : null;
    }

    private static IEnumerable<object> EnumerateNodeChildren(object? node)
    {
        object? children = null;
        if (!TryInvokeMethod(node, "GetChildren", out children))
        {
            _ = TryInvokeMethod(node, "GetChildren", out children, false);
        }

        return ExpandValue(children).Where(value => !IsScalar(value.GetType()));
    }

    private static List<object> FindCardHolders(object selectionScreen)
    {
        object? cardRow = ReadNamedMember(selectionScreen, "_cardRow");
        List<object> cardHolders = EnumerateNodeDescendants(cardRow)
            .Where(IsCardHolder)
            .ToList();
        if (cardHolders.Count > 0)
        {
            return cardHolders;
        }

        return EnumerateNodeDescendants(selectionScreen)
            .Where(IsCardHolder)
            .ToList();
    }

    private static List<object> FindGridCardHolders(object selectionScreen)
    {
        object? grid = ReadNamedMember(selectionScreen, "_grid");
        List<object> cardHolders = EnumerateNodeDescendants(grid)
            .Where(IsGridCardHolder)
            .ToList();
        if (cardHolders.Count > 0)
        {
            return cardHolders;
        }

        return EnumerateNodeDescendants(selectionScreen)
            .Where(IsGridCardHolder)
            .ToList();
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

            foreach (object child in EnumerateNodeChildren(current))
            {
                queue.Enqueue(child);
            }
        }
    }

    private static bool IsCardHolder(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return typeName.Contains("NCardHolder", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("NGridCardHolder", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGridCardHolder(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return typeName.Contains("NGridCardHolder", StringComparison.OrdinalIgnoreCase);
    }

    private static object? ReadCardFromHolder(object cardHolder)
    {
        object? cardModel = ReadNamedMember(cardHolder, "CardModel")
            ?? ReadNamedMember(cardHolder, "_baseCard");
        if (cardModel is not null)
        {
            return cardModel;
        }

        object? cardNode = ReadNamedMember(cardHolder, "CardNode");
        return ReadNamedMember(cardNode, "Model") ?? cardNode;
    }

    private static object? FindCardSelectionConfirmButton(object selectionScreen)
    {
        string[] memberNames =
        {
            "_singlePreviewConfirmButton",
            "_multiPreviewConfirmButton",
            "_previewConfirmButton",
            "_confirmButton"
        };

        List<object> buttons = memberNames
            .Select(name => ReadNamedMember(selectionScreen, name))
            .Where(button => button is not null)
            .Cast<object>()
            .ToList();

        return buttons.FirstOrDefault(button => ReadNamedMember(button, "IsEnabled") is bool isEnabled && isEnabled)
            ?? buttons.FirstOrDefault();
    }

    private static object? FindCardSelectionCancelButton(object selectionScreen)
    {
        string[] memberNames =
        {
            "_cancelButton",
            "CancelButton",
            "cancelButton",
            "_backButton",
            "BackButton",
            "backButton",
            "_closeButton",
            "CloseButton",
            "closeButton",
            "_returnButton",
            "ReturnButton",
            "returnButton"
        };

        List<object> buttons = memberNames
            .Select(name => ReadNamedMember(selectionScreen, name))
            .Where(button => button is not null)
            .Cast<object>()
            .ToList();

        if (buttons.Count == 0)
        {
            buttons = EnumerateNodeDescendants(selectionScreen)
                .Where(candidate =>
                {
                    string typeName = candidate.GetType().FullName ?? candidate.GetType().Name;
                    string nodeName = ReadNamedMember(candidate, "Name")?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(nodeName)
                        && TryInvokeMethod(candidate, "GetName", out object? invokedName))
                    {
                        nodeName = invokedName?.ToString() ?? string.Empty;
                    }

                    string label = $"{typeName} {nodeName}";
                    return ContainsAny(typeName, "Button")
                        && ContainsAny(label, "Cancel", "Back", "Close", "Return");
                })
                .ToList();
        }

        return buttons.FirstOrDefault(button => ReadNamedMember(button, "IsEnabled") is bool isEnabled && isEnabled)
            ?? buttons.FirstOrDefault();
    }

    private static bool IsCompletionSourceCompleted(object completionSource)
    {
        object? task = ReadNamedMember(completionSource, "Task");
        object? isCompleted = ReadNamedMember(task, "IsCompleted");
        return isCompleted is bool completed && completed;
    }

    private static bool TryExecuteUsePotion(LegalActionSnapshot legalAction, object combatRoot, out string detail)
    {
        if (legalAction.PotionSlotIndex is null)
        {
            detail = "use_potion 행동에는 potion_slot_index가 필요합니다.";
            return false;
        }

        object? player = ResolveRuntimePlayerForAction();
        if (player is null)
        {
            detail = "포션 사용에 필요한 플레이어 객체를 찾지 못했습니다.";
            return false;
        }

        List<object?> potionSlots = ResolvePotionSlots(player).ToList();
        int slotIndex = legalAction.PotionSlotIndex.Value;
        if (slotIndex < 0 || slotIndex >= potionSlots.Count)
        {
            detail = $"포션 슬롯 인덱스가 범위를 벗어났습니다. index={slotIndex}, count={potionSlots.Count}";
            return false;
        }

        object? potion = ExtractRuntimePotionModel(potionSlots[slotIndex]);
        if (potion is null)
        {
            detail = $"선택한 포션 슬롯이 비어 있습니다. index={slotIndex}";
            return false;
        }

        string? runtimePotionId = ReadObjectId(potion);
        if (!string.IsNullOrWhiteSpace(legalAction.PotionId)
            && !string.IsNullOrWhiteSpace(runtimePotionId)
            && !IsSameShopModelId(runtimePotionId, legalAction.PotionId))
        {
            detail = $"포션 슬롯의 포션이 바뀌었습니다. expected={legalAction.PotionId}, actual={runtimePotionId}";
            return false;
        }

        if (!TryResolvePotionTarget(legalAction, potion, combatRoot, out object? target, out detail))
        {
            return false;
        }

        if (!TryEnqueueUsePotionAction(potion, target, out detail))
        {
            return false;
        }

        string targetText = target is null
            ? "대상 없음"
            : ReadNamedMember(target, "LogName")?.ToString()
                ?? ReadNamedMember(target, "Name")?.ToString()
                ?? legalAction.TargetId
                ?? "대상";
        detail = $"UsePotionAction 입력 성공: potion={runtimePotionId ?? legalAction.PotionId ?? "<unknown>"}, slot={slotIndex}, target={targetText}";
        Logger.Info(detail);
        return true;
    }

    private static object? ResolveRuntimePlayerForAction()
    {
        object? player = CombatStateExporter.GetLatestRuntimePlayer();
        if (player is not null)
        {
            return player;
        }

        Type? localContextType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Context.LocalContext");
        return localContextType is null
            ? null
            : InvokeStaticNoArgMethod(localContextType, "GetMe");
    }

    private static IEnumerable<object?> ResolvePotionSlots(object player)
    {
        object? slots = ReadNamedMember(player, "PotionSlots")
            ?? ReadNamedMember(player, "potionSlots")
            ?? ReadNamedMember(player, "_potionSlots")
            ?? ReadNamedMember(player, "Potions")
            ?? ReadNamedMember(player, "potions")
            ?? ReadNamedMember(player, "_potions");

        if (slots is IEnumerable enumerable && slots is not string)
        {
            foreach (object? slot in enumerable)
            {
                yield return slot;
            }
        }
    }

    private static object? ExtractRuntimePotionModel(object? source)
    {
        if (source is null)
        {
            return null;
        }

        object? nested = ReadNamedMember(source, "Potion")
            ?? ReadNamedMember(source, "potion")
            ?? ReadNamedMember(source, "_potion")
            ?? ReadNamedMember(source, "PotionModel")
            ?? ReadNamedMember(source, "potionModel")
            ?? ReadNamedMember(source, "_potionModel")
            ?? ReadNamedMember(source, "Model")
            ?? ReadNamedMember(source, "model")
            ?? ReadNamedMember(source, "_model");
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

    private static bool TryResolvePotionTarget(
        LegalActionSnapshot legalAction,
        object potion,
        object combatRoot,
        out object? target,
        out string detail)
    {
        target = null;
        bool requiresTarget = legalAction.RequiresTarget == true || RuntimePotionRequiresTarget(potion);
        if (!requiresTarget)
        {
            detail = "대상이 필요 없는 포션입니다.";
            return true;
        }

        object? combatState = FindCombatState(potion)
            ?? FindCombatState(combatRoot)
            ?? FindCombatState(CombatStateExporter.GetLatestRuntimePlayer());
        if (combatState is null)
        {
            detail = "포션 대상을 찾기 위한 CombatState를 찾지 못했습니다.";
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

        detail = $"포션 대상을 찾지 못했습니다. target_id={legalAction.TargetId}, target_combat_id={legalAction.TargetCombatId?.ToString() ?? "<none>"}";
        return false;
    }

    private static bool RuntimePotionRequiresTarget(object potion)
    {
        string? targetType = ReadNamedMember(potion, "TargetType")?.ToString()
            ?? ReadNamedMember(potion, "targetType")?.ToString()
            ?? ReadNamedMember(potion, "_targetType")?.ToString();
        return !string.IsNullOrWhiteSpace(targetType)
            && ContainsAny(targetType, "Enemy", "Target", "Monster", "Creature")
            && !ContainsAny(targetType, "None", "Self", "Player", "All", "Random");
    }

    private static bool TryEnqueueUsePotionAction(object potion, object? target, out string detail)
    {
        try
        {
            Type? actionType = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.UsePotionAction");
            if (actionType is null)
            {
                detail = "UsePotionAction 타입을 찾지 못했습니다.";
                return false;
            }

            object? combatManager = ReadStaticNamedMember(AccessTools.TypeByName("MegaCrit.Sts2.Core.Combat.CombatManager"), "Instance");
            bool isInProgress = ReadBool(combatManager, "IsInProgress") ?? true;
            object? action = Activator.CreateInstance(actionType, potion, target, isInProgress);
            if (action is null)
            {
                detail = "UsePotionAction 인스턴스 생성에 실패했습니다.";
                return false;
            }

            InvokePotionBeforeUse(potion);
            SetPotionQueued(potion);

            object? synchronizer = ResolveActionQueueSynchronizer();
            if (synchronizer is null)
            {
                detail = "ActionQueueSynchronizer를 찾지 못했습니다.";
                return false;
            }

            MethodInfo? requestEnqueue = FindRequestEnqueueMethod(synchronizer, action);
            if (requestEnqueue is null)
            {
                detail = "ActionQueueSynchronizer.RequestEnqueue 메서드를 찾지 못했습니다.";
                return false;
            }

            requestEnqueue.Invoke(synchronizer, new[] { action });
            detail = "UsePotionAction을 큐에 입력했습니다.";
            return true;
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

    private static void InvokePotionBeforeUse(object potion)
    {
        object? beforeUse = ReadNamedMember(potion, "BeforeUse")
            ?? ReadNamedMember(potion, "BeforePotionUsed")
            ?? ReadNamedMember(potion, "_beforeUse")
            ?? ReadNamedMember(potion, "_beforePotionUsed");
        if (beforeUse is Action action)
        {
            action.Invoke();
        }
    }

    private static void SetPotionQueued(object potion)
    {
        SetNamedMember(potion, "IsQueued", true);
        SetNamedMember(potion, "isQueued", true);
        SetNamedMember(potion, "_isQueued", true);
    }

    private static object? ResolveActionQueueSynchronizer()
    {
        Type? runManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager");
        object? runManager = ReadStaticNamedMember(runManagerType, "Instance");
        return ReadNamedMember(runManager, "ActionQueueSynchronizer");
    }

    private static MethodInfo? FindRequestEnqueueMethod(object synchronizer, object action)
    {
        return synchronizer.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name.Equals("RequestEnqueue", StringComparison.OrdinalIgnoreCase)
                && method.GetParameters().Length == 1
                && IsArgumentCompatible(method.GetParameters()[0].ParameterType, action));
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

    private static bool TryInvokeMethod(object? source, string methodName, out object? result, params object?[] args)
    {
        result = null;
        if (source is null)
        {
            return false;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo? method = source.GetType()
            .GetMethods(flags)
            .Where(candidate => candidate.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => candidate.GetParameters().Length == args.Length)
            .FirstOrDefault(candidate =>
            {
                ParameterInfo[] parameters = candidate.GetParameters();
                for (int index = 0; index < parameters.Length; index++)
                {
                    if (!IsArgumentCompatible(parameters[index].ParameterType, args[index]))
                    {
                        return false;
                    }
                }

                return true;
            });
        if (method is null)
        {
            return false;
        }

        try
        {
            result = method.Invoke(source, args);
            return true;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            Logger.Warning($"{source.GetType().Name}.{method.Name} 호출 중 예외가 발생했습니다. {exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
            return false;
        }
        catch (Exception exception)
        {
            Logger.Warning($"{source.GetType().Name}.{method.Name} 호출에 실패했습니다. {exception.GetType().Name}: {exception.Message}");
            return false;
        }
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

    private static bool SetNamedMember(object? source, string memberName, object? value)
    {
        if (source is null)
        {
            return false;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MemberInfo? member = source.GetType()
            .GetMember(memberName, flags)
            .FirstOrDefault(candidate => candidate is FieldInfo or PropertyInfo);
        try
        {
            switch (member)
            {
                case FieldInfo field:
                    field.SetValue(source, value);
                    return true;
                case PropertyInfo property when property.SetMethod is not null:
                    property.SetValue(source, value);
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
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

    private static bool? ReadBool(object? source, string memberName)
    {
        object? value = ReadNamedMember(source, memberName);
        if (value is null)
        {
            return null;
        }

        if (value is bool boolean)
        {
            return boolean;
        }

        if (bool.TryParse(value.ToString(), out bool parsed))
        {
            return parsed;
        }

        return null;
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

    private sealed record PendingRewardCardSelection(
        int CardRewardIndex,
        PendingClaim Claim,
        long StartedAtMs);
}
