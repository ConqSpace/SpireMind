using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using HarmonyLib;

namespace SpireMindMod;

internal static class CombatActionExecutor
{
    private const int ClaimCooldownMs = 100;
    private const int BackgroundPollIntervalMs = 250;
    private const int DiagnosticLogIntervalMs = 5000;
    private const int ClaimInFlightWatchdogMs = 3000;
    private const int RewardCardSelectionTimeoutMs = 5000;
    private const int PotionRewardClaimAfterDiscardTimeoutMs = 5000;
    private const int CombatActionConfirmationForceExportIntervalMs = 1000;
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
    private static PendingPotionRewardClaim? pendingPotionRewardClaim;
    private static PendingTreasureRelicClaim? pendingTreasureRelicClaim;
    private static PendingMapNodeSelection? pendingMapNodeSelection;
    private static PendingCombatActionConfirmation? pendingCombatActionConfirmation;
    private static long roomEnteredSignalCount;

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

        Logger.Info("?袁る떮 ??る?claim 獄쏄퉫???깆뒲???類ㅼ뵥????뽰삂??됰뮸??덈뼄.");
    }

    public static void StopBackgroundPolling()
    {
        if (Interlocked.Exchange(ref backgroundPollingStarted, 0) == 0)
        {
            return;
        }

        Timer? timer = Interlocked.Exchange(ref backgroundPollTimer, null);
        timer?.Dispose();
        Logger.Info("?袁る떮 ??る?claim 獄쏄퉫???깆뒲???類ㅼ뵥??餓λ쵐???됰뮸??덈뼄.");
    }

    public static void Tick()
    {
        AutotestCommandChannel.Tick();
        TryStartClaimRequest();
    }

    public static void TickMainThread()
    {
        AutotestCommandChannel.TickMainThread();
        TryExecutePendingPotionRewardClaim();
        TryExecutePendingRewardCardSelection();
        TryExecutePendingTreasureRelicClaim();
        TryExecutePendingMapNodeSelection();
        TryExecutePendingCombatActionConfirmation();
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
            Logger.Warning($"??る??類ㅼ뵥 餓???됱뇚揶쎛 獄쏆뮇源??됰뮸??덈뼄. ??쇱벉 雅뚯눊由?癒?퐣 ??쇰뻻 ??뺣즲??몃빍?? {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void TryStartClaimRequest()
    {
        bool waitingForAdapterCardSelectionInput = pendingCombatActionConfirmation is not null
            && AdapterCardSelectionBridge.HasPendingSelection;
        if (pendingRewardCardSelection is not null
            || pendingPotionRewardClaim is not null
            || pendingTreasureRelicClaim is not null
            || pendingMapNodeSelection is not null
            || (pendingCombatActionConfirmation is not null && !waitingForAdapterCardSelectionInput))
        {
            return;
        }

        long nowMs = Environment.TickCount64;
        if (Volatile.Read(ref claimInFlight) == 1)
        {
            long startedAtMs = Interlocked.Read(ref claimStartedAtMs);
            if (startedAtMs > 0 && nowMs - startedAtMs > ClaimInFlightWatchdogMs)
            {
                Volatile.Write(ref claimInFlight, 0);
                Interlocked.Exchange(ref claimStartedAtMs, 0);
                Logger.Warning("??곸읈 claim ?遺욧퍕????댭???살삋 ??멸돌筌왖 ??녿툡 ??疫??怨밴묶????곸젫??됰뮸??덈뼄.");
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
            LogDiagnostic("claim ??疫? ?됰슢?곻쭪????源껊궗?怨몄몵嚥?野껊슣????袁る떮 ?怨밴묶揶쎛 ?袁⑹춦 ??곷뮸??덈뼄.");
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
                $"claim ??疫? ?袁⑹삺 ?袁る떮 ?怨밴묶?? ?됰슢?곻쭪? 野껊슣???怨밴묶揶쎛 ??살キ??덈뼄. ?袁⑹삺 ?怨밴묶????쇰뻻 野껊슣???됰뮸??덈뼄. current={context.StateId ?? "<none>"} posted={postedState.StateId}");
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
                    LogDiagnostic("claim ?臾먮뼗 ??곸벉: ?됰슢?곻쭪? ??쇱젟????쑵??源딆넅??뤿선 ??뉕탢???遺욧퍕????몄셽??뤿???щ빍??");
                }
                else if (response.Status == "none")
                {
                    LogDiagnostic($"claim ?臾먮뼗: ??쎈뻬????る????곷뮸??덈뼄. state_version={postedState.StateVersion}");
                }

                if (response?.Status == "claimed" && response.Action is not null)
                {
                    lock (SyncRoot)
                    {
                        pendingClaim = new PendingClaim(response.Action, postedState);
                    }

                    Logger.Info($"??곕짗 claim ?源껊궗: {response.Action.SelectedActionId} ({response.Action.SubmissionId})");
                }
                else if (response is not null && response.Status is "stale" or "unsupported")
                {
                    Logger.Info($"??곕짗 claim????쎈뻬 ??곸뵠 ?ル굝利??뤿???щ빍?? {response.Status} {response.Reason}");
                }
            }
            catch (Exception exception)
            {
                Logger.Warning($"??곕짗 claim ?遺욧퍕????쎈솭??됰뮸??덈뼄. 野껊슣??筌욊쑵六?? 筌롫뜆?쏉쭪? ??녿뮸??덈뼄. {exception.GetType().Name}: {exception.Message}");
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
            ReportResult(claim, "ignored_duplicate", "??? 筌ｌ꼶???submission_id????쎈뻬??? ??녿릭??щ빍??");
            return;
        }

        CombatActionContextSnapshot context = CombatActionRuntimeContext.GetSnapshot();
        if (context.CombatRoot is null)
        {
            RememberExecuted(claim.Action.SubmissionId);
            string result = IsTerminalContext(context) ? "terminal_transition" : "stale";
            ReportResult(claim, result, IsTerminalContext(context)
                ? "?ㅽ뻾 吏곸쟾 醫낅즺 ?곹깭濡??꾪솚?섏뿀?듬땲?? terminal_phase=game_over"
                : "??쎈뻬 筌욊낯???怨밴묶揶쎛 claim???怨밴묶?? ??살キ??덈뼄.");
            return;
        }

        LegalActionSnapshot? legalAction = context.FindAction(claim.Action.SelectedActionId);
        bool stateMatchesClaim = context.StateId == claim.PostedState.StateId;
        if (!stateMatchesClaim && !CanExecuteAcrossFreshCombatState(legalAction))
        {
            RememberExecuted(claim.Action.SubmissionId);
            string result = IsTerminalContext(context) ? "terminal_transition" : "stale";
            ReportResult(claim, result, IsTerminalContext(context)
                ? "?ㅽ뻾 吏곸쟾 醫낅즺 ?곹깭濡??꾪솚?섏뿀?듬땲?? terminal_phase=game_over"
                : "??쎈뻬 筌욊낯???怨밴묶揶쎛 claim???怨밴묶?? ??살キ??덈뼄.");
            return;
        }

        if (legalAction is null)
        {
            RememberExecuted(claim.Action.SubmissionId);
            string result = IsTerminalContext(context) ? "terminal_transition" : "stale";
            ReportResult(claim, result, IsTerminalContext(context)
                ? "?ㅽ뻾 吏곸쟾 醫낅즺 ?곹깭濡??꾪솚?섏뿀?듬땲?? terminal_phase=game_over"
                : "?袁⑹삺 legal_actions?癒?퐣 selected_action_id??筌≪뼚? 筌륁궢六??щ빍??");
            return;
        }

        if (!IsSupportedActionType(legalAction.ActionType))
        {
            RememberExecuted(claim.Action.SubmissionId);
            ReportResult(claim, "unsupported", $"{legalAction.ActionType} ??곕짗?? ?袁⑹춦 ??쎈뻬疫꿸퀗? 筌왖?癒곕릭筌왖 ??녿뮸??덈뼄.");
            return;
        }

        try
        {
            string detail;
            bool applied;
            string failedResult = "failed";
            if (legalAction.ActionType.Equals("end_turn", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteEndTurn(context.CombatRoot, out detail);
                if (applied)
                {
                    pendingCombatActionConfirmation = new PendingCombatActionConfirmation(
                        legalAction,
                        claim,
                        context.StateId,
                        detail,
                        Environment.TickCount64);
                    Logger.Info($"?袁る떮 ??곕짗 ??낆젾 ???怨밴묶 癰궰?遺? 疫꿸퀡?롧뵳?덈빍?? action={legalAction.ActionId}, detail={detail}");
                    return;
                }
            }
            else if (legalAction.ActionType.Equals("play_card", StringComparison.OrdinalIgnoreCase))
            {
                IDisposable? cardSelectorScope = AdapterCardSelectionBridge.InstallSelectorForQueuedCardAction();
                applied = TryExecutePlayCard(legalAction, context.CombatRoot, out detail);
                if (applied)
                {
                    CombatStateExporter.RememberCardSelectionSourceHint(
                        legalAction.CardId,
                        legalAction.CardName,
                        legalAction.CardUpgraded);

                    PendingCombatActionConfirmation pendingConfirmation = new(
                        legalAction,
                        claim,
                        context.StateId,
                        detail,
                        Environment.TickCount64);
                    pendingConfirmation.ActiveCardSelectorScope = cardSelectorScope;
                    pendingCombatActionConfirmation = pendingConfirmation;
                    Logger.Info($"?袁る떮 ??곕짗 ??낆젾 ???怨밴묶 癰궰?遺? 疫꿸퀡?롧뵳?덈빍?? action={legalAction.ActionId}, detail={detail}");
                    return;
                }

                cardSelectorScope?.Dispose();
            }
            else if (legalAction.ActionType.Equals("use_potion", StringComparison.OrdinalIgnoreCase))
            {
                IDisposable? cardSelectorScope = AdapterCardSelectionBridge.InstallSelectorForQueuedCardAction();
                applied = TryExecuteUsePotion(legalAction, context.CombatRoot, out detail);
                if (applied)
                {
                    PendingCombatActionConfirmation pendingConfirmation = new(
                        legalAction,
                        claim,
                        context.StateId,
                        detail,
                        Environment.TickCount64);
                    pendingConfirmation.ActiveCardSelectorScope = cardSelectorScope;
                    pendingCombatActionConfirmation = pendingConfirmation;
                    Logger.Info($"?袁る떮 ??곕짗 ??낆젾 ???怨밴묶 癰궰?遺? 疫꿸퀡?롧뵳?덈빍?? action={legalAction.ActionId}, detail={detail}");
                    return;
                }

                cardSelectorScope?.Dispose();
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
            else if (legalAction.ActionType.Equals("claim_treasure_relic", StringComparison.OrdinalIgnoreCase)
                || legalAction.ActionType.Equals("choose_treasure_relic", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteClaimTreasureRelic(legalAction, context.CombatRoot, claim, out detail, out bool resultDeferred);
                if (applied && resultDeferred)
                {
                    Logger.Info(detail);
                    return;
                }
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
                applied = TryExecuteCardSelectionChoice(legalAction, context.CombatRoot, out detail, out failedResult);
            }
            else if (legalAction.ActionType.Equals("confirm_card_selection", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteCardSelectionConfirm(legalAction, context.CombatRoot, out detail, out failedResult);
            }
            else if (legalAction.ActionType.Equals("cancel_card_selection", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteCardSelectionCancel(context.CombatRoot, out detail);
            }
            else if (legalAction.ActionType.Equals("continue_run", StringComparison.OrdinalIgnoreCase))
            {
                applied = AutotestCommandChannel.TryStartContinueRunFromLegalAction(
                    claim.Action.SubmissionId,
                    out detail);
            }
            else if (legalAction.ActionType.Equals("start_new_run", StringComparison.OrdinalIgnoreCase))
            {
                applied = AutotestCommandChannel.TryStartNewRunFromLegalAction(
                    claim.Action.SubmissionId,
                    out detail);
            }
            else if (legalAction.ActionType.Equals("dismiss_game_over", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteDismissGameOver(out detail);
            }
            else if (legalAction.ActionType.Equals("choose_map_node", StringComparison.OrdinalIgnoreCase))
            {
                applied = TryExecuteMapNodeSelection(legalAction, context.CombatRoot, claim, out detail, out bool resultDeferred);
                if (applied && resultDeferred)
                {
                    Logger.Info(detail);
                    return;
                }
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
            ReportResult(claim, failedResult, detail);
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
            || actionType.Equals("claim_potion_reward_with_discard", StringComparison.OrdinalIgnoreCase)
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
            || actionType.Equals("choose_treasure_relic", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("proceed_treasure", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("buy_shop_item", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("remove_card_at_shop", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("choose_card_selection", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("confirm_card_selection", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("cancel_card_selection", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("continue_run", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("start_new_run", StringComparison.OrdinalIgnoreCase)
            || actionType.Equals("dismiss_game_over", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryExecuteDismissGameOver(out string detail)
    {
        object? gameOverScreen = GetCurrentScreen();
        if (!IsGameOverScreen(gameOverScreen))
        {
            detail = $"현재 화면이 게임 오버 화면이 아닙니다. screen={gameOverScreen?.GetType().FullName ?? "<none>"}";
            return false;
        }

        object? continueButton = ReadNamedMember(gameOverScreen, "_continueButton")
            ?? ReadNamedMember(gameOverScreen, "continueButton")
            ?? ReadNamedMember(gameOverScreen, "ContinueButton");
        object? mainMenuButton = ReadNamedMember(gameOverScreen, "_mainMenuButton")
            ?? ReadNamedMember(gameOverScreen, "mainMenuButton")
            ?? ReadNamedMember(gameOverScreen, "MainMenuButton")
            ?? ReadNamedMember(gameOverScreen, "_returnToMainMenuButton")
            ?? ReadNamedMember(gameOverScreen, "returnToMainMenuButton")
            ?? ReadNamedMember(gameOverScreen, "ReturnToMainMenuButton");
        object? button = IsUsableButton(continueButton) ? continueButton : mainMenuButton;
        string buttonLabel = ReferenceEquals(button, continueButton) ? "계속" : "메인 메뉴";
        if (button is null)
        {
            detail = "게임 오버 화면에서 실행 가능한 계속/메인 메뉴 버튼을 찾지 못했습니다.";
            return false;
        }

        if (!IsUsableButton(button))
        {
            detail = $"게임 오버 {buttonLabel} 버튼이 보이지 않거나 비활성화되어 있습니다.";
            return false;
        }

        foreach (string methodName in new[] { "CallReleaseLogic", "ForceClick", "OnRelease", "OnPressed" })
        {
            if (TryInvokeMethod(button, methodName, out _))
            {
                detail = $"게임 오버 {buttonLabel} 버튼을 실행했습니다. method={methodName}";
                return true;
            }
        }

        detail = $"게임 오버 {buttonLabel} 버튼에서 실행 가능한 release 메서드를 찾지 못했습니다.";
        return false;
    }

    private static bool IsUsableButton(object? button)
    {
        if (button is null)
        {
            return false;
        }

        bool? visible = ReadBool(button, "Visible")
            ?? ReadBool(button, "visible")
            ?? ReadBool(button, "_visible");
        bool? visibleInTree = ReadBool(button, "VisibleInTree")
            ?? ReadBool(button, "visibleInTree")
            ?? ReadBool(button, "_visibleInTree");
        bool? isEnabled = ReadBool(button, "IsEnabled")
            ?? ReadBool(button, "isEnabled")
            ?? ReadBool(button, "_isEnabled");
        return visible != false && visibleInTree != false && isEnabled != false;
    }

    private static bool IsGameOverScreen(object? source)
    {
        if (source is null)
        {
            return false;
        }

        string typeName = source.GetType().FullName ?? source.GetType().Name;
        return typeName.Contains("NGameOverScreen", StringComparison.OrdinalIgnoreCase)
            || typeName.Contains("GameOverScreen", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanExecuteAcrossFreshCombatState(LegalActionSnapshot? legalAction)
    {
        if (legalAction is null)
        {
            return false;
        }

        return legalAction.ActionType.Equals("play_card", StringComparison.OrdinalIgnoreCase)
            || legalAction.ActionType.Equals("use_potion", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalContext(CombatActionContextSnapshot context)
    {
        if (context.StateId.StartsWith("game_over_", StringComparison.OrdinalIgnoreCase)
            || context.StateId.StartsWith("run_finished_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(context.CombatStateJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(context.CombatStateJson);
            if (document.RootElement.TryGetProperty("phase", out JsonElement phase)
                && phase.ValueKind == JsonValueKind.String)
            {
                string? phaseText = phase.GetString();
                return phaseText is "game_over" or "run_finished";
            }
        }
        catch
        {
            return false;
        }

        return false;
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
            detail = $"筌뤴뫀?뤻겫??醫뤾문筌왖???袁⑹삺 ?遺얇늺?癒?퐣 ??쎈뻬??????곷뮸??덈뼄. root={rootTypeName}";
            return false;
        }

        if (legalAction.RestOptionIndex is null)
        {
            detail = "choose_rest_site_option?癒?뮉 rest_option_index揶쎛 ?袁⑹뒄??몃빍??";
            return false;
        }

        object? optionButton = FindRestSiteOptionButton(restSiteRoom, legalAction.RestOptionIndex.Value);
        if (optionButton is null)
        {
            detail = $"筌뤴뫀?뤻겫??醫뤾문 甕곌쑵???筌≪뼚? 筌륁궢六??щ빍?? rest_option_id={legalAction.RestOptionId ?? "<none>"}, index={legalAction.RestOptionIndex.Value}";
            return false;
        }

        object? option = ReadNamedMember(optionButton, "Option");
        if (ReadNamedMember(option, "IsEnabled") is bool isEnabled && !isEnabled)
        {
            detail = $"筌뤴뫀?뤻겫??醫뤾문筌왖揶쎛 ??쑵????怨밴묶??낅빍?? rest_option_id={legalAction.RestOptionId ?? "<none>"}, index={legalAction.RestOptionIndex.Value}";
            return false;
        }

        if (!TryInvokeMethod(optionButton, "OnRelease", out _))
        {
            detail = $"筌뤴뫀?뤻겫??醫뤾문 甕곌쑵???紐꾪뀱????쎈솭??됰뮸??덈뼄. rest_option_id={legalAction.RestOptionId ?? "<none>"}, index={legalAction.RestOptionIndex.Value}";
            return false;
        }

        detail = $"筌뤴뫀?뤻겫??醫뤾문筌왖???ⓥ뫀???щ빍?? rest_option_id={legalAction.RestOptionId ?? "<none>"}, index={legalAction.RestOptionIndex.Value}";
        Logger.Info(detail);
        return true;
    }

    private static bool TryExecuteRestSiteProceed(object restSiteRoot, out string detail)
    {
        object? restSiteRoom = ResolveRestSiteRoom(restSiteRoot);
        if (restSiteRoom is null)
        {
            string rootTypeName = restSiteRoot.GetType().FullName ?? restSiteRoot.GetType().Name;
            detail = $"筌뤴뫀?뤻겫?筌욊쑵六?甕곌쑵??? ?袁⑹삺 ?遺얇늺?癒?퐣 ??쎈뻬??????곷뮸??덈뼄. root={rootTypeName}";
            return false;
        }

        object? proceedButton = ReadNamedMember(restSiteRoom, "ProceedButton")
            ?? ReadNamedMember(restSiteRoom, "_proceedButton");
        if (proceedButton is null)
        {
            detail = "筌뤴뫀?뤻겫?筌욊쑵六?甕곌쑵???筌≪뼚? 筌륁궢六??щ빍??";
            return false;
        }

        if (ReadNamedMember(proceedButton, "IsEnabled") is bool isEnabled && !isEnabled)
        {
            detail = "筌뤴뫀?뤻겫?筌욊쑵六?甕곌쑵????袁⑹춦 ??쑵????怨밴묶??낅빍??";
            return false;
        }

        if (!TryInvokeMethod(restSiteRoom, "OnProceedButtonReleased", out _, proceedButton))
        {
            detail = "筌뤴뫀?뤻겫?筌욊쑵六?甕곌쑵???紐꾪뀱????쎈솭??됰뮸??덈뼄.";
            return false;
        }

        detail = "筌뤴뫀?뤻겫?筌욊쑵六?甕곌쑵????????щ빍??";
        Logger.Info(detail);
        return true;
    }

    private static bool TryExecuteShopProceed(object shopRoot, out string detail)
    {
        object? shopRoom = ResolveShopRoom(shopRoot);
        if (shopRoom is null)
        {
            string rootTypeName = shopRoot.GetType().FullName ?? shopRoot.GetType().Name;
            detail = $"?怨몄젎 筌욊쑵六?甕곌쑵??? ?袁⑹삺 ?遺얇늺?癒?퐣 ??쎈뻬??????곷뮸??덈뼄. root={rootTypeName}";
            return false;
        }

        object? proceedButton = ReadNamedMember(shopRoom, "ProceedButton")
            ?? ReadNamedMember(shopRoom, "_proceedButton")
            ?? ReadNamedMember(shopRoom, "ContinueButton")
            ?? ReadNamedMember(shopRoom, "_continueButton");
        if (proceedButton is null)
        {
            detail = "?怨몄젎 筌욊쑵六?甕곌쑵???筌≪뼚? 筌륁궢六??щ빍??";
            return false;
        }

        if (ReadNamedMember(proceedButton, "IsEnabled") is bool isEnabled && !isEnabled)
        {
            detail = "?怨몄젎 筌욊쑵六?甕곌쑵????袁⑹춦 ??쑵????怨밴묶??낅빍??";
            return false;
        }

        if (TryCallDeferredNoArgs(proceedButton, "ForceClick", out string deferredFailureReason))
        {
            if (WaitForShopProceedTransitionToMap())
            {
                detail = "?怨몄젎 筌욊쑵六?甕곌쑵????????筌왖???遺얇늺 筌욊쑴????類ㅼ뵥??됰뮸??덈뼄. method=button.CallDeferred(ForceClick)";
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
                detail = $"?怨몄젎 筌욊쑵六?甕곌쑵????????筌왖???遺얇늺 筌욊쑴????類ㅼ뵥??됰뮸??덈뼄. method={label}";
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
        detail = $"?怨몄젎 筌욊쑵六?甕곌쑵???紐꾪뀱 ??쇰퓠??筌왖???遺얇늺???袁⑤뼎??? 筌륁궢六??щ빍?? tried={tried}, current_screen={currentScreenTypeName}";
        return false;
    }

    private static bool TryExecuteOpenTreasureChest(object treasureRoot, out string detail)
    {
        object? treasureRoom = ResolveTreasureRoom(treasureRoot);
        if (treasureRoom is null)
        {
            string rootTypeName = treasureRoot.GetType().FullName ?? treasureRoot.GetType().Name;
            detail = $"癰귣?窺?怨몄쁽 ??용┛???袁⑹삺 ?遺얇늺?癒?퐣 ??쎈뻬??????곷뮸??덈뼄. root={rootTypeName}";
            return false;
        }

        if (IsTreasureChestOpened(treasureRoom))
        {
            detail = "癰귣?窺?怨몄쁽????? ?????怨밴묶??낅빍??";
            return true;
        }

        object? chestButton = FindTreasureChestButton(treasureRoom);
        if (chestButton is null)
        {
            detail = "癰귣?窺?怨몄쁽 Chest 甕곌쑵???筌≪뼚? 筌륁궢六??щ빍??";
            return false;
        }

        if (ReadNamedMember(chestButton, "IsEnabled") is bool isEnabled && !isEnabled)
        {
            detail = "癰귣?窺?怨몄쁽 Chest 甕곌쑵????袁⑹춦 ??쑵????怨밴묶??낅빍??";
            return false;
        }

        List<string> invokedCandidates = new();
        if (TryCallDeferredNoArgs(chestButton, "ForceClick", out string deferredFailureReason))
        {
            if (WaitForTreasureChestOpen(treasureRoom))
            {
                detail = "癰귣?窺?怨몄쁽????욱?癰귣똻湲?UI 筌욊쑴????類ㅼ뵥??됰뮸??덈뼄. method=button.CallDeferred(ForceClick)";
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
                detail = $"癰귣?窺?怨몄쁽????욱?癰귣똻湲?UI 筌욊쑴????類ㅼ뵥??됰뮸??덈뼄. method={label}";
                Logger.Info(detail);
                return true;
            }
        }

        string currentScreenTypeName = GetCurrentScreenTypeName();
        string tried = invokedCandidates.Count == 0 ? "<none>" : string.Join(", ", invokedCandidates);
        detail = $"癰귣?窺?怨몄쁽 甕곌쑵???紐꾪뀱 ??癰귣똻湲?UI 筌욊쑴????類ㅼ뵥??? 筌륁궢六??щ빍?? tried={tried}, current_screen={currentScreenTypeName}, opened={IsTreasureChestOpened(treasureRoom)}";
        return false;
    }

    private static bool TryExecuteClaimTreasureRelic(
        LegalActionSnapshot legalAction,
        object treasureRoot,
        PendingClaim claim,
        out string detail,
        out bool resultDeferred)
    {
        resultDeferred = false;
        object? treasureRoom = ResolveTreasureRoom(treasureRoot);
        if (treasureRoom is null)
        {
            string rootTypeName = treasureRoot.GetType().FullName ?? treasureRoot.GetType().Name;
            detail = $"癰귣?窺獄??醫듢???얜굣?? ?袁⑹삺 ?遺얇늺?癒?퐣 ??쎈뻬??????곷뮸??덈뼄. root={rootTypeName}";
            return false;
        }

        if (!IsTreasureRelicCollectionOpen(treasureRoom))
        {
            detail = "癰귣?窺獄??醫듢??醫뤾문 UI揶쎛 ?袁⑹춦 ??????? ??녿뮸??덈뼄.";
            return false;
        }

        if (legalAction.TreasureRelicIndex is null)
        {
            detail = "claim_treasure_relic?癒?뮉 treasure_relic_index揶쎛 ?袁⑹뒄??몃빍??";
            return false;
        }

        object? relicCollection = FindTreasureRelicCollection(treasureRoom);
        if (relicCollection is null)
        {
            detail = "癰귣?窺獄??醫듢??뚎됱젂???紐껊굡??筌≪뼚? 筌륁궢六??щ빍??";
            return false;
        }

        object? holder = FindTreasureRelicHolder(relicCollection, legalAction.TreasureRelicIndex.Value);
        if (holder is null)
        {
            detail = $"癰귣?窺獄??醫듢????遺? 筌≪뼚? 筌륁궢六??щ빍?? index={legalAction.TreasureRelicIndex.Value}, relic_id={legalAction.TreasureRelicId ?? "<none>"}";
            return false;
        }

        if (ReadNamedMember(holder, "IsEnabled") is bool isEnabled && !isEnabled)
        {
            detail = $"癰귣?窺獄??醫듢????遺? ??쑵????怨밴묶??낅빍?? index={legalAction.TreasureRelicIndex.Value}";
            return false;
        }

        string? runtimeRelicId = ReadTreasureRelicHolderModelId(holder);
        if (!string.IsNullOrWhiteSpace(legalAction.TreasureRelicId)
            && !string.IsNullOrWhiteSpace(runtimeRelicId)
            && !IsSameShopModelId(runtimeRelicId, legalAction.TreasureRelicId))
        {
            detail = $"癰귣?窺獄??醫듢??袁⑤궖揶쎛 獄쏅뗀??????щ빍?? expected={legalAction.TreasureRelicId}, actual={runtimeRelicId}, index={legalAction.TreasureRelicIndex.Value}";
            return false;
        }

        List<string> invokedCandidates = new();
        if (TryCallDeferredNoArgs(holder, "ForceClick", out string deferredFailureReason))
        {
            pendingTreasureRelicClaim = new PendingTreasureRelicClaim(
                legalAction,
                claim,
                treasureRoom,
                "holder.CallDeferred(ForceClick)",
                Environment.TickCount64);
            detail = $"蹂대Ъ諛??좊Ъ ?좏깮 ?좏샇瑜??덉빟?섍퀬 ?꾨즺 ?곹깭瑜?湲곕떎由쎈땲?? method=holder.CallDeferred(ForceClick), index={legalAction.TreasureRelicIndex.Value}, relic_id={legalAction.TreasureRelicId ?? "<none>"}";
            resultDeferred = true;
            return true;
        }

        if (deferredFailureReason.Length > 0)
        {
            invokedCandidates.Add($"holder.CallDeferred(ForceClick): {deferredFailureReason}");
        }

        List<(string Label, object? Source, string MethodName, object?[] Args)> candidates = new()
        {
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
            pendingTreasureRelicClaim = new PendingTreasureRelicClaim(
                legalAction,
                claim,
                treasureRoom,
                label,
                Environment.TickCount64);
            detail = $"蹂대Ъ諛??좊Ъ ?좏깮 ?좏샇瑜?蹂대궡怨??꾨즺 ?곹깭瑜?湲곕떎由쎈땲?? method={label}, index={legalAction.TreasureRelicIndex.Value}, relic_id={legalAction.TreasureRelicId ?? "<none>"}";
            resultDeferred = true;
            return true;
        }

        string tried = invokedCandidates.Count == 0 ? "<none>" : string.Join(", ", invokedCandidates);
        detail = $"癰귣?窺獄??醫듢??醫뤾문 ?紐꾪뀱 ???袁⑥┷ ?怨밴묶???類ㅼ뵥??? 筌륁궢六??щ빍?? tried={tried}, index={legalAction.TreasureRelicIndex.Value}, relic_open={IsTreasureRelicCollectionOpen(treasureRoom)}, proceed_enabled={IsTreasureProceedEnabled(treasureRoom)}";
        return false;
    }

    private static bool TryExecuteTreasureProceed(object treasureRoot, out string detail)
    {
        object? treasureRoom = ResolveTreasureRoom(treasureRoot);
        if (treasureRoom is null)
        {
            string rootTypeName = treasureRoot.GetType().FullName ?? treasureRoot.GetType().Name;
            detail = $"癰귣?窺獄?筌욊쑵六?? ?袁⑹삺 ?遺얇늺?癒?퐣 ??쎈뻬??????곷뮸??덈뼄. root={rootTypeName}";
            return false;
        }

        object? proceedButton = ReadNamedMember(treasureRoom, "ProceedButton")
            ?? ReadNamedMember(treasureRoom, "_proceedButton")
            ?? ReadNamedMember(treasureRoom, "proceedButton");
        if (proceedButton is null)
        {
            detail = "癰귣?窺獄?筌욊쑵六?甕곌쑵???筌≪뼚? 筌륁궢六??щ빍??";
            return false;
        }

        if (ReadNamedMember(proceedButton, "IsEnabled") is bool isEnabled && !isEnabled)
        {
            detail = "癰귣?窺獄?筌욊쑵六?甕곌쑵????袁⑹춦 ??쑵????怨밴묶??낅빍??";
            return false;
        }

        List<string> invokedCandidates = new();
        if (TryCallDeferredNoArgs(proceedButton, "ForceClick", out string deferredFailureReason))
        {
            if (WaitForTreasureProceedTransitionToMap())
            {
                detail = "癰귣?窺獄?筌욊쑵六?甕곌쑵????????筌왖???遺얇늺 筌욊쑴????類ㅼ뵥??됰뮸??덈뼄. method=button.CallDeferred(ForceClick)";
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
                detail = $"癰귣?窺獄?筌욊쑵六?甕곌쑵????????筌왖???遺얇늺 筌욊쑴????類ㅼ뵥??됰뮸??덈뼄. method={label}";
                Logger.Info(detail);
                return true;
            }
        }

        string currentScreenTypeName = GetCurrentScreenTypeName();
        string tried = invokedCandidates.Count == 0 ? "<none>" : string.Join(", ", invokedCandidates);
        detail = $"癰귣?窺獄?筌욊쑵六?甕곌쑵???紐꾪뀱 ??筌왖???遺얇늺 筌욊쑴????類ㅼ뵥??? 筌륁궢六??щ빍?? tried={tried}, current_screen={currentScreenTypeName}";
        return false;
    }

    private static void TryExecutePendingTreasureRelicClaim()
    {
        PendingTreasureRelicClaim? pending = pendingTreasureRelicClaim;
        if (pending is null)
        {
            return;
        }

        long nowMs = Environment.TickCount64;
        bool completed = IsTreasureProceedEnabled(pending.TreasureRoom);
        if (completed)
        {
            pending.StableCompletionPollCount++;
            if (pending.StableCompletionPollCount < 3)
            {
                return;
            }

            pendingTreasureRelicClaim = null;
            string detail = $"蹂대Ъ諛??좊Ъ ?띾뱷 ?꾨즺瑜??뺤씤?덉뒿?덈떎. method={pending.MethodLabel}, index={pending.LegalAction.TreasureRelicIndex}, relic_id={pending.LegalAction.TreasureRelicId ?? "<none>"}";
            Logger.Info(detail);
            RememberExecuted(pending.Claim.Action.SubmissionId);
            ReportResult(pending.Claim, "applied", detail);
            return;
        }

        pending.StableCompletionPollCount = 0;
        if (nowMs - pending.StartedAtMs <= 10000)
        {
            return;
        }

        pendingTreasureRelicClaim = null;
        string timeoutDetail = $"蹂대Ъ諛??좊Ъ ?띾뱷 ?꾨즺瑜??쒗븳 ?쒓컙 ?덉뿉 ?뺤씤?섏? 紐삵뻽?듬땲?? method={pending.MethodLabel}, index={pending.LegalAction.TreasureRelicIndex}, relic_open={IsTreasureRelicCollectionOpen(pending.TreasureRoom)}, proceed_enabled={IsTreasureProceedEnabled(pending.TreasureRoom)}";
        Logger.Warning(timeoutDetail);
        RememberExecuted(pending.Claim.Action.SubmissionId);
        ReportResult(pending.Claim, "failed", timeoutDetail);
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
            detail = $"?怨몄젎 ?닌됤꼻???袁⑹삺 ?遺얇늺?癒?퐣 ??쎈뻬??????곷뮸??덈뼄. root={rootTypeName}";
            return false;
        }

        object? runtimeInventory = ResolveRuntimeMerchantInventory(shopRoom)
            ?? ResolveRuntimeMerchantInventoryFromRunManager();
        if (runtimeInventory is null)
        {
            detail = "?怨몄젎 ?닌됤꼻???袁⑹뒄??MerchantInventory??筌≪뼚? 筌륁궢六??щ빍??";
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
            detail = $"?怨몄젎 ?얠눛萸????? ??됱쟿??낅빍?? model_id={candidate.ModelId}, cost={candidate.Cost?.ToString() ?? "<unknown>"}";
            return false;
        }

        if (!candidate.IsAffordable)
        {
            detail = $"?怨몄젎 ?얠눛萸?????ⓥ뫀諭뜹첎? ?봔鈺곌퉲鍮??덈뼄. model_id={candidate.ModelId}, cost={candidate.Cost?.ToString() ?? "<unknown>"}";
            return false;
        }

        if (!TryInvokePurchaseWrapper(candidate.Entry, runtimeInventory, waitForCompletion: true, out object? purchaseResult, out string purchaseDetail))
        {
            detail = purchaseDetail;
            return false;
        }

        detail = $"?怨몄젎 ?닌됤꼻 ?袁⑥┷: kind={candidate.Kind}, model_id={candidate.ModelId}, cost={candidate.Cost?.ToString() ?? "<unknown>"} result={DescribeResult(purchaseResult)}";
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
            detail = $"燁삳?諭???볤탢 ??뺥돩??삳뮉 ?袁⑹삺 ?遺얇늺?癒?퐣 ??쎈뻬??????곷뮸??덈뼄. root={rootTypeName}";
            return false;
        }

        object? runtimeInventory = ResolveRuntimeMerchantInventory(shopRoom)
            ?? ResolveRuntimeMerchantInventoryFromRunManager();
        if (runtimeInventory is null)
        {
            detail = "燁삳?諭???볤탢 ??뺥돩??쇰퓠 ?袁⑹뒄??MerchantInventory??筌≪뼚? 筌륁궢六??щ빍??";
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
            detail = $"燁삳?諭???볤탢 ??뺥돩??? ??? ????癒?탢????쑵??源딆넅?癒?뮸??덈뼄. cost={candidate.Cost?.ToString() ?? "<unknown>"}";
            return false;
        }

        if (!candidate.IsAffordable)
        {
            detail = $"燁삳?諭???볤탢 ??뺥돩??? ??곸뒠???ⓥ뫀諭뜹첎? ?봔鈺곌퉲鍮??덈뼄. cost={candidate.Cost?.ToString() ?? "<unknown>"}";
            return false;
        }

        if (!TryInvokePurchaseWrapper(candidate.Entry, runtimeInventory, waitForCompletion: false, out object? purchaseResult, out string purchaseDetail))
        {
            detail = purchaseDetail;
            return false;
        }

        detail = $"燁삳?諭???볤탢 ??뺥돩???醫뤾문 ?袁⑥┷: cost={candidate.Cost?.ToString() ?? "<unknown>"} result={DescribeResult(purchaseResult)}. ??쇱벉 ?怨밴묶?癒?퐣 ??볤탢??燁삳?諭띄몴?choose_card_selection??곗쨮 ?醫뤾문??곷튊 ??몃빍??";
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

        if (!string.IsNullOrWhiteSpace(legalAction.ShopSlotGroup))
        {
            matches = matches.Where(candidate => IsSameShopSlotGroup(candidate, legalAction.ShopSlotGroup));
        }

        if (legalAction.ShopSlotIndex is not null)
        {
            matches = matches.Where(candidate => IsSameShopSlotIndex(candidate, legalAction.ShopSlotIndex.Value));
        }

        if (!string.IsNullOrWhiteSpace(legalAction.ShopLocatorId))
        {
            matches = matches.Where(candidate => IsSameShopLocator(candidate, legalAction.ShopLocatorId));
        }

        List<ShopPurchaseCandidate> exactMatches = matches.ToList();
        if (exactMatches.Count == 0)
        {
            string candidateSummary = string.Join(", ", candidates.Take(8).Select(candidate =>
                $"{candidate.Kind}:{candidate.ModelId}:{candidate.Cost?.ToString() ?? "<null>"}:{candidate.RawSlotGroup}:{candidate.RawSlotIndex}/{candidate.SlotGroup}:{candidate.SlotIndex}"));
            detail = $"?怨몄젎 ?닌됤꼻 ?袁⑤궖??筌≪뼚? 筌륁궢六??щ빍?? kind={legalAction.ShopKind ?? "<none>"}, model_id={legalAction.ShopModelId ?? "<none>"}, cost={legalAction.ShopCost?.ToString() ?? "<none>"}, slot={legalAction.ShopSlotGroup ?? "<none>"}:{legalAction.ShopSlotIndex?.ToString() ?? "<none>"}, locator={legalAction.ShopLocatorId ?? "<none>"}, candidates=[{candidateSummary}]";
            return null;
        }

        if (exactMatches.Count == 1)
        {
            detail = "?怨몄젎 ?닌됤꼻 ?袁⑤궖??筌≪뼚釉??щ빍??";
            return exactMatches[0];
        }

        if (legalAction.ShopSlotIndex is not null)
        {
            ShopPurchaseCandidate? slotMatch = exactMatches.FirstOrDefault(candidate => IsSameShopSlotIndex(candidate, legalAction.ShopSlotIndex.Value));
            if (slotMatch is not null)
            {
                detail = "?怨몄젎 ?닌됤꼻 ?袁⑤궖??????甕곕뜇?뉑에??類ㅼ젟??됰뮸??덈뼄.";
                return slotMatch;
            }
        }

        detail = $"?怨몄젎 ?닌됤꼻 ?袁⑤궖揶쎛 ????揶쏆뮇???덈뼄. model_id={legalAction.ShopModelId ?? "<none>"}, count={exactMatches.Count}";
        return null;
    }

    private static bool IsSameShopSlotGroup(ShopPurchaseCandidate candidate, string expectedSlotGroup)
    {
        return candidate.SlotGroup.Equals(expectedSlotGroup, StringComparison.OrdinalIgnoreCase)
            || candidate.RawSlotGroup.Equals(expectedSlotGroup, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameShopSlotIndex(ShopPurchaseCandidate candidate, int expectedSlotIndex)
    {
        return candidate.SlotIndex == expectedSlotIndex
            || candidate.RawSlotIndex == expectedSlotIndex;
    }

    private static bool IsSameShopLocator(ShopPurchaseCandidate candidate, string expectedLocatorId)
    {
        return $"{candidate.SlotGroup}:{candidate.SlotIndex}".Equals(expectedLocatorId, StringComparison.OrdinalIgnoreCase)
            || $"{candidate.RawSlotGroup}:{candidate.RawSlotIndex}".Equals(expectedLocatorId, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<ShopPurchaseCandidate> EnumerateShopPurchaseCandidates(object runtimeInventory)
    {
        foreach (ShopPurchaseCandidate candidate in EnumerateCardPurchaseCandidates(runtimeInventory, "CharacterCardEntries", "character_card"))
        {
            yield return candidate;
        }

        foreach (ShopPurchaseCandidate candidate in EnumerateCardPurchaseCandidates(runtimeInventory, "ColorlessCardEntries", "colorless_card"))
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
            detail = "?怨몄젎 燁삳?諭???볤탢 ?酉?껆뵳?? 筌≪뼚? 筌륁궢六??щ빍??";
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
            detail = $"燁삳?諭???볤탢 ??뺥돩??揶쎛野꺿뫗???袁⑹삺 ?怨밴묶?? ??살キ??덈뼄. expected={legalAction.ShopCost.Value}, actual={candidate.Cost?.ToString() ?? "<unknown>"}";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(legalAction.ShopSlotGroup)
            && !IsSameShopSlotGroup(candidate, legalAction.ShopSlotGroup))
        {
            detail = $"燁삳?諭???볤탢 ??뺥돩??????域밸챶竊???袁⑹삺 ?怨밴묶?? ??살キ??덈뼄. expected={legalAction.ShopSlotGroup}, actual={candidate.SlotGroup}";
            return null;
        }

        if (legalAction.ShopSlotIndex is not null
            && !IsSameShopSlotIndex(candidate, legalAction.ShopSlotIndex.Value))
        {
            detail = $"燁삳?諭???볤탢 ??뺥돩??????甕곕뜇?뉐첎? ?袁⑹삺 ?怨밴묶?? ??살キ??덈뼄. expected={legalAction.ShopSlotIndex.Value}, actual={candidate.SlotIndex}";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(legalAction.ShopLocatorId)
            && !IsSameShopLocator(candidate, legalAction.ShopLocatorId))
        {
            detail = $"燁삳?諭???볤탢 ??뺥돩??locator揶쎛 ?袁⑹삺 ?怨밴묶?? ??살キ??덈뼄. expected={legalAction.ShopLocatorId}, actual={candidate.SlotGroup}:{candidate.SlotIndex}";
            return null;
        }

        detail = "?怨몄젎 燁삳?諭???볤탢 ?酉?껆뵳?? 筌≪뼚釉??щ빍??";
        return candidate;
    }

    private static IEnumerable<ShopPurchaseCandidate> EnumerateCardPurchaseCandidates(
        object runtimeInventory,
        string entriesMemberName,
        string rawSlotGroup)
    {
        object? entries = ReadNamedMember(runtimeInventory, entriesMemberName)
            ?? ReadNamedMember(runtimeInventory, "_" + entriesMemberName)
            ?? ReadNamedMember(runtimeInventory, ToCamelCase(entriesMemberName));
        int rawIndex = 0;
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
                    slotGroup: rawSlotGroup,
                    slotIndex: rawIndex,
                    rawSlotGroup: rawSlotGroup,
                    rawSlotIndex: rawIndex);
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
            detail = $"?怨몄젎 ?닌됤꼻 筌롫뗄苑???紐꾪뀱????쎈솭??됰뮸??덈뼄. entry={entry.GetType().FullName ?? entry.GetType().Name}, methods=[{DescribePurchaseMethods(entry)}]";
            return false;
        }

        result = invocationResult;
        if (!waitForCompletion)
        {
            detail = "?怨몄젎 ?닌됤꼻/??뺥돩??筌롫뗄苑??? ?紐꾪뀱??뉙???쑬猷욄묾??袁⑥┷ ??疫꿸퀡????몄셽??됰뮸??덈뼄.";
            return true;
        }

        if (invocationResult is Task<bool> booleanTask)
        {
            result = booleanTask.GetAwaiter().GetResult();
            if (result is bool ok && !ok)
            {
                detail = "?怨몄젎 ?닌됤꼻 筌롫뗄苑??? false??獄쏆꼹???됰뮸??덈뼄.";
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
            detail = "?怨몄젎 ?닌됤꼻 筌롫뗄苑??? false??獄쏆꼹???됰뮸??덈뼄.";
            return false;
        }

        detail = "?怨몄젎 ?닌됤꼻 筌롫뗄苑??? ?紐꾪뀱??됰뮸??덈뼄.";
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
        out string detail,
        out string resultStatus)
    {
        detail = string.Empty;
        resultStatus = "failed";
        CardSelectionActionStatus adapterStatus = CardSelectionActionStatus.Failed;
        if (legalAction.CardSelectionIndex is not null
            && AdapterCardSelectionBridge.TryChoose(
                legalAction.CardSelectionIndex.Value,
                legalAction.CardSelectionId,
                legalAction.CardId,
                legalAction.CardName,
                legalAction.CardUpgraded,
                legalAction.CardSelectionPile,
                legalAction.CardSelectionRuntimeId,
                out detail,
                out adapterStatus))
        {
            resultStatus = ToResultStatus(adapterStatus);
            return true;
        }

        if (adapterStatus == CardSelectionActionStatus.Stale && !string.IsNullOrWhiteSpace(legalAction.CardSelectionRuntimeId))
        {
            resultStatus = "stale";
            detail = string.IsNullOrWhiteSpace(detail) ? "카드 선택 액션이 오래되었습니다." : detail;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(legalAction.CardSelectionRuntimeId))
        {
            resultStatus = ToResultStatus(adapterStatus);
            detail = string.IsNullOrWhiteSpace(detail) ? "카드 선택 액션 검증에 실패했습니다." : detail;
            return false;
        }

        if (legalAction.CardSelectionIndex is not null
            && AdapterCardSelectionBridge.TryChoose(
                legalAction.CardSelectionIndex.Value,
                legalAction.CardSelectionId,
                out detail))
        {
            return true;
        }

        object? selectionScreen = ResolveCardSelectionScreen(currentRoot);
        if (selectionScreen is null)
        {
            string rootTypeName = currentRoot.GetType().FullName ?? currentRoot.GetType().Name;
            detail = $"燁삳?諭??醫뤾문 ?遺얇늺??筌≪뼚? 筌륁궢六??щ빍?? root={rootTypeName}";
            return false;
        }

        if (legalAction.CardSelectionIndex is null)
        {
            detail = "choose_card_selection?癒?뮉 card_selection_index揶쎛 ?袁⑹뒄??몃빍??";
            return false;
        }

        List<object> cardHolders = FindGridCardHolders(selectionScreen);
        int selectionIndex = legalAction.CardSelectionIndex.Value;
        if (selectionIndex < 0 || selectionIndex >= cardHolders.Count)
        {
            detail = $"燁삳?諭??醫뤾문 ?紐껊쑔??? ?遺얇늺??燁삳?諭???? 甕곗щ선?????덈뼄. index={selectionIndex}, count={cardHolders.Count}";
            return false;
        }

        object cardHolder = cardHolders[selectionIndex];
        if (ReadBool(cardHolder, "Visible") == false || ReadBool(cardHolder, "visible") == false)
        {
            detail = $"移대뱶 ?좏깮 ??곸씠 ?꾩옱 蹂댁씠吏 ?딆뒿?덈떎. card_selection_id={legalAction.CardSelectionId ?? "<none>"}, index={selectionIndex}";
            return false;
        }
        object? card = ReadCardFromHolder(cardHolder);
        if (card is null)
        {
            detail = $"?醫뤾문??燁삳?諭?筌뤴뫀???筌≪뼚? 筌륁궢六??щ빍?? index={selectionIndex}";
            return false;
        }

        if (!IsSameCardSelectionTarget(legalAction, card, out string targetDetail))
        {
            detail = $"카드 선택 대상이 관찰 시점과 다릅니다. card_selection_id={legalAction.CardSelectionId ?? "<none>"}, index={selectionIndex}, {targetDetail}";
            return false;
        }

        int selectedCountBefore = CountSelectedCards(selectionScreen);
        if (IsCardAlreadySelected(selectionScreen, card))
        {
            detail = $"?대? ?좏깮??移대뱶?낅땲?? card_selection_id={legalAction.CardSelectionId ?? "<none>"}, index={selectionIndex}, {targetDetail}";
            return false;
        }

        if (!TryInvokeCardSelectionHolder(selectionScreen, cardHolder, card))
        {
            detail = $"燁삳?諭??醫뤾문 ?醫륁깈??癰귣?沅∽쭪? 筌륁궢六??щ빍?? card_selection_id={legalAction.CardSelectionId ?? "<none>"}, index={selectionIndex}";
            return false;
        }

        int selectedCountAfter = CountSelectedCards(selectionScreen);
        detail = $"카드 선택을 요청했습니다. card_selection_id={legalAction.CardSelectionId ?? "<none>"}, index={selectionIndex}, selected_count_before={selectedCountBefore}, selected_count_after={selectedCountAfter}, {targetDetail}";
        Logger.Info(detail);
        return true;
    }

    private static string ToResultStatus(CardSelectionActionStatus status)
    {
        return status switch
        {
            CardSelectionActionStatus.Applied => "applied",
            CardSelectionActionStatus.Stale => "stale",
            _ => "failed"
        };
    }

    private static bool TryBeginManualSpecialCardSelection(LegalActionSnapshot legalAction, out string detail)
    {
        detail = string.Empty;
        object? sourceCard = legalAction.CombatCardId is null
            ? null
            : CombatStateExporter.FindLatestRuntimeHandCardByCombatCardId(legalAction.CombatCardId.Value);
        if (sourceCard is null)
        {
            return false;
        }

        object? owner = ReadNamedMember(sourceCard, "Owner")
            ?? ReadNamedMember(sourceCard, "owner")
            ?? ReadNamedMember(sourceCard, "_owner");
        string text = $"{legalAction.CardId ?? string.Empty} {legalAction.CardName ?? string.Empty}";
        if (text.Contains("HEADBUTT", StringComparison.OrdinalIgnoreCase) || text.Contains("박치기", StringComparison.OrdinalIgnoreCase))
        {
            List<object> discardCards = GetPlayerPileCards(owner, "Discard").ToList();
            return AdapterCardSelectionBridge.BeginManualSelection(
                "adapter_headbutt_discard_to_draw",
                discardCards,
                1,
                1,
                selected => MoveSelectedCardsToPile(selected, owner, "Draw", "Top"),
                out detail);
        }

        return false;
    }

    private static bool TryExecuteHeadbuttAdapterManual(LegalActionSnapshot legalAction, object combatRoot, out string detail)
    {
        detail = string.Empty;
        if (legalAction.CombatCardId is null)
        {
            return false;
        }

        object? sourceCard = CombatStateExporter.FindLatestRuntimeHandCardByCombatCardId(legalAction.CombatCardId.Value);
        if (sourceCard is null)
        {
            detail = "박치기 수동 실행 실패: 손패에서 원본 카드를 찾지 못했습니다.";
            return false;
        }

        object? owner = ReadNamedMember(sourceCard, "Owner");
        if (owner is null)
        {
            detail = "박치기 수동 실행 실패: 카드 소유자를 찾지 못했습니다.";
            return false;
        }

        if (!TryResolveCardTarget(legalAction, sourceCard, combatRoot, out object? target, out detail) || target is null)
        {
            return false;
        }

        int damage = ReadBool(sourceCard, "IsUpgraded") == true || legalAction.CardUpgraded == true ? 12 : 9;
        ApplySimpleDamage(target, damage);
        SpendCardEnergy(owner, 1);

        List<object> discardCards = GetPlayerPileCards(owner, "Discard").ToList();
        MoveSelectedCardsToPile(new[] { sourceCard }, owner, "Discard", "Bottom");

        bool selectionStarted = AdapterCardSelectionBridge.BeginManualSelection(
            "adapter_headbutt_discard_to_draw",
            discardCards,
            1,
            1,
            selected => MoveSelectedCardsToPile(selected, owner, "Draw", "Top"),
            out string selectionDetail);
        if (!selectionStarted)
        {
            detail = $"박치기 수동 실행 실패: {selectionDetail}";
            return false;
        }

        detail = $"박치기 수동 실행 완료: damage={damage}, target={ReadNamedMember(target, "Name") ?? legalAction.TargetId}, {selectionDetail}";
        Logger.Info(detail);
        return true;
    }

    private static bool TryExecuteArmamentsAdapterManual(LegalActionSnapshot legalAction, out string detail)
    {
        detail = string.Empty;
        if (legalAction.CombatCardId is null)
        {
            return false;
        }

        object? sourceCard = CombatStateExporter.FindLatestRuntimeHandCardByCombatCardId(legalAction.CombatCardId.Value);
        if (sourceCard is null)
        {
            detail = "전투장비 수동 실행 실패: 손패에서 원본 카드를 찾지 못했습니다.";
            return false;
        }

        object? owner = ReadNamedMember(sourceCard, "Owner");
        object? creature = ReadNamedMember(owner, "Creature");
        if (owner is null || creature is null)
        {
            detail = "전투장비 수동 실행 실패: 카드 소유자 또는 플레이어 생명체를 찾지 못했습니다.";
            return false;
        }

        ApplySimpleBlock(creature, 5);
        SpendCardEnergy(owner, 1);

        List<object> upgradeCandidates = GetPlayerPileCards(owner, "Hand")
            .Where(card => !ReferenceEquals(card, sourceCard)
                && (ReadBool(card, "IsUpgradable") ?? ReadBool(card, "isUpgradable")) == true)
            .ToList();
        MoveSelectedCardsToPile(new[] { sourceCard }, owner, "Discard", "Bottom");

        bool sourceUpgraded = ReadBool(sourceCard, "IsUpgraded") == true || legalAction.CardUpgraded == true;
        if (sourceUpgraded)
        {
            UpgradeSelectedCards(upgradeCandidates);
            detail = $"전투장비 수동 실행 완료: block=5, upgraded_all={upgradeCandidates.Count}";
            Logger.Info(detail);
            return true;
        }

        bool selectionStarted = AdapterCardSelectionBridge.BeginManualSelection(
            "adapter_armaments_upgrade_hand",
            upgradeCandidates,
            1,
            1,
            selected => UpgradeSelectedCards(selected),
            out string selectionDetail);
        if (!selectionStarted)
        {
            detail = $"전투장비 수동 실행 완료: block=5, 강화 후보 없음. {selectionDetail}";
            Logger.Info(detail);
            return true;
        }

        detail = $"전투장비 수동 실행 완료: block=5, {selectionDetail}";
        Logger.Info(detail);
        return true;
    }

    private static void ApplySimpleDamage(object target, int damage)
    {
        Type? valuePropType = AccessTools.TypeByName("MegaCrit.Sts2.Core.ValueProps.ValueProp");
        object props = valuePropType is null ? 0 : Enum.Parse(valuePropType, "Move");

        decimal blocked = 0m;
        MethodInfo? damageBlock = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "DamageBlockInternal" && method.GetParameters().Length == 2);
        if (damageBlock is not null)
        {
            object? result = damageBlock.Invoke(target, new[] { (object)(decimal)damage, props });
            if (result is decimal decimalResult)
            {
                blocked = decimalResult;
            }
        }

        decimal hpDamage = Math.Max(0m, (decimal)damage - blocked);
        MethodInfo? loseHp = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "LoseHpInternal" && method.GetParameters().Length == 2);
        loseHp?.Invoke(target, new[] { (object)hpDamage, props });
    }

    private static void ApplySimpleBlock(object creature, int block)
    {
        MethodInfo? gainBlock = creature.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "GainBlockInternal" && method.GetParameters().Length == 1);
        gainBlock?.Invoke(creature, new object[] { (decimal)Math.Max(0, block) });
    }

    private static void SpendCardEnergy(object owner, int cost)
    {
        object? playerCombatState = ReadNamedMember(owner, "PlayerCombatState");
        MethodInfo? loseEnergy = playerCombatState?.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "LoseEnergy" && method.GetParameters().Length == 1);
        loseEnergy?.Invoke(playerCombatState, new object[] { (decimal)Math.Max(0, cost) });
    }

    private static IEnumerable<object> GetPlayerPileCards(object? player, string pileTypeName)
    {
        if (player is null)
        {
            return Array.Empty<object>();
        }

        Type? pileType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Cards.PileType");
        Type? extensionsType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Extensions.PileTypeExtensions")
            ?? AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Cards.PileTypeExtensions");
        if (pileType is null || extensionsType is null)
        {
            return Array.Empty<object>();
        }

        object pileValue = Enum.Parse(pileType, pileTypeName);
        MethodInfo? getPile = extensionsType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "GetPile" && method.GetParameters().Length == 2);
        object? pile = getPile?.Invoke(null, new[] { pileValue, player });
        object? cards = ReadNamedMember(pile, "Cards")
            ?? ReadNamedMember(pile, "cards")
            ?? ReadNamedMember(pile, "_cards");
        return ExpandValue(cards);
    }

    private static void UpgradeSelectedCards(IReadOnlyList<object> selectedCards)
    {
        Type? cardCmdType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.CardCmd");
        MethodInfo? upgrade = cardCmdType?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "Upgrade" && method.GetParameters().Length >= 1 && !typeof(IEnumerable).IsAssignableFrom(method.GetParameters()[0].ParameterType));
        foreach (object card in selectedCards)
        {
            if (upgrade is null)
            {
                continue;
            }

            object?[] args = upgrade.GetParameters().Length == 1
                ? new[] { card }
                : new[] { card, Type.Missing };
            upgrade.Invoke(null, args);
        }
    }

    private static void MoveSelectedCardsToPile(IReadOnlyList<object> selectedCards, object? player, string pileTypeName, string positionName)
    {
        Type? pileType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Cards.PileType");
        if (pileType is null || player is null)
        {
            return;
        }

        object pileValue = Enum.Parse(pileType, pileTypeName);
        object? targetPile = ResolvePlayerPile(player, pileValue);
        if (targetPile is null)
        {
            return;
        }

        MethodInfo? addInternal = targetPile.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "AddInternal" && method.GetParameters().Length >= 1);
        if (addInternal is null)
        {
            return;
        }

        int insertIndex = positionName.Equals("Top", StringComparison.OrdinalIgnoreCase) ? 0 : -1;
        foreach (object card in selectedCards)
        {
            object? oldPile = ReadFirstMember(card, "Pile", "pile", "_pile");
            MethodInfo? removeFromCurrentPile = card.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "RemoveFromCurrentPile" && method.GetParameters().Length <= 1);
            object?[] removeArgs = removeFromCurrentPile?.GetParameters().Length == 1
                ? new object?[] { false }
                : Array.Empty<object?>();
            removeFromCurrentPile?.Invoke(card, removeArgs);

            object?[] addArgs = addInternal.GetParameters().Length switch
            {
                1 => new[] { card },
                2 => new[] { card, insertIndex },
                _ => new[] { card, insertIndex, false }
            };
            addInternal.Invoke(targetPile, addArgs);

            InvokeNoArg(oldPile, "InvokeContentsChanged");
            InvokeNoArg(targetPile, "InvokeContentsChanged");
            InvokeNoArg(targetPile, "InvokeCardAddFinished");
        }
    }

    private static object? ResolvePlayerPile(object player, object pileValue)
    {
        Type? cardPileType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Cards.CardPile");
        MethodInfo? getPile = cardPileType?
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "Get" && method.GetParameters().Length == 2);
        if (getPile is not null)
        {
            return getPile.Invoke(null, new[] { pileValue, player });
        }

        Type? extensionsType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Extensions.PileTypeExtensions")
            ?? AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Cards.PileTypeExtensions");
        MethodInfo? extensionGetPile = extensionsType?
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "GetPile" && method.GetParameters().Length == 2);
        return extensionGetPile?.Invoke(null, new[] { pileValue, player });
    }

    private static void InvokeNoArg(object? source, string methodName)
    {
        if (source is null)
        {
            return;
        }

        MethodInfo? method = source.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.Name == methodName && candidate.GetParameters().Length == 0);
        method?.Invoke(source, Array.Empty<object?>());
    }

    private static object? ReadFirstMember(object? source, params string[] memberNames)
    {
        foreach (string memberName in memberNames)
        {
            object? value = ReadNamedMember(source, memberName);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryInvokeCardSelectionHolder(object selectionScreen, object cardHolder, object card)
    {
        if (IsHandCardSelectionRoot(selectionScreen))
        {
            string mode = ReadNamedMember(selectionScreen, "CurrentMode")?.ToString()
                ?? ReadNamedMember(selectionScreen, "_currentMode")?.ToString()
                ?? string.Empty;
            if (mode.Contains("Upgrade", StringComparison.OrdinalIgnoreCase)
                && TryInvokeMethod(selectionScreen, "SelectCardInUpgradeMode", out _, cardHolder))
            {
                return true;
            }

            if (TryInvokeMethod(selectionScreen, "SelectCardInSimpleMode", out _, cardHolder))
            {
                return true;
            }
        }

        return TryInvokeMethod(selectionScreen, "SelectHolder", out _, cardHolder)
            || TryInvokeMethod(selectionScreen, "OnCardClicked", out _, card)
            || TryInvokeMethod(cardHolder, "EmitSignalPressed", out _, cardHolder);
    }

    private static bool IsSameCardSelectionTarget(LegalActionSnapshot legalAction, object card, out string detail)
    {
        string? currentCardId = ReadCardModelId(card);
        string? currentName = ReadCardModelName(card);
        bool? currentUpgraded = ReadFirstBool(new[] { card }, "upgraded", "isUpgraded", "_upgraded", "_isUpgraded");

        if (!string.IsNullOrWhiteSpace(legalAction.CardId)
            && !string.IsNullOrWhiteSpace(currentCardId)
            && !IsSameLooseIdentifier(currentCardId, legalAction.CardId))
        {
            detail = $"card_id ?븍뜆?ょ㎉? expected={legalAction.CardId}, current={currentCardId}";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(legalAction.CardName)
            && !string.IsNullOrWhiteSpace(currentName)
            && !string.Equals(currentName, legalAction.CardName, StringComparison.OrdinalIgnoreCase))
        {
            detail = $"name ?븍뜆?ょ㎉? expected={legalAction.CardName}, current={currentName}";
            return false;
        }

        if (legalAction.CardUpgraded is not null
            && currentUpgraded is not null
            && currentUpgraded.Value != legalAction.CardUpgraded.Value)
        {
            detail = $"upgraded ?븍뜆?ょ㎉? expected={legalAction.CardUpgraded.Value}, current={currentUpgraded.Value}";
            return false;
        }

        detail = $"card_id={currentCardId ?? "<unknown>"}, name={currentName ?? "<unknown>"}, upgraded={currentUpgraded?.ToString() ?? "<unknown>"}";
        return true;
    }

    private static int CountSelectedCards(object selectionScreen)
    {
        object? selectedCards = ReadNamedMember(selectionScreen, "_selectedCards")
            ?? ReadNamedMember(selectionScreen, "SelectedCards")
            ?? ReadNamedMember(selectionScreen, "selectedCards");
        return ExpandValue(selectedCards).Count();
    }

    private static bool IsCardAlreadySelected(object selectionScreen, object card)
    {
        object? selectedCards = ReadNamedMember(selectionScreen, "_selectedCards")
            ?? ReadNamedMember(selectionScreen, "SelectedCards")
            ?? ReadNamedMember(selectionScreen, "selectedCards");
        return ExpandValue(selectedCards).Any(selectedCard => ReferenceEquals(selectedCard, card) || selectedCard.Equals(card));
    }

    private static string? ReadCardModelId(object card)
    {
        object? cardModel = ReadNamedMember(card, "Model")
            ?? ReadNamedMember(card, "model")
            ?? ReadNamedMember(card, "_model")
            ?? ReadNamedMember(card, "cardModel")
            ?? ReadNamedMember(card, "_cardModel");
        object? cardInfo = ReadNamedMember(card, "cardInfo")
            ?? ReadNamedMember(card, "_cardInfo")
            ?? ReadNamedMember(card, "info")
            ?? ReadNamedMember(card, "_info")
            ?? ReadNamedMember(card, "baseCard")
            ?? ReadNamedMember(card, "_baseCard");
        return ReadFirstString(new[] { card, cardModel, cardInfo }, "id", "_id", "cardId", "_cardId", "key", "_key")
            ?? ReadObjectId(cardModel)
            ?? ReadObjectId(cardInfo)
            ?? ReadObjectId(card);
    }

    private static string? ReadCardModelName(object card)
    {
        object? cardModel = ReadNamedMember(card, "Model")
            ?? ReadNamedMember(card, "model")
            ?? ReadNamedMember(card, "_model")
            ?? ReadNamedMember(card, "cardModel")
            ?? ReadNamedMember(card, "_cardModel");
        object? cardInfo = ReadNamedMember(card, "cardInfo")
            ?? ReadNamedMember(card, "_cardInfo")
            ?? ReadNamedMember(card, "info")
            ?? ReadNamedMember(card, "_info")
            ?? ReadNamedMember(card, "baseCard")
            ?? ReadNamedMember(card, "_baseCard");
        return ReadFirstString(new[] { card, cardModel, cardInfo }, "name", "_name", "title", "_title", "displayName", "_displayName");
    }

    private static string? ReadFirstString(IEnumerable<object?> sources, params string[] memberNames)
    {
        foreach (object? source in sources)
        {
            foreach (string memberName in memberNames)
            {
                object? value = ReadNamedMember(source, memberName);
                if (value is null)
                {
                    continue;
                }

                string text = value.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static bool? ReadFirstBool(IEnumerable<object?> sources, params string[] memberNames)
    {
        foreach (object? source in sources)
        {
            foreach (string memberName in memberNames)
            {
                bool? value = ReadBool(source, memberName);
                if (value is not null)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static bool IsSameLooseIdentifier(string left, string right)
    {
        return NormalizeLooseIdentifier(left).Equals(NormalizeLooseIdentifier(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLooseIdentifier(string value)
    {
        string normalized = new string(value
            .Where(character => char.IsLetterOrDigit(character))
            .Select(char.ToUpperInvariant)
            .ToArray());

        string[] knownPrefixes = { "CARD", "POTION", "RELIC", "POWER" };
        foreach (string prefix in knownPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal)
                && normalized.Length > prefix.Length)
            {
                return normalized[prefix.Length..];
            }
        }

        return normalized;
    }

    private static bool TryExecuteCardSelectionConfirm(
        LegalActionSnapshot legalAction,
        object currentRoot,
        out string detail,
        out string resultStatus)
    {
        detail = string.Empty;
        resultStatus = "failed";
        if (AdapterCardSelectionBridge.TryConfirm(
                legalAction.CardSelectionRuntimeId,
                legalAction.CardSelectionSelectedCount,
                out detail,
                out CardSelectionActionStatus adapterStatus))
        {
            resultStatus = ToResultStatus(adapterStatus);
            return true;
        }

        if (adapterStatus == CardSelectionActionStatus.Stale && !string.IsNullOrWhiteSpace(legalAction.CardSelectionRuntimeId))
        {
            resultStatus = "stale";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(legalAction.CardSelectionRuntimeId))
        {
            resultStatus = ToResultStatus(adapterStatus);
            return false;
        }

        if (AdapterCardSelectionBridge.TryConfirm(out detail))
        {
            return true;
        }

        object? selectionScreen = ResolveCardSelectionScreen(currentRoot);
        if (selectionScreen is null)
        {
            string rootTypeName = currentRoot.GetType().FullName ?? currentRoot.GetType().Name;
            detail = $"燁삳?諭??醫뤾문 ?類ㅼ뵥????쎈뻬???遺얇늺??筌≪뼚? 筌륁궢六??щ빍?? root={rootTypeName}";
            return false;
        }

        if (TryInvokeMethod(selectionScreen, "CheckIfSelectionComplete", out _))
        {
            detail = "燁삳?諭??醫뤾문 ?類ㅼ뵥???遺욧퍕??됰뮸??덈뼄. method=CheckIfSelectionComplete";
            Logger.Info(detail);
            return true;
        }

        object? confirmButton = FindCardSelectionConfirmButton(selectionScreen);
        if (confirmButton is not null && ReadBool(confirmButton, "IsEnabled") == false)
        {
            detail = "移대뱶 ?좏깮 ?뺤젙 踰꾪듉???꾩쭅 鍮꾪솢???곹깭?낅땲??";
            return false;
        }

        if (TryInvokeMethod(selectionScreen, "CompleteSelection", out _, confirmButton))
        {
            detail = "燁삳?諭??醫뤾문 ?類ㅼ뵥???遺욧퍕??됰뮸??덈뼄. method=CompleteSelection";
            Logger.Info(detail);
            return true;
        }

        if (TryInvokeMethod(selectionScreen, "ConfirmSelection", out _, confirmButton))
        {
            detail = "燁삳?諭??醫뤾문 ?類ㅼ뵥???遺욧퍕??됰뮸??덈뼄. method=ConfirmSelection";
            Logger.Info(detail);
            return true;
        }

        if (confirmButton is not null && TryInvokeMethod(confirmButton, "OnRelease", out _))
        {
            detail = "燁삳?諭??醫뤾문 ?類ㅼ뵥 甕곌쑵????????щ빍??";
            Logger.Info(detail);
            return true;
        }

        detail = "燁삳?諭??醫뤾문 ?類ㅼ뵥 筌롫뗄苑??? ?類ㅼ뵥 甕곌쑵???筌≪뼚? 筌륁궢六??щ빍??";
        return false;
    }

    private static bool TryExecuteCardSelectionCancel(object currentRoot, out string detail)
    {
        if (AdapterCardSelectionBridge.TryCancel(out detail))
        {
            return true;
        }

        object? selectionScreen = ResolveCardSelectionScreen(currentRoot);
        if (selectionScreen is null)
        {
            string rootTypeName = currentRoot.GetType().FullName ?? currentRoot.GetType().Name;
            detail = $"燁삳?諭??醫뤾문 ?띯뫁?쇘몴???쎈뻬???遺얇늺??筌≪뼚? 筌륁궢六??щ빍?? root={rootTypeName}";
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
                detail = $"燁삳?諭??醫뤾문 ?띯뫁?쇘몴??遺욧퍕??됰뮸??덈뼄. method={methodName}";
                Logger.Info(detail);
                return true;
            }
        }

        object? cancelButton = FindCardSelectionCancelButton(selectionScreen);
        if (cancelButton is not null && TryInvokeMethod(cancelButton, "OnRelease", out _))
        {
            detail = "燁삳?諭??醫뤾문 ?띯뫁??甕곌쑵????????щ빍??";
            Logger.Info(detail);
            return true;
        }

        detail = "燁삳?諭??醫뤾문 ?띯뫁??筌롫뗄苑??뺢돌 ?띯뫁??甕곌쑵???筌≪뼚? 筌륁궢六??щ빍??";
        return false;
    }

    private static bool TryExecuteMapNodeSelection(
        LegalActionSnapshot legalAction,
        object mapRoot,
        PendingClaim claim,
        out string detail,
        out bool resultDeferred)
    {
        resultDeferred = false;
        string rootTypeName = mapRoot.GetType().FullName ?? mapRoot.GetType().Name;
        if (!rootTypeName.Contains("NMapScreen", StringComparison.OrdinalIgnoreCase))
        {
            detail = $"筌왖????곕짗????쎈뻬??????덈뮉 ?遺얇늺???袁⑤뻸??덈뼄. root={rootTypeName}";
            return false;
        }

        if (ReadNamedMember(mapRoot, "IsTraveling") is bool isTraveling && isTraveling)
        {
            detail = "??? 筌왖????猷?餓λ쵐?????筌왖???醫뤾문????쎈뻬??? ??녿릭??щ빍??";
            return false;
        }

        bool isTravelEnabled = ReadNamedMember(mapRoot, "IsTravelEnabled") is bool travelEnabled && travelEnabled;
        bool isDebugTravelEnabled = ReadNamedMember(mapRoot, "IsDebugTravelEnabled") is bool debugTravelEnabled && debugTravelEnabled;
        if (!isTravelEnabled && !isDebugTravelEnabled)
        {
            detail = "?袁⑹삺 筌왖?袁⑸퓠????猷??醫뤾문????뽮쉐?遺얜┷????? ??녿뮸??덈뼄.";
            return false;
        }

        object? mapPoint = FindMapPointForAction(mapRoot, legalAction);
        if (mapPoint is null)
        {
            detail = $"?醫뤾문??筌왖???紐껊굡??筌≪뼚? 筌륁궢六??щ빍?? node_id={legalAction.NodeId ?? "<none>"}, row={legalAction.MapRow?.ToString() ?? "?"}, column={legalAction.MapColumn?.ToString() ?? "?"}";
            return false;
        }

        if (!IsMapPointSelectable(mapPoint))
        {
            detail = $"筌왖???紐껊굡揶쎛 ?袁⑹삺 ?醫뤾문 揶쎛???怨밴묶揶쎛 ?袁⑤뻸??덈뼄. node_id={legalAction.NodeId ?? BuildMapNodeId(mapPoint)}";
            return false;
        }

        long roomEnteredBaseline = Interlocked.Read(ref roomEnteredSignalCount);
        bool roomEnteredSubscribed = TrySubscribeRoomEntered(out object? runManager, out Delegate? roomEnteredHandler, out string roomEnteredSubscribeDetail);
        List<string> invokedCandidates = new();
        if (!roomEnteredSubscribed && !string.IsNullOrWhiteSpace(roomEnteredSubscribeDetail))
        {
            invokedCandidates.Add($"room_entered_subscribe: {roomEnteredSubscribeDetail}");
        }

        List<(string Label, object? Source, string MethodName, object?[] Args)> nonBlockingCandidates = new()
        {
            ("point.ForceClick", mapPoint, "ForceClick", Array.Empty<object?>()),
            ("point.OnRelease", mapPoint, "OnRelease", Array.Empty<object?>()),
            ("point.OnPressed", mapPoint, "OnPressed", Array.Empty<object?>()),
            ("map.OnMapPointSelectedLocally(point)", mapRoot, "OnMapPointSelectedLocally", new object?[] { mapPoint })
        };

        foreach ((string label, object? source, string methodName, object?[] args) in nonBlockingCandidates)
        {
            if (!TryInvokeMethod(source, methodName, out _, args))
            {
                continue;
            }

            invokedCandidates.Add(label);
            pendingMapNodeSelection = new PendingMapNodeSelection(
                legalAction,
                claim,
                mapRoot,
                legalAction.NodeId ?? BuildMapNodeId(mapPoint),
                roomEnteredBaseline,
                roomEnteredSubscribed ? runManager : null,
                roomEnteredSubscribed ? roomEnteredHandler : null,
                label,
                Environment.TickCount64);
            detail = $"筌왖???紐껊굡 ?醫뤾문 ?醫륁깈??癰귣?源됪?獄?筌욊쑴???袁⑥┷ ?癒?젟????됰튋??됰뮸??덈뼄. method={label}, node_id={legalAction.NodeId ?? BuildMapNodeId(mapPoint)}, row={legalAction.MapRow?.ToString() ?? "?"}, column={legalAction.MapColumn?.ToString() ?? "?"}";
            resultDeferred = true;
            return true;
        }

        if (TryCallDeferredNoArgs(mapPoint, "ForceClick", out string nonBlockingDeferredFailureReason))
        {
            invokedCandidates.Add("point.CallDeferred(ForceClick)");
            pendingMapNodeSelection = new PendingMapNodeSelection(
                legalAction,
                claim,
                mapRoot,
                legalAction.NodeId ?? BuildMapNodeId(mapPoint),
                roomEnteredBaseline,
                roomEnteredSubscribed ? runManager : null,
                roomEnteredSubscribed ? roomEnteredHandler : null,
                "point.CallDeferred(ForceClick)",
                Environment.TickCount64);
            detail = $"筌왖???紐껊굡 ?醫뤾문 ?醫륁깈????됰튋??뉙?獄?筌욊쑴???袁⑥┷ ?癒?젟??疫꿸퀡?롧뵳?덈빍?? method=point.CallDeferred(ForceClick), node_id={legalAction.NodeId ?? BuildMapNodeId(mapPoint)}, row={legalAction.MapRow?.ToString() ?? "?"}, column={legalAction.MapColumn?.ToString() ?? "?"}";
            resultDeferred = true;
            return true;
        }

        if (roomEnteredSubscribed)
        {
            UnsubscribeRoomEntered(runManager, roomEnteredHandler);
        }

        if (nonBlockingDeferredFailureReason.Length > 0)
        {
            invokedCandidates.Add($"point.CallDeferred(ForceClick): {nonBlockingDeferredFailureReason}");
        }

        string nonBlockingTried = invokedCandidates.Count == 0 ? "<none>" : string.Join(", ", invokedCandidates);
        detail = $"筌왖???紐껊굡 ?醫뤾문 筌롫뗄苑??? ?紐꾪뀱??? 筌륁궢六??щ빍?? tried={nonBlockingTried}, node_id={legalAction.NodeId ?? BuildMapNodeId(mapPoint)}";
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
            detail = $"癰귣똻湲???곕짗????쎈뻬??????덈뮉 ?遺얇늺???袁⑤뻸??덈뼄. root={rootTypeName}";
            return false;
        }

        if (legalAction.ActionType.Equals("proceed_reward_screen", StringComparison.OrdinalIgnoreCase))
        {
            return TryPressRewardProceedButton(rewardRoot, out detail);
        }

        if (!TryParseRewardIndex(legalAction.RewardId, out int rewardIndex))
        {
            detail = $"reward_id????곴퐤??? 筌륁궢六??щ빍?? reward_id={legalAction.RewardId ?? "<none>"}";
            return false;
        }

        object? rewardButton = FindRewardButtonByIndex(rewardRoot, rewardIndex);
        if (rewardButton is null)
        {
            detail = $"癰귣똻湲?甕곌쑵???筌≪뼚? 筌륁궢六??щ빍?? reward_id={legalAction.RewardId}, index={rewardIndex}";
            return false;
        }

        if (legalAction.ActionType.Equals("skip_card_reward", StringComparison.OrdinalIgnoreCase))
        {
            object? reward = ReadNamedMember(rewardButton, "Reward");
            _ = TryInvokeMethod(reward, "OnSkipped", out _);
            if (!TryInvokeMethod(rewardRoot, "RewardCollectedFrom", out _, rewardButton))
            {
                detail = $"燁삳?諭?癰귣똻湲?椰꾨?瑗?怨뚮┛ 筌ｌ꼶??????筌?癰귣똻湲??遺얇늺?癒?퐣 甕곌쑵????볤탢 ?紐꾪뀱????쎈솭??됰뮸??덈뼄. reward_id={legalAction.RewardId}";
                return false;
            }

            detail = $"燁삳?諭?癰귣똻湲??椰꾨?瑗?怨쀫???щ빍?? reward_id={legalAction.RewardId}";
            Logger.Info(detail);
            return true;
        }

        if (legalAction.ActionType.Equals("choose_card_reward", StringComparison.OrdinalIgnoreCase))
        {
            if (legalAction.CardRewardIndex is null)
            {
                detail = "choose_card_reward ??곕짗??card_reward_index揶쎛 ??곷뮸??덈뼄.";
                return false;
            }

            if (!TryInvokeMethod(rewardButton, "OnRelease", out _))
            {
                detail = $"燁삳?諭?癰귣똻湲?甕곌쑵????袁ⓥ뀮筌왖 筌륁궢六??щ빍?? reward_id={legalAction.RewardId}";
                return false;
            }

            pendingRewardCardSelection = new PendingRewardCardSelection(
                legalAction.CardRewardIndex.Value,
                claim,
                Environment.TickCount64);
            detail = $"燁삳?諭?癰귣똻湲??遺얇늺????욱?{legalAction.CardRewardIndex.Value}甕?燁삳?諭??醫뤾문????됰튋??됰뮸??덈뼄. reward_id={legalAction.RewardId}";
            resultDeferred = true;
            return true;
        }

        if (legalAction.ActionType.Equals("claim_potion_reward_with_discard", StringComparison.OrdinalIgnoreCase))
        {
            if (legalAction.DiscardPotionSlotIndex is null)
            {
                detail = "?????대Ŋ猿?癰귣똻湲???곕짗??discard_potion_slot_index揶쎛 ??곷뮸??덈뼄.";
                return false;
            }

            if (!TryEnqueueDiscardPotionAction(legalAction, out detail))
            {
                return false;
            }

            pendingPotionRewardClaim = new PendingPotionRewardClaim(
                rewardIndex,
                legalAction,
                claim,
                Environment.TickCount64);
            detail = $"????????{legalAction.DiscardPotionSlotIndex.Value}甕?甕곌쑬?곫묾怨? ??됰튋??뉙? ???????쑬??癰귣똻湲??????獄쏆룇????됱젟??낅빍?? reward_id={legalAction.RewardId}";
            resultDeferred = true;
            return true;
        }

        if (!TryInvokeMethod(rewardButton, "OnRelease", out _))
        {
            detail = $"癰귣똻湲?甕곌쑵????袁ⓥ뀮筌왖 筌륁궢六??щ빍?? reward_id={legalAction.RewardId}, action_type={legalAction.ActionType}";
            return false;
        }

        detail = $"癰귣똻湲?甕곌쑵????????щ빍?? reward_id={legalAction.RewardId}, action_type={legalAction.ActionType}";
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
            string detail = $"??됰튋??燁삳?諭?癰귣똻湲??醫뤾문????쀫립 ??볦퍢 ??됰퓠 ?袁⑥┷??? ??녿릭??щ빍?? submission_id={pending.Claim.Action.SubmissionId}, card_reward_index={pending.CardRewardIndex}";
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
            string detail = $"燁삳?諭?癰귣똻湲??醫뤾문????? ?袁⑥┷??뤿선 ??됰튋???ル굝利??됰뮸??덈뼄. submission_id={pending.Claim.Action.SubmissionId}, card_reward_index={pending.CardRewardIndex}";
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
            Logger.Warning($"燁삳?諭?癰귣똻湲??醫뤾문 ?紐껊쑔??? ?遺얇늺??燁삳?諭???? 甕곗щ선?????덈뼄. index={pending.CardRewardIndex}, count={cardHolders.Count}");
            return;
        }

        object cardHolder = cardHolders[pending.CardRewardIndex];
        if (!TryInvokeMethod(selectionScreen, "SelectCard", out _, cardHolder))
        {
            if (IsCompletionSourceCompleted(completionSource))
            {
                pendingRewardCardSelection = null;
                string completedDetail = $"燁삳?諭?癰귣똻湲??醫뤾문???袁⑥┷???怨밴묶嚥??類ㅼ뵥??뤿???щ빍?? submission_id={pending.Claim.Action.SubmissionId}, card_reward_index={pending.CardRewardIndex}";
                Logger.Info(completedDetail);
                RememberExecuted(pending.Claim.Action.SubmissionId);
                ReportResult(pending.Claim, "applied", completedDetail);
            }

            return;
        }

        pendingRewardCardSelection = null;
        string successDetail = $"燁삳?諭?癰귣똻湲??醫뤾문 ?醫륁깈??癰귣?源??щ빍?? submission_id={pending.Claim.Action.SubmissionId}, card_reward_index={pending.CardRewardIndex}";
        Logger.Info(successDetail);
        RememberExecuted(pending.Claim.Action.SubmissionId);
        ReportResult(pending.Claim, "applied", successDetail);
    }

    private static void TryExecutePendingPotionRewardClaim()
    {
        PendingPotionRewardClaim? pending = pendingPotionRewardClaim;
        if (pending is null)
        {
            return;
        }

        long nowMs = Environment.TickCount64;
        if (nowMs - pending.StartedAtMs > PotionRewardClaimAfterDiscardTimeoutMs)
        {
            pendingPotionRewardClaim = null;
            string detail = $"????????甕곌쑬?곫묾???癰귣똻湲???롮죯????쀫립 ??볦퍢 ??됰퓠 ??멸돌筌왖 ??녿릭??щ빍?? submission_id={pending.Claim.Action.SubmissionId}, reward_id={pending.LegalAction.RewardId}";
            Logger.Warning(detail);
            RememberExecuted(pending.Claim.Action.SubmissionId);
            ReportResult(pending.Claim, "failed", detail);
            return;
        }

        if (!IsPotionSlotOpen(pending.LegalAction.DiscardPotionSlotIndex))
        {
            return;
        }

        object? rewardScreen = ResolveCurrentRewardScreen();
        if (rewardScreen is null)
        {
            return;
        }

        object? rewardButton = FindRewardButtonByIndex(rewardScreen, pending.RewardIndex);
        if (rewardButton is null)
        {
            return;
        }

        if (!TryInvokeMethod(rewardButton, "OnRelease", out _))
        {
            pendingPotionRewardClaim = null;
            string detail = $"????????? ??쑴肉筌왖筌?癰귣똻湲?甕곌쑵???紐꾪뀱????쎈솭??됰뮸??덈뼄. reward_id={pending.LegalAction.RewardId}";
            Logger.Warning(detail);
            RememberExecuted(pending.Claim.Action.SubmissionId);
            ReportResult(pending.Claim, "failed", detail);
            return;
        }

        pendingPotionRewardClaim = null;
        string successDetail = $"???????????쑴????癰귣똻湲??????獄쏆룇釉??щ빍?? reward_id={pending.LegalAction.RewardId}, slot={pending.LegalAction.DiscardPotionSlotIndex}";
        Logger.Info(successDetail);
        RememberExecuted(pending.Claim.Action.SubmissionId);
        ReportResult(pending.Claim, "applied", successDetail);
    }

    private static object? ResolveCurrentRewardScreen()
    {
        object? root = CombatActionRuntimeContext.GetSnapshot().CombatRoot;
        string rootTypeName = root?.GetType().FullName ?? root?.GetType().Name ?? string.Empty;
        if (root is not null && rootTypeName.Contains("NRewardsScreen", StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return FindTopOverlayByTypeName("NRewardsScreen");
    }

    private static bool IsPotionSlotOpen(int? slotIndex)
    {
        if (slotIndex is null)
        {
            return false;
        }

        object? player = ResolveRuntimePlayerForAction();
        if (player is null)
        {
            return false;
        }

        List<object?> potionSlots = ResolvePotionSlots(player).ToList();
        int index = slotIndex.Value;
        return index >= 0
            && index < potionSlots.Count
            && ExtractRuntimePotionModel(potionSlots[index]) is null;
    }

    private static bool TryEnqueueDiscardPotionAction(LegalActionSnapshot legalAction, out string detail)
    {
        object? player = ResolveRuntimePlayerForAction();
        if (player is null)
        {
            detail = "?????甕곌쑬?????쟿??곷선 揶쏆빘猿쒐몴?筌≪뼚? 筌륁궢六??щ빍??";
            return false;
        }

        int slotIndex = legalAction.DiscardPotionSlotIndex ?? -1;
        List<object?> potionSlots = ResolvePotionSlots(player).ToList();
        if (slotIndex < 0 || slotIndex >= potionSlots.Count)
        {
            detail = $"甕곌쑬???????????紐껊쑔??? 甕곕뗄?욅몴?甕곗щ선?????덈뼄. index={slotIndex}, count={potionSlots.Count}";
            return false;
        }

        object? potion = ExtractRuntimePotionModel(potionSlots[slotIndex]);
        if (potion is null)
        {
            detail = $"甕곌쑬?????????????? ??쑴堉???됰뮸??덈뼄. index={slotIndex}";
            return false;
        }

        string? runtimePotionId = ReadObjectId(potion);
        if (!string.IsNullOrWhiteSpace(legalAction.DiscardPotionId)
            && !string.IsNullOrWhiteSpace(runtimePotionId)
            && !IsSameShopModelId(runtimePotionId, legalAction.DiscardPotionId))
        {
            detail = $"甕곌쑬???????獄쏅뗀??????щ빍?? expected={legalAction.DiscardPotionId}, actual={runtimePotionId}";
            return false;
        }

        try
        {
            Type? actionType = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.DiscardPotionGameAction");
            if (actionType is null)
            {
                detail = "DiscardPotionGameAction ????놁뱽 筌≪뼚? 筌륁궢六??щ빍??";
                return false;
            }

            object? combatManager = ReadStaticNamedMember(AccessTools.TypeByName("MegaCrit.Sts2.Core.Combat.CombatManager"), "Instance");
            bool isInProgress = ReadBool(combatManager, "IsInProgress") ?? false;
            object? action = Activator.CreateInstance(actionType, player, (uint)slotIndex, isInProgress);
            if (action is null)
            {
                detail = "DiscardPotionGameAction ?紐꾨뮞??곷뮞 ??밴쉐????쎈솭??됰뮸??덈뼄.";
                return false;
            }

            object? synchronizer = ResolveActionQueueSynchronizer();
            if (synchronizer is null)
            {
                detail = "ActionQueueSynchronizer??筌≪뼚? 筌륁궢六??щ빍??";
                return false;
            }

            MethodInfo? requestEnqueue = FindRequestEnqueueMethod(synchronizer, action);
            if (requestEnqueue is null)
            {
                detail = "ActionQueueSynchronizer.RequestEnqueue 筌롫뗄苑??? 筌≪뼚? 筌륁궢六??щ빍??";
                return false;
            }

            requestEnqueue.Invoke(synchronizer, new[] { action });
            detail = $"DiscardPotionGameAction???癒?퓠 ?節뚮???щ빍?? slot={slotIndex}, potion={runtimePotionId ?? legalAction.DiscardPotionId ?? "<unknown>"}";
            Logger.Info(detail);
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

    private static bool TryPressRewardProceedButton(object rewardScreen, out string detail)
    {
        object? proceedButton = ReadNamedMember(rewardScreen, "_proceedButton");
        if (proceedButton is null)
        {
            detail = "癰귣똻湲??遺얇늺??筌욊쑵六?甕곌쑵???筌≪뼚? 筌륁궢六??щ빍??";
            return false;
        }

        if (!TryInvokeMethod(rewardScreen, "OnProceedButtonPressed", out _, proceedButton))
        {
            detail = "癰귣똻湲??遺얇늺 筌욊쑵六?甕곌쑵???紐꾪뀱????쎈솭??됰뮸??덈뼄.";
            return false;
        }

        detail = "癰귣똻湲??遺얇늺 筌욊쑵六?甕곌쑵????????щ빍??";
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
        if (IsCardSelectionScreen(currentScreen))
        {
            return currentScreen;
        }

        return FindHandCardSelectionRoot();
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

    private static void TryExecutePendingMapNodeSelection()
    {
        PendingMapNodeSelection? pending = pendingMapNodeSelection;
        if (pending is null)
        {
            return;
        }

        long nowMs = Environment.TickCount64;
        if (nowMs - pending.StartedAtMs > 60000)
        {
            pendingMapNodeSelection = null;
            UnsubscribeRoomEntered(pending.RunManager, pending.RoomEnteredHandler);
            string currentScreenTypeName = GetCurrentScreenTypeName();
            bool? isStillTraveling = ReadNamedMember(pending.MapRoot, "IsTraveling") is bool stillTraveling
                ? stillTraveling
                : null;
            string detail = $"筌왖???紐껊굡 ??猷??袁⑥┷????쀫립 ??볦퍢 ??됰퓠 ?類ㅼ뵥??? 筌륁궢六??щ빍?? method={pending.MethodLabel}, current_screen={currentScreenTypeName}, is_traveling={isStillTraveling?.ToString() ?? "<unknown>"}, node_id={pending.NodeId}";
            Logger.Warning(detail);
            RememberExecuted(pending.Claim.Action.SubmissionId);
            ReportResult(pending.Claim, "failed", detail);
            return;
        }

        object? currentScreen = GetCurrentScreen();
        bool roomEntered = Interlocked.Read(ref roomEnteredSignalCount) > pending.RoomEnteredBaseline;
        bool screenLeftMap = currentScreen is not null && !IsMapScreen(currentScreen);
        bool traveling = ReadNamedMember(pending.MapRoot, "IsTraveling") is bool isTraveling && isTraveling;
        bool arrivedAtTargetOnMap = !traveling && IsCurrentMapPointTarget(pending.MapRoot, pending.LegalAction);
        if (roomEntered || screenLeftMap || arrivedAtTargetOnMap)
        {
            pending.StableCompletionPollCount++;
            if (pending.StableCompletionPollCount < 5)
            {
                return;
            }

            pendingMapNodeSelection = null;
            UnsubscribeRoomEntered(pending.RunManager, pending.RoomEnteredHandler);
            string detail = $"筌왖???紐껊굡 ??猷??袁⑥┷???類ㅼ뵥??됰뮸??덈뼄. method={pending.MethodLabel}, room_entered={roomEntered}, screen_left_map={screenLeftMap}, arrived_at_target={arrivedAtTargetOnMap}, node_id={pending.NodeId}";
            Logger.Info(detail);
            RememberExecuted(pending.Claim.Action.SubmissionId);
            ReportResult(pending.Claim, "applied", detail);
            return;
        }

        pending.StableCompletionPollCount = 0;
    }

    private static void TryExecutePendingCombatActionConfirmation()
    {
        PendingCombatActionConfirmation? pending = pendingCombatActionConfirmation;
        if (pending is null)
        {
            return;
        }

        CombatActionContextSnapshot context = CombatActionRuntimeContext.GetSnapshot();
        CombatStateBridgePoster.PostedStateSnapshot? postedState = CombatStateBridgePoster.GetLatestPostedState();
        bool runtimeStateChanged = !string.IsNullOrWhiteSpace(context.StateId)
            && !context.StateId.Equals(pending.InitialStateId, StringComparison.Ordinal);
        bool postedStateChanged = postedState is not null
            && !postedState.StateId.Equals(pending.Claim.PostedState.StateId, StringComparison.Ordinal);

        long nowMs = Environment.TickCount64;
        if (runtimeStateChanged || postedStateChanged)
        {
            if (TryResolveAdapterCardSelectionActionCompletion(
                pending,
                context,
                ref postedState,
                out string adapterCardSelectionDetail))
            {
                ClearPendingCombatActionConfirmation(pending);
                Logger.Info(adapterCardSelectionDetail);
                RememberExecuted(pending.Claim.Action.SubmissionId);
                ReportResult(pending.Claim, "applied", adapterCardSelectionDetail, postedState);
                return;
            }

            if (ShouldWaitForCombatActionQueueToSettle(pending, nowMs, context.StateId, postedState))
            {
                return;
            }

            ClearPendingCombatActionConfirmation(pending);
            string detail = $"?袁る떮 ??곕짗 ?怨몄뒠 ???怨밴묶 癰궰?遺? ?類ㅼ뵥??됰뮸??덈뼄. action={pending.LegalAction.ActionId}, input_detail={pending.InputDetail}, initial_state={pending.InitialStateId}, current_state={context.StateId ?? "<none>"}, posted_state={postedState?.StateId ?? "<none>"}";
            Logger.Info(detail);
            RememberExecuted(pending.Claim.Action.SubmissionId);
            ReportResult(pending.Claim, "applied", detail, postedState);
            return;
        }

        if (nowMs - pending.LastForceExportAtMs >= CombatActionConfirmationForceExportIntervalMs)
        {
            pending.LastForceExportAtMs = nowMs;
            CombatStateExporter.CombatExportProbe probe = CombatStateExporter.ForceExportFromCombatManager("combat_action_confirmation");
            if (probe.StateFound)
            {
                context = CombatActionRuntimeContext.GetSnapshot();
                postedState = CombatStateBridgePoster.GetLatestPostedState();
                runtimeStateChanged = !string.IsNullOrWhiteSpace(context.StateId)
                    && !context.StateId.Equals(pending.InitialStateId, StringComparison.Ordinal);
                postedStateChanged = postedState is not null
                    && !postedState.StateId.Equals(pending.Claim.PostedState.StateId, StringComparison.Ordinal);
                if (runtimeStateChanged || postedStateChanged)
                {
                    if (TryResolveAdapterCardSelectionActionCompletion(
                        pending,
                        context,
                        ref postedState,
                        out string adapterCardSelectionDetail))
                    {
                        ClearPendingCombatActionConfirmation(pending);
                        Logger.Info(adapterCardSelectionDetail);
                        RememberExecuted(pending.Claim.Action.SubmissionId);
                        ReportResult(pending.Claim, "applied", adapterCardSelectionDetail, postedState);
                        return;
                    }

                    if (ShouldWaitForCombatActionQueueToSettle(pending, nowMs, context.StateId, postedState))
                    {
                        return;
                    }

                    ClearPendingCombatActionConfirmation(pending);
                    string detail = $"?袁る떮 ??곕짗 ?怨몄뒠 ??揶쏅벡???怨밴묶 揶쏄퉮???곗쨮 癰궰?遺? ?類ㅼ뵥??됰뮸??덈뼄. action={pending.LegalAction.ActionId}, input_detail={pending.InputDetail}, initial_state={pending.InitialStateId}, current_state={context.StateId ?? "<none>"}, posted_state={postedState?.StateId ?? "<none>"}";
                    Logger.Info(detail);
                    RememberExecuted(pending.Claim.Action.SubmissionId);
                    ReportResult(pending.Claim, "applied", detail, postedState);
                    return;
                }
            }
        }

        if (pending.LegalAction.ActionType.Equals("use_potion", StringComparison.OrdinalIgnoreCase)
            && TryResolvePotionActionCompletion(pending, nowMs, out string potionCompletionResult, out string potionCompletionDetail))
        {
            ClearPendingCombatActionConfirmation(pending);
            Logger.Info(potionCompletionDetail);
            RememberExecuted(pending.Claim.Action.SubmissionId);
            ReportResult(pending.Claim, potionCompletionResult, potionCompletionDetail, postedState);
            return;
        }

        long timeoutMs = pending.LegalAction.ActionType.Equals("use_potion", StringComparison.OrdinalIgnoreCase)
            ? 45000
            : 10000;
        if (nowMs - pending.StartedAtMs <= timeoutMs)
        {
            return;
        }

        ClearPendingCombatActionConfirmation(pending);
        string timeoutDetail = $"?袁る떮 ??곕짗 ??낆젾 ????쀫립 ??볦퍢 ??됰퓠 ?怨밴묶 癰궰?遺? ?온筌↔퀡由븝쭪? ??녿릭??щ빍?? action={pending.LegalAction.ActionId}, input_detail={pending.InputDetail}, state={context.StateId ?? "<none>"}";
        Logger.Warning(timeoutDetail);
        RememberExecuted(pending.Claim.Action.SubmissionId);
        ReportResult(pending.Claim, "failed", timeoutDetail, postedState);
    }

    private static bool TryResolveAdapterCardSelectionActionCompletion(
        PendingCombatActionConfirmation pending,
        CombatActionContextSnapshot context,
        ref CombatStateBridgePoster.PostedStateSnapshot? postedState,
        out string detail)
    {
        detail = string.Empty;
        bool canOpenFollowUpSelection = pending.LegalAction.ActionType.Equals("play_card", StringComparison.OrdinalIgnoreCase)
            || pending.LegalAction.ActionType.Equals("use_potion", StringComparison.OrdinalIgnoreCase);
        if (!canOpenFollowUpSelection || !AdapterCardSelectionBridge.HasPendingSelection)
        {
            return false;
        }

        CombatStateExporter.TryExportAdapterCardSelectionStateIfPending();
        postedState = CombatStateBridgePoster.GetLatestPostedState();
        string source = pending.LegalAction.ActionType.Equals("use_potion", StringComparison.OrdinalIgnoreCase)
            ? "포션 사용"
            : "카드 사용";
        detail = $"{source} 후 후속 카드 선택이 열렸습니다. action={pending.LegalAction.ActionId}, input_detail={pending.InputDetail}, initial_state={pending.InitialStateId}, current_state={context.StateId ?? "<none>"}, posted_state={postedState?.StateId ?? "<none>"}";
        return true;
    }

    private static void ClearPendingCombatActionConfirmation(PendingCombatActionConfirmation pending)
    {
        if (ReferenceEquals(pendingCombatActionConfirmation, pending))
        {
            pendingCombatActionConfirmation = null;
        }

        pending.ActiveCardSelectorScope?.Dispose();
        pending.ActiveCardSelectorScope = null;
    }

    private static bool TryResolvePotionActionCompletion(
        PendingCombatActionConfirmation pending,
        long nowMs,
        out string result,
        out string detail)
    {
        result = "failed";
        detail = string.Empty;

        LegalActionSnapshot legalAction = pending.LegalAction;
        if (legalAction.PotionSlotIndex is null)
        {
            detail = $"포션 행동 완료 확인 실패: potion_slot_index가 없습니다. action={legalAction.ActionId}";
            return true;
        }

        if (!IsPlayerDrivenActionQueueSettled())
        {
            return false;
        }

        object? player = ResolveRuntimePlayerForAction();
        if (player is null)
        {
            detail = $"포션 행동 완료 확인 실패: 현재 플레이어 객체를 찾지 못했습니다. action={legalAction.ActionId}";
            return true;
        }

        List<object?> potionSlots = ResolvePotionSlots(player).ToList();
        int slotIndex = legalAction.PotionSlotIndex.Value;
        if (slotIndex < 0 || slotIndex >= potionSlots.Count)
        {
            detail = $"포션 행동 완료 확인 실패: 포션 슬롯 범위가 달라졌습니다. action={legalAction.ActionId}, slot={slotIndex}, count={potionSlots.Count}";
            return true;
        }

        object? currentPotion = ExtractRuntimePotionModel(potionSlots[slotIndex]);
        if (currentPotion is null)
        {
            result = "applied";
            detail = $"포션 행동 적용 확인: 슬롯의 포션이 소비되었습니다. action={legalAction.ActionId}, slot={slotIndex}, expected_potion={legalAction.PotionId ?? "<unknown>"}, input_detail={pending.InputDetail}";
            return true;
        }

        string? currentPotionId = ReadObjectId(currentPotion);
        if (!string.IsNullOrWhiteSpace(legalAction.PotionId)
            && !string.IsNullOrWhiteSpace(currentPotionId)
            && !IsSameShopModelId(currentPotionId, legalAction.PotionId))
        {
            result = "applied";
            detail = $"포션 행동 적용 확인: 해당 슬롯의 포션이 다른 포션으로 바뀌었습니다. action={legalAction.ActionId}, slot={slotIndex}, expected_potion={legalAction.PotionId}, current_potion={currentPotionId}, input_detail={pending.InputDetail}";
            return true;
        }

        bool isQueued = ReadFirstBool(new[] { currentPotion }, "IsQueued", "isQueued", "_isQueued") == true;
        if (isQueued && nowMs - pending.StartedAtMs <= 5000)
        {
            return false;
        }

        detail = $"포션 행동 적용 실패: 큐는 비었지만 슬롯의 포션이 그대로 남아 있습니다. action={legalAction.ActionId}, slot={slotIndex}, expected_potion={legalAction.PotionId ?? "<unknown>"}, current_potion={currentPotionId ?? "<unknown>"}, is_queued={isQueued}, input_detail={pending.InputDetail}";
        return true;
    }

    private static bool ShouldWaitForCombatActionQueueToSettle(
        PendingCombatActionConfirmation pending,
        long nowMs,
        string? currentStateId,
        CombatStateBridgePoster.PostedStateSnapshot? postedState)
    {
        if (!pending.LegalAction.ActionType.Equals("play_card", StringComparison.OrdinalIgnoreCase)
            && !pending.LegalAction.ActionType.Equals("use_potion", StringComparison.OrdinalIgnoreCase)
            && !pending.LegalAction.ActionType.Equals("end_turn", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        pending.FirstObservedChangeAtMs = pending.FirstObservedChangeAtMs == 0
            ? nowMs
            : pending.FirstObservedChangeAtMs;
        pending.FirstObservedRuntimeStateId ??= currentStateId;
        pending.FirstObservedPostedStateId ??= postedState?.StateId;

        if (AdapterCardSelectionBridge.HasPendingSelection)
        {
            CombatStateExporter.TryExportAdapterCardSelectionStateIfPending();
            return true;
        }

        if (MayRequireAdapterCardSelection(pending.LegalAction)
            && pending.ActiveCardSelectorScope is not null
            && pending.FirstObservedChangeAtMs > 0
            && nowMs - pending.FirstObservedChangeAtMs < 5000)
        {
            CombatStateExporter.TryExportAdapterCardSelectionStateIfPending();
            return true;
        }

        if (IsPlayerDrivenActionQueueSettled())
        {
            return false;
        }

        if (pending.FirstObservedChangeAtMs > 0
            && nowMs - pending.FirstObservedChangeAtMs >= 5000)
        {
            Logger.Warning(
                $"전투 행동 상태 변화는 확인했지만 action queue가 오래 비지 않아 결과 보고를 진행합니다. action={pending.LegalAction.ActionId}, first_runtime_state={pending.FirstObservedRuntimeStateId ?? "<none>"}, first_posted_state={pending.FirstObservedPostedStateId ?? "<none>"}");
            return false;
        }

        if (nowMs - pending.LastForceExportAtMs >= CombatActionConfirmationForceExportIntervalMs)
        {
            pending.LastForceExportAtMs = nowMs;
            CombatStateExporter.ForceExportFromCombatManager("combat_action_queue_settle");
        }

        return true;
    }

    private static bool MayRequireAdapterCardSelection(LegalActionSnapshot legalAction)
    {
        return IsHeadbuttAction(legalAction)
            || IsArmamentsAction(legalAction);
    }

    private static bool IsHeadbuttAction(LegalActionSnapshot legalAction)
    {
        string text = $"{legalAction.CardId ?? string.Empty} {legalAction.CardName ?? string.Empty}";
        return text.Contains("HEADBUTT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("박치기", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsArmamentsAction(LegalActionSnapshot legalAction)
    {
        string text = $"{legalAction.CardId ?? string.Empty} {legalAction.CardName ?? string.Empty}";
        return text.Contains("ARMAMENTS", StringComparison.OrdinalIgnoreCase)
            || text.Contains("전투장비", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlayerDrivenActionQueueSettled()
    {
        object? runManager = ReadStaticNamedMember(
            AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager"),
            "Instance");
        object? actionExecutor = ReadNamedMember(runManager, "ActionExecutor");
        object? runningAction = ReadNamedMember(actionExecutor, "CurrentlyRunningAction");
        if (IsPlayerDrivenGameAction(runningAction))
        {
            return false;
        }

        object? queueSet = ReadNamedMember(runManager, "ActionQueueSet");
        if (TryFindQueuedPlayerDrivenAction(queueSet, out _))
        {
            return false;
        }

        return true;
    }

    private static bool TryFindQueuedPlayerDrivenAction(object? actionQueueSet, out object? queuedAction)
    {
        queuedAction = null;
        if (actionQueueSet is null)
        {
            return false;
        }

        object? queuesObject = ReadNamedMember(actionQueueSet, "_actionQueues")
            ?? ReadNamedMember(actionQueueSet, "actionQueues")
            ?? ReadNamedMember(actionQueueSet, "ActionQueues");
        if (queuesObject is not IEnumerable queues)
        {
            return false;
        }

        foreach (object? queue in queues)
        {
            object? actionsObject = ReadNamedMember(queue, "actions")
                ?? ReadNamedMember(queue, "_actions")
                ?? ReadNamedMember(queue, "Actions");
            if (actionsObject is not IEnumerable actions)
            {
                continue;
            }

            foreach (object? action in actions)
            {
                if (IsPlayerDrivenGameAction(action))
                {
                    queuedAction = action;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsPlayerDrivenGameAction(object? action)
    {
        if (action is null || IsCompletedGameAction(action))
        {
            return false;
        }

        Type? actionQueueSetType = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.ActionQueueSet");
        MethodInfo? isPlayerDrivenMethod = actionQueueSetType?
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "IsGameActionPlayerDriven"
                && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType.IsAssignableFrom(action.GetType()));
        if (isPlayerDrivenMethod is null)
        {
            return true;
        }

        try
        {
            return isPlayerDrivenMethod.Invoke(null, new[] { action }) is true;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsCompletedGameAction(object action)
    {
        object? completionTask = ReadNamedMember(action, "CompletionTask");
        if (completionTask is Task task && task.IsCompleted)
        {
            return true;
        }

        object? state = ReadNamedMember(action, "State");
        if (state is null)
        {
            return false;
        }

        string stateText = state.ToString() ?? string.Empty;
        return stateText.Equals("Complete", StringComparison.OrdinalIgnoreCase)
            || stateText.Equals("Completed", StringComparison.OrdinalIgnoreCase)
            || stateText.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
            || stateText.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
            || stateText.Equals("Failed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySubscribeRoomEntered(out object? runManager, out Delegate? handler, out string detail)
    {
        runManager = null;
        handler = null;
        detail = string.Empty;
        try
        {
            Type? runManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager");
            runManager = ReadStaticNamedMember(runManagerType, "Instance");
            if (runManager is null)
            {
                detail = "RunManager.Instance??筌≪뼚? 筌륁궢六??щ빍??";
                return false;
            }

            EventInfo? roomEnteredEvent = runManager.GetType()
                .GetEvent("RoomEntered", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (roomEnteredEvent?.EventHandlerType is null)
            {
                detail = "RunManager.RoomEntered ??源?紐? 筌≪뼚? 筌륁궢六??щ빍??";
                return false;
            }

            MethodInfo? handlerMethod = typeof(CombatActionExecutor)
                .GetMethod(nameof(OnRoomEnteredForMapWait), BindingFlags.Static | BindingFlags.NonPublic);
            if (handlerMethod is null)
            {
                detail = "RoomEntered ?紐껊굶??筌롫뗄苑??? 筌≪뼚? 筌륁궢六??щ빍??";
                return false;
            }

            handler = Delegate.CreateDelegate(roomEnteredEvent.EventHandlerType, handlerMethod);
            roomEnteredEvent.AddEventHandler(runManager, handler);
            return true;
        }
        catch (Exception exception)
        {
            detail = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static void UnsubscribeRoomEntered(object? runManager, Delegate? handler)
    {
        if (runManager is null || handler is null)
        {
            return;
        }

        try
        {
            EventInfo? roomEnteredEvent = runManager.GetType()
                .GetEvent("RoomEntered", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            roomEnteredEvent?.RemoveEventHandler(runManager, handler);
        }
        catch
        {
            // ??源???닌됰즴 ??곸젫 ??쎈솭????쇱벉 ??쎈뻬???怨밸샨??雅뚯눘? ??낅즲嚥??얜똻???뺣뼄.
        }
    }

    private static void OnRoomEnteredForMapWait()
    {
        Interlocked.Increment(ref roomEnteredSignalCount);
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
            failureReason = $"{methodName} 筌롫뗄苑??? 筌≪뼚? 筌륁궢六??щ빍??";
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
            failureReason = "Godot CallDeferred 筌롫뗄苑??? 筌≪뼚? 筌륁궢六??щ빍??";
            return false;
        }

        ParameterInfo[] callDeferredParameters = callDeferred.GetParameters();
        object? deferredMethodName = CreateGodotStringName(callDeferredParameters[0].ParameterType, methodName);
        if (deferredMethodName is null)
        {
            failureReason = "Godot StringName 揶쏅???筌띾슢諭?????곷뮸??덈뼄.";
            return false;
        }

        Type? argumentElementType = callDeferredParameters[1].ParameterType.GetElementType();
        if (argumentElementType is null)
        {
            failureReason = "CallDeferred ?紐꾩쁽 獄쏄퀣肉?????놁뱽 ??곴퐤??? 筌륁궢六??щ빍??";
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
        if (typeName.Contains("NPlayerHand", StringComparison.OrdinalIgnoreCase))
        {
            return ReadBool(source, "IsInCardSelection") == true;
        }

        return ContainsAny(
            typeName,
            "NDeckEnchantSelectScreen",
            "NDeckUpgradeSelectScreen",
            "NDeckTransformSelectScreen",
            "NDeckCardSelectScreen",
            "NChooseACardSelectionScreen",
            "NSimpleCardSelectScreen",
            "SimpleCardSelectScreen");
    }

    private static object? FindHandCardSelectionRoot()
    {
        Type? handType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand");
        object? staticHand = ReadStaticNamedMember(handType, "Instance");
        if (IsCardSelectionScreen(staticHand))
        {
            return staticHand;
        }

        Type? combatRoomType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom");
        object? combatRoom = ReadStaticNamedMember(combatRoomType, "Instance");
        object? combatUi = ReadNamedMember(combatRoom, "Ui");
        object? hand = ReadNamedMember(combatUi, "Hand");
        return IsCardSelectionScreen(hand) ? hand : null;
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

    private static string? ReadTreasureRelicHolderModelId(object holder)
    {
        object? relicNode = ReadNamedMember(holder, "Relic")
            ?? ReadNamedMember(holder, "_relic")
            ?? ReadNamedMember(holder, "relic");
        object? relicModel = ReadNamedMember(relicNode, "Model")
            ?? ReadNamedMember(relicNode, "_model")
            ?? ReadNamedMember(relicNode, "model")
            ?? ReadNamedMember(holder, "Model")
            ?? ReadNamedMember(holder, "_model")
            ?? ReadNamedMember(holder, "model");
        return ReadObjectId(relicModel) ?? ReadObjectId(relicNode) ?? ReadObjectId(holder);
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
        if (IsHandCardSelectionRoot(selectionScreen))
        {
            object? activeHolders = ReadNamedMember(selectionScreen, "ActiveHolders");
            List<object> handHolders = ExpandValue(activeHolders)
                .Where(IsHandCardHolder)
                .Where(holder => ReadBool(holder, "Visible") != false)
                .ToList();
            if (handHolders.Count > 0)
            {
                return handHolders;
            }

            object? holderContainer = ReadNamedMember(selectionScreen, "CardHolderContainer")
                ?? ReadNamedMember(selectionScreen, "_cardHolderContainer");
            return EnumerateNodeDescendants(holderContainer)
                .Where(IsHandCardHolder)
                .Where(holder => ReadBool(holder, "Visible") != false)
                .ToList();
        }

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

    private static bool IsHandCardSelectionRoot(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return typeName.Contains("NPlayerHand", StringComparison.OrdinalIgnoreCase)
            && ReadBool(value, "IsInCardSelection") == true;
    }

    private static bool IsHandCardHolder(object value)
    {
        string typeName = value.GetType().FullName ?? value.GetType().Name;
        return typeName.Contains("NHandCardHolder", StringComparison.OrdinalIgnoreCase);
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
        if (IsHandCardSelectionRoot(selectionScreen))
        {
            return ReadNamedMember(selectionScreen, "_selectModeConfirmButton")
                ?? ReadNamedMember(selectionScreen, "SelectModeConfirmButton")
                ?? ReadNamedMember(selectionScreen, "selectModeConfirmButton");
        }

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
            detail = "use_potion ??곕짗?癒?뮉 potion_slot_index揶쎛 ?袁⑹뒄??몃빍??";
            return false;
        }

        object? player = ResolveRuntimePlayerForAction();
        if (player is null)
        {
            detail = "??????????袁⑹뒄?????쟿??곷선 揶쏆빘猿쒐몴?筌≪뼚? 筌륁궢六??щ빍??";
            return false;
        }

        List<object?> potionSlots = ResolvePotionSlots(player).ToList();
        int slotIndex = legalAction.PotionSlotIndex.Value;
        if (slotIndex < 0 || slotIndex >= potionSlots.Count)
        {
            detail = $"?????????紐껊쑔??? 甕곕뗄?욅몴?甕곗щ선?????덈뼄. index={slotIndex}, count={potionSlots.Count}";
            return false;
        }

        object? potion = ExtractRuntimePotionModel(potionSlots[slotIndex]);
        if (potion is null)
        {
            detail = $"?醫뤾문?????????????쑴堉???됰뮸??덈뼄. index={slotIndex}";
            return false;
        }

        string? runtimePotionId = ReadObjectId(potion);
        if (!string.IsNullOrWhiteSpace(legalAction.PotionId)
            && !string.IsNullOrWhiteSpace(runtimePotionId)
            && !IsSameShopModelId(runtimePotionId, legalAction.PotionId))
        {
            detail = $"??????????????獄쏅뗀??????щ빍?? expected={legalAction.PotionId}, actual={runtimePotionId}";
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
            ? "??????곸벉"
            : ReadNamedMember(target, "LogName")?.ToString()
                ?? ReadNamedMember(target, "Name")?.ToString()
                ?? legalAction.TargetId
                ?? "<unknown>";
        detail = $"UsePotionAction ??낆젾 ?源껊궗: potion={runtimePotionId ?? legalAction.PotionId ?? "<unknown>"}, slot={slotIndex}, target={targetText}";
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
        string? targetType = ReadPotionTargetType(potion);
        string targetKind = ResolvePotionTargetKind(targetType);
        bool requiresTarget = legalAction.RequiresTarget == true || PotionRequiresTarget(targetType);
        if (!requiresTarget)
        {
            detail = "???怨몄뵠 ?袁⑹뒄 ??용뮉 ?????낅빍??";
            return true;
        }

        if (targetKind == "targeted_no_creature")
        {
            detail = "???곻㎗????怨몄뵠 ?袁⑤빒 ??μ뵬 ????????? ?袁⑹춦 ?癒?짗 ??쎈뻬??? ??녿뮸??덈뼄.";
            return false;
        }

        if (targetKind is "self" or "player" or "ally")
        {
            object? combatStateForPlayer = FindCombatState(potion)
                ?? FindCombatState(combatRoot)
                ?? FindCombatState(CombatStateExporter.GetLatestRuntimePlayer());
            target = ResolvePlayerCreatureForPotion(combatStateForPlayer);
            if (target is not null)
            {
                detail = $"????????????{targetKind}??筌띿쉸?????쟿??곷선 ???곻㎗?? 筌≪뼚釉??щ빍??";
                return true;
            }
        }

        object? combatState = FindCombatState(potion)
            ?? FindCombatState(combatRoot)
            ?? FindCombatState(CombatStateExporter.GetLatestRuntimePlayer());
        if (combatState is null)
        {
            detail = "포션 대상 확인 실패: CombatState를 찾지 못했습니다.";
            return false;
        }

        if (legalAction.TargetCombatId is not null)
        {
            target = FindCreatureByCombatId(combatState, legalAction.TargetCombatId.Value);
            if (target is not null)
            {
                detail = $"target_combat_id={legalAction.TargetCombatId.Value} 기준으로 포션 대상을 찾았습니다.";
                return true;
            }
        }

        if (TryParseEnemyIndex(legalAction.TargetId, out int enemyIndex))
        {
            target = EnumerateEnemies(combatState).Skip(enemyIndex).FirstOrDefault();
            if (target is not null)
            {
                detail = $"target_id={legalAction.TargetId} ??뽮퐣 ???怨몄뱽 筌≪뼚釉??щ빍??";
                return true;
            }
        }

        detail = $"???????怨몄뱽 筌≪뼚? 筌륁궢六??щ빍?? target_id={legalAction.TargetId}, target_combat_id={legalAction.TargetCombatId?.ToString() ?? "<none>"}";
        return false;
    }

    private static bool RuntimePotionRequiresTarget(object potion)
    {
        return PotionRequiresTarget(ReadPotionTargetType(potion));
    }

    private static string? ReadPotionTargetType(object potion)
    {
        return ReadNamedMember(potion, "TargetType")?.ToString()
            ?? ReadNamedMember(potion, "targetType")?.ToString()
            ?? ReadNamedMember(potion, "_targetType")?.ToString();
    }

    private static bool PotionRequiresTarget(string? targetType)
    {
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

    private static bool TryEnqueueUsePotionAction(object potion, object? target, out string detail)
    {
        try
        {
            if (TryInvokeMethod(potion, "EnqueueManualUse", out _, target))
            {
                detail = "PotionModel.EnqueueManualUse 호출 완료";
                return true;
            }

            detail = "PotionModel.EnqueueManualUse를 찾지 못했거나 호출하지 못했습니다.";
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
            detail = "play_card ??곕짗??combat_card_id揶쎛 ??곷뮸??덈뼄.";
            return false;
        }

        object? card = CombatStateExporter.FindLatestRuntimeHandCardByCombatCardId(legalAction.CombatCardId.Value);
        if (card is null && !TryGetCombatCard((uint)legalAction.CombatCardId.Value, out card, out detail))
        {
            return false;
        }

        if (card is null)
        {
            detail = $"NetCombatCardDb揶쎛 null 燁삳?諭띄몴?獄쏆꼹???됰뮸??덈뼄. combat_card_id={legalAction.CombatCardId.Value}";
            return false;
        }

        if (!IsCardInHand(card))
        {
            Logger.Warning(
                $"NetCombatCardDb card pile did not report Hand. Continuing because legal_actions were generated from the exported hand. combat_card_id={legalAction.CombatCardId.Value}");
        }

        if (!TryResolveCardTarget(legalAction, card, combatRoot, out object? target, out detail))
        {
            return false;
        }

        if (TryCheckCanPlayTargeting(card, target, out bool canPlay) && !canPlay)
        {
            detail = $"燁삳?諭띄몴??袁⑹삺 ???怨몃퓠 ?????????곷뮸??덈뼄. combat_card_id={legalAction.CombatCardId.Value}, target_id={legalAction.TargetId ?? "<none>"}";
            return false;
        }

        if (!TryInvokeTryManualPlay(card, target, out bool enqueued, out detail))
        {
            return false;
        }

        if (!enqueued)
        {
            detail = $"TryManualPlay揶쎛 false??獄쏆꼹???됰뮸??덈뼄. combat_card_id={legalAction.CombatCardId.Value}, target_id={legalAction.TargetId ?? "<none>"}";
            return false;
        }

        string cardName = ReadNamedMember(card, "Title")?.ToString()
            ?? ReadNamedMember(card, "Id")?.ToString()
            ?? $"combat_card_{legalAction.CombatCardId.Value}";
        string targetText = target is null
            ? "??????곸벉"
            : ReadNamedMember(target, "LogName")?.ToString()
                ?? ReadNamedMember(target, "Name")?.ToString()
                ?? legalAction.TargetId
                ?? "<unknown>";
        detail = $"PlayCardAction ??낆젾 ?源껊궗: card={cardName}, combat_card_id={legalAction.CombatCardId.Value}, target={targetText}";
        Logger.Info(detail);
        return true;
    }

    private static bool TryGetCombatCard(uint combatCardId, out object? card, out string detail)
    {
        card = null;
        Type? databaseType = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.Multiplayer.NetCombatCardDb");
        if (databaseType is null)
        {
            detail = "NetCombatCardDb ????놁뱽 筌≪뼚? 筌륁궢六??щ빍??";
            return false;
        }

        object? database = ReadStaticNamedMember(databaseType, "Instance");
        if (database is null)
        {
            detail = "NetCombatCardDb.Instance??筌≪뼚? 筌륁궢六??щ빍??";
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
            detail = "NetCombatCardDb.TryGetCard(uint, out CardModel)??筌≪뼚? 筌륁궢六??щ빍??";
            return false;
        }

        object?[] args = { combatCardId, null };
        try
        {
            object? result = tryGetCard.Invoke(database, args);
            if (result is true && args[1] is not null)
            {
                card = args[1];
                detail = "燁삳?諭띄몴?筌≪뼚釉??щ빍??";
                return true;
            }
        }
        catch (Exception exception)
        {
            detail = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }

        detail = $"NetCombatCardDb?癒?퐣 combat_card_id={combatCardId} 燁삳?諭띄몴?筌≪뼚? 筌륁궢六??щ빍??";
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
            detail = "???怨몄뵠 ?袁⑹뒄 ??용뮉 燁삳?諭??낅빍??";
            return true;
        }

        object? combatState = FindCombatState(card)
            ?? FindCombatState(combatRoot)
            ?? FindCombatState(CombatStateExporter.GetLatestRuntimePlayer());
        if (combatState is null)
        {
            detail = "燁삳?諭????怨몄뱽 筌≪뼐由??袁る립 CombatState??筌≪뼚? 筌륁궢六??щ빍??";
            return false;
        }

        if (legalAction.TargetCombatId is not null)
        {
            target = FindCreatureByCombatId(combatState, legalAction.TargetCombatId.Value);
            if (target is not null)
            {
                detail = $"target_combat_id={legalAction.TargetCombatId.Value} ???怨몄뱽 筌≪뼚釉??щ빍??";
                return true;
            }
        }

        if (TryParseEnemyIndex(legalAction.TargetId, out int enemyIndex))
        {
            target = EnumerateEnemies(combatState).Skip(enemyIndex).FirstOrDefault();
            if (target is not null)
            {
                detail = $"target_id={legalAction.TargetId} ??뽮퐣 ???怨몄뱽 筌≪뼚釉??щ빍??";
                return true;
            }
        }

        detail = $"???怨몄뱽 筌≪뼚? 筌륁궢六??щ빍?? target_id={legalAction.TargetId}, target_combat_id={legalAction.TargetCombatId?.ToString() ?? "<none>"}";
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

    private static object? ResolvePlayerCreatureForPotion(object? combatState)
    {
        object? player = CombatStateExporter.GetLatestRuntimePlayer() ?? ResolveRuntimePlayerForAction();
        object? playerCreature = ReadNamedMember(player, "Creature")
            ?? ReadNamedMember(player, "creature")
            ?? ReadNamedMember(player, "_creature");
        if (playerCreature is not null)
        {
            return playerCreature;
        }

        return combatState is null
            ? null
            : EnumeratePlayerCreatures(combatState).FirstOrDefault(IsLiveCreature);
    }

    private static IEnumerable<object> EnumeratePlayerCreatures(object combatState)
    {
        List<object> playerCreatures = ExpandValue(ReadNamedMember(combatState, "PlayerCreatures"))
            .Where(value => !IsScalar(value.GetType()))
            .ToList();
        if (playerCreatures.Count > 0)
        {
            return playerCreatures;
        }

        return EnumerateCreatures(combatState)
            .Where(creature => ReadBool(creature, "IsPlayer") == true || ReadNamedMember(creature, "Player") is not null);
    }

    private static bool IsLiveCreature(object creature)
    {
        bool? isAlive = ReadBool(creature, "IsAlive") ?? ReadBool(creature, "isAlive");
        if (isAlive is not null)
        {
            return isAlive.Value;
        }

        int? hp = ReadInt(creature, "Hp")
            ?? ReadInt(creature, "hp")
            ?? ReadInt(creature, "CurrentHp")
            ?? ReadInt(creature, "currentHp");
        return hp is null || hp.Value > 0;
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
            detail = "CardModel.TryManualPlay(Creature?)??筌≪뼚? 筌륁궢六??щ빍??";
            return false;
        }

        try
        {
            object? result = method.Invoke(card, new[] { target });
            if (result is bool boolean)
            {
                enqueued = boolean;
                detail = $"TryManualPlay 獄쏆꼹?싧첎? {boolean}";
                return true;
            }

            detail = $"TryManualPlay 獄쏆꼹??????놁뵠 bool???袁⑤뻸??덈뼄: {DescribeResult(result)}";
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
            Logger.Warning($"{source.GetType().Name}.{method.Name} ?紐꾪뀱 餓???됱뇚揶쎛 獄쏆뮇源??됰뮸??덈뼄. {exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
            return false;
        }
        catch (Exception exception)
        {
            Logger.Warning($"{source.GetType().Name}.{method.Name} ?紐꾪뀱????쎈솭??됰뮸??덈뼄. {exception.GetType().Name}: {exception.Message}");
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
        if (TryInvokeEndTurnButtonReleaseLogic(out detail))
        {
            return true;
        }

        Logger.Warning($"끝턴 버튼 내부 실행 함수 호출이 실패했습니다. PlayerCmd.EndTurn 직접 호출로 재시도합니다. {detail}");

        if (TryInvokeDirectEndTurnCommand(out detail))
        {
            return true;
        }

        Logger.Warning($"PlayerCmd.EndTurn 직접 호출이 실패했습니다. EndPlayerTurnAction 큐 등록으로 재시도합니다. {detail}");

        if (TryEnqueueEndPlayerTurnAction(out detail))
        {
            return true;
        }

        Logger.Warning($"EndPlayerTurnAction ????낆젾????쎈솭??됰뮸??덈뼄. UI ?紐껊굶???袁⑤궖???癒?퉳??몃빍?? {detail}");

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
                detail = $"???ル굝利??袁⑤궖 筌롫뗄苑???紐꾪뀱: {type.FullName}.{method.Name} 獄쏆꼹??{DescribeResult(result)}";
                Logger.Info(detail);
                return true;
            }
        }

        detail = "?ル슦紐???????곸뵠 ?紐꾪뀱??????덈뮉 ???ル굝利??袁⑤궖 筌롫뗄苑??? 筌≪뼚? 筌륁궢六??щ빍??";
        Logger.Warning(detail);
        return false;
    }

    private static bool TryInvokeEndTurnButtonReleaseLogic(out string detail)
    {
        try
        {
            Type? combatRoomType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom");
            object? combatRoom = ReadStaticNamedMember(combatRoomType, "Instance");
            object? combatUi = ReadNamedMember(combatRoom, "Ui");
            object? endTurnButton = ReadNamedMember(combatUi, "EndTurnButton");
            if (endTurnButton is null)
            {
                detail = "NCombatRoom.Instance.Ui.EndTurnButton을 찾지 못했습니다.";
                return false;
            }

            bool? isEnabled = ReadBool(endTurnButton, "IsEnabled");
            if (isEnabled == false)
            {
                detail = "끝턴 버튼 객체가 비활성 상태입니다.";
                return false;
            }

            if (!TryInvokeMethod(endTurnButton, "CallReleaseLogic", out _))
            {
                detail = "EndTurnButton.CallReleaseLogic 호출에 실패했습니다.";
                return false;
            }

            detail = "EndTurnButton.CallReleaseLogic 호출 완료";
            Logger.Info(detail);
            return true;
        }
        catch (Exception exception)
        {
            detail = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    private static bool TryInvokeDirectEndTurnCommand(out string detail)
    {
        try
        {
            object? player = ResolveRuntimePlayerForAction();
            if (player is null)
            {
                detail = "현재 플레이어 객체를 찾지 못했습니다.";
                return false;
            }

            if (IsPlayerReadyToEndTurn(player))
            {
                detail = "현재 플레이어는 이미 턴 종료 준비 상태입니다.";
                return true;
            }

            Type? playerCmdType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.PlayerCmd");
            if (playerCmdType is null)
            {
                detail = "PlayerCmd 타입을 찾지 못했습니다.";
                return false;
            }

            MethodInfo? endTurnMethod = playerCmdType
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name.Equals("EndTurn", StringComparison.OrdinalIgnoreCase))
                .Where(method => method.GetParameters().Length == 3)
                .FirstOrDefault(method =>
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    return IsArgumentCompatible(parameters[0].ParameterType, player)
                        && parameters[1].ParameterType == typeof(bool)
                        && (!parameters[2].ParameterType.IsValueType
                            || Nullable.GetUnderlyingType(parameters[2].ParameterType) is not null);
                });

            if (endTurnMethod is null)
            {
                detail = "PlayerCmd.EndTurn(Player, bool, Func<Task>?) 메서드를 찾지 못했습니다.";
                return false;
            }

            endTurnMethod.Invoke(null, new object?[] { player, true, null });
            int? roundNumber = ReadInt(ReadNamedMember(ReadNamedMember(player, "Creature"), "CombatState"), "RoundNumber");
            detail = $"PlayerCmd.EndTurn 직접 호출 완료: round={roundNumber?.ToString() ?? "<unknown>"}";
            Logger.Info(detail);
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
                detail = "LocalContext.GetMe()?癒?퐣 ???쟿??곷선??筌≪뼚? 筌륁궢六??щ빍??";
                return false;
            }

            if (IsPlayerReadyToEndTurn(player))
            {
                detail = "???쟿??곷선揶쎛 ??? ???ル굝利?餓Β???怨밴묶??낅빍?? ??롫즼?귐덈┛ ??곕짗?? ?節? ??녿뮸??덈뼄.";
                return true;
            }

            object? creature = ReadNamedMember(player, "Creature");
            object? combatState = ReadNamedMember(creature, "CombatState");
            int? roundNumber = ReadInt(combatState, "RoundNumber");
            if (roundNumber is null)
            {
                detail = "?袁⑹삺 ?袁る떮 ??깆뒲??甕곕뜇?뉒몴?筌≪뼚? 筌륁궢六??щ빍??";
                return false;
            }

            Type? actionType = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.EndPlayerTurnAction");
            if (actionType is null)
            {
                detail = "EndPlayerTurnAction ????놁뱽 筌≪뼚? 筌륁궢六??щ빍??";
                return false;
            }

            object? action = Activator.CreateInstance(actionType, player, roundNumber.Value);
            if (action is null)
            {
                detail = "EndPlayerTurnAction ?紐꾨뮞??곷뮞 ??밴쉐????쎈솭??됰뮸??덈뼄.";
                return false;
            }

            Type? runManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager");
            object? runManager = ReadStaticNamedMember(runManagerType, "Instance");
            object? synchronizer = ReadNamedMember(runManager, "ActionQueueSynchronizer");
            if (synchronizer is null)
            {
                detail = "ActionQueueSynchronizer??筌≪뼚? 筌륁궢六??щ빍??";
                return false;
            }

            MethodInfo? requestEnqueue = synchronizer.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name.Equals("RequestEnqueue", StringComparison.OrdinalIgnoreCase)
                    && method.GetParameters().Length == 1);
            if (requestEnqueue is null)
            {
                detail = "ActionQueueSynchronizer.RequestEnqueue 筌롫뗄苑??? 筌≪뼚? 筌륁궢六??щ빍??";
                return false;
            }

            requestEnqueue.Invoke(synchronizer, new[] { action });
            detail = $"EndPlayerTurnAction ????낆젾 ?源껊궗: round={roundNumber.Value}";
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

    private static void ReportResult(
        PendingClaim claim,
        string result,
        string note,
        CombatStateBridgePoster.PostedStateSnapshot? observedState = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                CombatStateBridgePoster.PostedStateSnapshot resultState = observedState ?? claim.PostedState;
                using CancellationTokenSource cancellation = new(1500);
                await CombatActionBridgeClient.ReportResultAsync(
                    claim.Action,
                    result,
                    resultState,
                    note,
                    cancellation.Token).ConfigureAwait(false);
                Logger.Info($"??곕짗 ??쎈뻬 野껉퀗??癰귣떯?? {result} ({claim.Action.SubmissionId})");
            }
            catch (Exception exception)
            {
                Logger.Warning($"??곕짗 ??쎈뻬 野껉퀗??癰귣떯?????쎈솭??됰뮸??덈뼄. {exception.GetType().Name}: {exception.Message}");
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

    private sealed record PendingPotionRewardClaim(
        int RewardIndex,
        LegalActionSnapshot LegalAction,
        PendingClaim Claim,
        long StartedAtMs);

    private sealed class PendingTreasureRelicClaim(
        LegalActionSnapshot legalAction,
        PendingClaim claim,
        object treasureRoom,
        string methodLabel,
        long startedAtMs)
    {
        public LegalActionSnapshot LegalAction { get; } = legalAction;
        public PendingClaim Claim { get; } = claim;
        public object TreasureRoom { get; } = treasureRoom;
        public string MethodLabel { get; } = methodLabel;
        public long StartedAtMs { get; } = startedAtMs;
        public int StableCompletionPollCount { get; set; }
    }

    private sealed record PendingCombatActionConfirmation(
        LegalActionSnapshot LegalAction,
        PendingClaim Claim,
        string InitialStateId,
        string InputDetail,
        long StartedAtMs)
    {
        public long LastForceExportAtMs { get; set; }
        public long FirstObservedChangeAtMs { get; set; }
        public string? FirstObservedRuntimeStateId { get; set; }
        public string? FirstObservedPostedStateId { get; set; }
        public IDisposable? ActiveCardSelectorScope { get; set; }
    }

    private sealed class PendingMapNodeSelection(
        LegalActionSnapshot legalAction,
        PendingClaim claim,
        object mapRoot,
        string nodeId,
        long roomEnteredBaseline,
        object? runManager,
        Delegate? roomEnteredHandler,
        string methodLabel,
        long startedAtMs)
    {
        public LegalActionSnapshot LegalAction { get; } = legalAction;
        public PendingClaim Claim { get; } = claim;
        public object MapRoot { get; } = mapRoot;
        public string NodeId { get; } = nodeId;
        public long RoomEnteredBaseline { get; } = roomEnteredBaseline;
        public object? RunManager { get; } = runManager;
        public Delegate? RoomEnteredHandler { get; } = roomEnteredHandler;
        public string MethodLabel { get; } = methodLabel;
        public long StartedAtMs { get; } = startedAtMs;
        public int StableCompletionPollCount { get; set; }
    }
}
