using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace SpireMindMod;

internal static class AdapterCardSelectionBridge
{
    private static readonly object SyncRoot = new();
    private static readonly SpireMindLogger Logger = new("SpireMind.CardSelect");
    private static readonly MethodInfo GetSelectedCardsBridgeMethod = typeof(AdapterCardSelectionBridge)
        .GetMethod(nameof(GetSelectedCardsBridge), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo GetSelectedCardRewardBridgeMethod = typeof(AdapterCardSelectionBridge)
        .GetMethod(nameof(GetSelectedCardRewardBridge), BindingFlags.Static | BindingFlags.NonPublic)!;

    private static Type? adapterSelectorType;
    private static PendingAdapterCardSelection? pendingSelection;

    internal static bool HasPendingSelection
    {
        get
        {
            lock (SyncRoot)
            {
                return pendingSelection is not null && !pendingSelection.Completion.Task.IsCompleted;
            }
        }
    }

    internal static PendingAdapterCardSelection? GetPendingSelectionSnapshot()
    {
        lock (SyncRoot)
        {
            return pendingSelection;
        }
    }

    internal static IDisposable? InstallSelectorForQueuedCardAction()
    {
        Type? cardSelectCmdType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.CardSelectCmd");
        Type? selectorInterfaceType = AccessTools.TypeByName("MegaCrit.Sts2.Core.TestSupport.ICardSelector");
        if (cardSelectCmdType is null || selectorInterfaceType is null)
        {
            Logger.Warning("CardSelectCmd 또는 ICardSelector 타입을 찾지 못해 카드 선택 프록시를 설치하지 못했습니다.");
            return null;
        }

        Type selectorType = adapterSelectorType ??= BuildAdapterSelectorType(selectorInterfaceType);
        object selector = Activator.CreateInstance(selectorType)!;
        object? currentSelector = cardSelectCmdType.GetProperty("Selector", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
        MethodInfo? installMethod = cardSelectCmdType
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == (currentSelector is null ? "UseSelector" : "PushSelector")
                && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType.IsAssignableFrom(selector.GetType()));

        if (installMethod is null)
        {
            Logger.Warning("CardSelectCmd 선택자 설치 메서드를 찾지 못했습니다.");
            return null;
        }

        IDisposable? scope = installMethod.Invoke(null, new[] { selector }) as IDisposable;
        Logger.Info($"CardSelectCmd 선택자 프록시를 설치했습니다. method={installMethod.Name}, selector_type={selector.GetType().FullName}, scope_created={scope is not null}");
        return scope;
    }

    internal static bool TryChoose(int selectionIndex, string? expectedSelectionId, out string detail)
    {
        lock (SyncRoot)
        {
            PendingAdapterCardSelection? pending = pendingSelection;
            if (pending is null || pending.Completion.Task.IsCompleted)
            {
                detail = "어댑터 카드 선택 대기가 없습니다.";
                return false;
            }

            if (selectionIndex < 0 || selectionIndex >= pending.Options.Count)
            {
                detail = $"카드 선택 번호가 범위를 벗어났습니다. index={selectionIndex}, count={pending.Options.Count}";
                return false;
            }

            AdapterCardSelectionOption option = pending.Options[selectionIndex];
            if (!string.IsNullOrWhiteSpace(expectedSelectionId)
                && !string.Equals(expectedSelectionId, option.CardSelectionId, StringComparison.Ordinal))
            {
                detail = $"카드 선택 대상이 관찰 시점과 다릅니다. expected={expectedSelectionId}, current={option.CardSelectionId}";
                return false;
            }

            if (pending.SelectedIndexes.Contains(selectionIndex))
            {
                detail = $"이미 선택된 카드입니다. index={selectionIndex}, card={option.Name}";
                return false;
            }

            if (pending.SelectedIndexes.Count >= pending.MaxSelect)
            {
                detail = $"더 선택할 수 없습니다. selected={pending.SelectedIndexes.Count}, max={pending.MaxSelect}";
                return false;
            }

            pending.SelectedIndexes.Add(selectionIndex);
            if (ShouldAutoConfirm(pending))
            {
                CompleteSelectionIfNeeded(pending, BuildSelectedCards(pending));
            }
            detail = $"어댑터 카드 선택을 기록했습니다. index={selectionIndex}, card={option.Name}, selected={pending.SelectedIndexes.Count}/{pending.MaxSelect}";
            Logger.Info(detail);
            return true;
        }
    }

    internal static bool TryConfirm(out string detail)
    {
        PendingAdapterCardSelection? pending;
        List<object> selectedCards;
        lock (SyncRoot)
        {
            pending = pendingSelection;
            if (pending is null || pending.Completion.Task.IsCompleted)
            {
                detail = "확정할 어댑터 카드 선택 대기가 없습니다.";
                return false;
            }

            if (pending.SelectedIndexes.Count < pending.MinSelect)
            {
                detail = $"선택 수가 부족합니다. selected={pending.SelectedIndexes.Count}, min={pending.MinSelect}";
                return false;
            }

            selectedCards = pending.SelectedIndexes
                .Select(index => pending.Options[index].Card)
                .ToList();
        }

        pending.Completion.TrySetResult(selectedCards);
        pending.OnConfirmed?.Invoke(selectedCards);
        detail = $"어댑터 카드 선택을 확정했습니다. selected={selectedCards.Count}";
        Logger.Info(detail);
        return true;
    }

    internal static bool TryChoose(
        int selectionIndex,
        string? expectedSelectionId,
        string? expectedCardId,
        string? expectedName,
        bool? expectedUpgraded,
        string? expectedPile,
        string? expectedPendingSelectionId,
        out string detail,
        out CardSelectionActionStatus status)
    {
        lock (SyncRoot)
        {
            PendingAdapterCardSelection? pending = pendingSelection;
            if (pending is null || pending.Completion.Task.IsCompleted)
            {
                status = CardSelectionActionStatus.Stale;
                detail = "어댑터 카드 선택 대기가 없습니다. 이미 닫혔거나 다른 상태로 전환됐습니다.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(expectedPendingSelectionId)
                && !string.Equals(expectedPendingSelectionId, pending.SelectionId, StringComparison.Ordinal))
            {
                status = CardSelectionActionStatus.Stale;
                detail = $"카드 선택 화면이 관찰 시점과 다릅니다. expected_selection={expectedPendingSelectionId}, current_selection={pending.SelectionId}";
                return false;
            }

            if (selectionIndex < 0 || selectionIndex >= pending.Options.Count)
            {
                status = CardSelectionActionStatus.Failed;
                detail = $"카드 선택 번호가 범위를 벗어났습니다. index={selectionIndex}, count={pending.Options.Count}";
                return false;
            }

            AdapterCardSelectionOption option = pending.Options[selectionIndex];
            if (!string.IsNullOrWhiteSpace(expectedSelectionId)
                && !string.Equals(expectedSelectionId, option.CardSelectionId, StringComparison.Ordinal))
            {
                status = CardSelectionActionStatus.Stale;
                detail = $"카드 선택 대상이 관찰 시점과 다릅니다. expected_id={expectedSelectionId}, current_id={option.CardSelectionId}";
                return false;
            }

            if (!MatchesExpectedOption(option, expectedCardId, expectedName, expectedUpgraded, expectedPile, out string mismatchDetail))
            {
                status = CardSelectionActionStatus.Stale;
                detail = $"카드 선택 후보가 관찰 시점과 다릅니다. index={selectionIndex}, {mismatchDetail}";
                return false;
            }

            if (pending.SelectedIndexes.Contains(selectionIndex))
            {
                status = CardSelectionActionStatus.Failed;
                detail = $"이미 선택된 카드입니다. index={selectionIndex}, card={option.Name}";
                return false;
            }

            if (pending.SelectedIndexes.Count >= pending.MaxSelect)
            {
                status = CardSelectionActionStatus.Failed;
                detail = $"더 선택할 수 없습니다. selected={pending.SelectedIndexes.Count}, max={pending.MaxSelect}";
                return false;
            }

            pending.SelectedIndexes.Add(selectionIndex);
            if (ShouldAutoConfirm(pending))
            {
                CompleteSelectionIfNeeded(pending, BuildSelectedCards(pending));
            }
            status = CardSelectionActionStatus.Applied;
            detail = $"어댑터 카드 선택을 기록했습니다. selection={pending.SelectionId}, index={selectionIndex}, card={option.Name}, selected={pending.SelectedIndexes.Count}/{pending.MaxSelect}";
            Logger.Info(detail);
            return true;
        }
    }

    internal static bool TryConfirm(
        string? expectedPendingSelectionId,
        int? expectedSelectedCount,
        out string detail,
        out CardSelectionActionStatus status)
    {
        PendingAdapterCardSelection? pending;
        List<object> selectedCards;
        lock (SyncRoot)
        {
            pending = pendingSelection;
            if (pending is null || pending.Completion.Task.IsCompleted)
            {
                status = CardSelectionActionStatus.Stale;
                detail = "확정할 어댑터 카드 선택 대기가 없습니다. 이미 닫혔거나 다른 상태로 전환됐습니다.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(expectedPendingSelectionId)
                && !string.Equals(expectedPendingSelectionId, pending.SelectionId, StringComparison.Ordinal))
            {
                status = CardSelectionActionStatus.Stale;
                detail = $"확정 대상 선택 화면이 관찰 시점과 다릅니다. expected_selection={expectedPendingSelectionId}, current_selection={pending.SelectionId}";
                return false;
            }

            if (expectedSelectedCount is not null && expectedSelectedCount.Value != pending.SelectedIndexes.Count)
            {
                status = CardSelectionActionStatus.Stale;
                detail = $"선택 개수가 관찰 시점과 다릅니다. expected_selected={expectedSelectedCount.Value}, current_selected={pending.SelectedIndexes.Count}";
                return false;
            }

            if (pending.SelectedIndexes.Count < pending.MinSelect)
            {
                status = CardSelectionActionStatus.Failed;
                detail = $"선택 수가 부족합니다. selected={pending.SelectedIndexes.Count}, min={pending.MinSelect}";
                return false;
            }

            if (pending.SelectedIndexes.Count > pending.MaxSelect)
            {
                status = CardSelectionActionStatus.Failed;
                detail = $"선택 수가 최대치를 넘었습니다. selected={pending.SelectedIndexes.Count}, max={pending.MaxSelect}";
                return false;
            }

            selectedCards = pending.SelectedIndexes
                .Select(index => pending.Options[index].Card)
                .ToList();
        }

        pending.Completion.TrySetResult(selectedCards);
        pending.OnConfirmed?.Invoke(selectedCards);
        status = CardSelectionActionStatus.Applied;
        detail = $"어댑터 카드 선택을 확정했습니다. selection={pending.SelectionId}, selected={selectedCards.Count}";
        Logger.Info(detail);
        return true;
    }

    internal static bool TryCancel(out string detail)
    {
        PendingAdapterCardSelection? pending;
        lock (SyncRoot)
        {
            pending = pendingSelection;
            if (pending is null || pending.Completion.Task.IsCompleted)
            {
                detail = "취소할 어댑터 카드 선택 대기가 없습니다.";
                return false;
            }

            if (pending.MinSelect > 0)
            {
                detail = $"이 선택은 필수 선택입니다. selected={pending.SelectedIndexes.Count}, min={pending.MinSelect}";
                return false;
            }
        }

        pending.Completion.TrySetResult(new List<object>());
        detail = "어댑터 카드 선택을 취소했습니다.";
        Logger.Info(detail);
        return true;
    }

    internal static bool BeginManualSelection(
        string selectionIdPrefix,
        IEnumerable<object> options,
        int minSelect,
        int maxSelect,
        Action<IReadOnlyList<object>> onConfirmed,
        out string detail)
    {
        List<object> cards = options.ToList();
        if (cards.Count == 0)
        {
            detail = "수동 카드 선택 후보가 없습니다.";
            return false;
        }

        PendingAdapterCardSelection pending = CreatePendingSelection(
            $"{selectionIdPrefix}_{Environment.TickCount64}",
            cards,
            minSelect,
            maxSelect,
            onConfirmed);

        lock (SyncRoot)
        {
            pendingSelection = pending;
        }

        detail = $"수동 어댑터 카드 선택 대기를 시작했습니다. id={pending.SelectionId}, options={pending.Options.Count}, min={pending.MinSelect}, max={pending.MaxSelect}";
        Logger.Info(detail);
        return true;
    }

    internal static async Task<IReadOnlyList<TCard>> RequestSelectionAsync<TCard>(
        IEnumerable<TCard> options,
        int minSelect,
        int maxSelect)
        where TCard : class
    {
        PendingAdapterCardSelection pending = BeginSelection(options.Cast<object>(), minSelect, maxSelect);
        IReadOnlyList<object> selected = await pending.Completion.Task.ConfigureAwait(false);

        lock (SyncRoot)
        {
            if (ReferenceEquals(pendingSelection, pending))
            {
                pendingSelection = null;
            }
        }

        return selected.Cast<TCard>().ToList();
    }

    private static Type BuildAdapterSelectorType(Type selectorInterfaceType)
    {
        AssemblyName assemblyName = new("SpireMindAdapterCardSelectorRuntime");
        AssemblyBuilder assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name!);
        TypeBuilder typeBuilder = moduleBuilder.DefineType(
            "SpireMindMod.RuntimeAdapterCardSelector",
            TypeAttributes.Public | TypeAttributes.Sealed);
        typeBuilder.AddInterfaceImplementation(selectorInterfaceType);
        typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);

        foreach (MethodInfo interfaceMethod in selectorInterfaceType.GetMethods())
        {
            ParameterInfo[] parameters = interfaceMethod.GetParameters();
            Type[] parameterTypes = parameters.Select(parameter => parameter.ParameterType).ToArray();
            MethodBuilder methodBuilder = typeBuilder.DefineMethod(
                interfaceMethod.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                interfaceMethod.ReturnType,
                parameterTypes);
            ILGenerator il = methodBuilder.GetILGenerator();

            if (interfaceMethod.Name == "GetSelectedCards" && parameterTypes.Length == 3)
            {
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Call, GetSelectedCardsBridgeMethod);
                il.Emit(OpCodes.Castclass, interfaceMethod.ReturnType);
                il.Emit(OpCodes.Ret);
            }
            else if (interfaceMethod.Name == "GetSelectedCardReward" && parameterTypes.Length == 2)
            {
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Call, GetSelectedCardRewardBridgeMethod);
                if (interfaceMethod.ReturnType.IsValueType)
                {
                    il.Emit(OpCodes.Unbox_Any, interfaceMethod.ReturnType);
                }
                else
                {
                    il.Emit(OpCodes.Castclass, interfaceMethod.ReturnType);
                }
                il.Emit(OpCodes.Ret);
            }
            else
            {
                throw new NotSupportedException($"지원하지 않는 ICardSelector 메서드입니다. method={interfaceMethod.Name}");
            }

            typeBuilder.DefineMethodOverride(methodBuilder, interfaceMethod);
        }

        return typeBuilder.CreateType()!;
    }

    private static object GetSelectedCardsBridge(object options, int minSelect, int maxSelect)
    {
        Type? cardType = options.GetType()
            .GetInterfaces()
            .Concat(new[] { options.GetType() })
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(type => type.GetGenericArguments()[0])
            .FirstOrDefault();
        if (cardType is null)
        {
            throw new InvalidOperationException("카드 선택 옵션의 카드 타입을 확인하지 못했습니다.");
        }

        MethodInfo genericMethod = typeof(AdapterCardSelectionBridge)
            .GetMethod(nameof(GetSelectedCardsTyped), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(cardType);
        return genericMethod.Invoke(null, new[] { options, minSelect, maxSelect })!;
    }

    private static async Task<IEnumerable<TCard>> GetSelectedCardsTyped<TCard>(IEnumerable<TCard> options, int minSelect, int maxSelect)
        where TCard : class
    {
        return await RequestSelectionAsync(options, minSelect, maxSelect).ConfigureAwait(false);
    }

    private static object? GetSelectedCardRewardBridge(object options, object alternatives)
    {
        foreach (object? option in EnumerateObjects(options))
        {
            object? card = ReadNamedMember(option, "Card", "card");
            if (card is not null)
            {
                return card;
            }
        }

        return null;
    }

    private static PendingAdapterCardSelection BeginSelection(IEnumerable<object> options, int minSelect, int maxSelect)
    {
        List<object> cards = options.ToList();
        PendingAdapterCardSelection pending = CreatePendingSelection(
            $"adapter_card_selection_{Environment.TickCount64}",
            cards,
            minSelect,
            maxSelect,
            null);

        lock (SyncRoot)
        {
            pendingSelection = pending;
        }

        Logger.Info($"어댑터 카드 선택 대기를 시작했습니다. id={pending.SelectionId}, options={pending.Options.Count}, min={pending.MinSelect}, max={pending.MaxSelect}");
        return pending;
    }

    private static PendingAdapterCardSelection CreatePendingSelection(
        string selectionId,
        IReadOnlyList<object> cards,
        int minSelect,
        int maxSelect,
        Action<IReadOnlyList<object>>? onConfirmed)
    {
        return new PendingAdapterCardSelection(
            selectionId,
            Math.Max(0, minSelect),
            Math.Max(0, maxSelect),
            cards.Select((card, index) => new AdapterCardSelectionOption(
                card,
                index,
                BuildCardSelectionId(index, card),
                ReadCardId(card),
                ReadCardName(card),
                ReadCardUpgraded(card),
                InferPileName(card))).ToList(),
            new TaskCompletionSource<IReadOnlyList<object>>(TaskCreationOptions.RunContinuationsAsynchronously),
            Environment.TickCount64,
            onConfirmed);
    }

    private static string BuildCardSelectionId(int index, object card)
    {
        string upgraded = ReadCardUpgraded(card) ? "upgraded" : "base";
        return SanitizeActionId($"card_selection_adapter_{index}_{ReadCardId(card)}_{ReadCardName(card)}_{upgraded}");
    }

    private static string ReadCardId(object card)
    {
        object? id = ReadNamedMember(card, "Id", "id", "_id");
        return ReadNamedMember(id, "Entry", "entry")?.ToString()
            ?? id?.ToString()
            ?? ReadCardName(card);
    }

    private static string ReadCardName(object card)
    {
        return ReadNamedMember(card, "Title", "title", "Name", "name")?.ToString()
            ?? ReadCardIdFallback(card);
    }

    private static string ReadCardIdFallback(object card)
    {
        object? id = ReadNamedMember(card, "Id", "id", "_id");
        return id?.ToString() ?? card.GetType().Name;
    }

    private static bool ReadCardUpgraded(object card)
    {
        object? value = ReadNamedMember(card, "IsUpgraded", "isUpgraded", "upgraded");
        return value is bool boolean && boolean;
    }

    private static string? InferPileName(object card)
    {
        object? pile = ReadNamedMember(card, "Pile", "pile", "_pile");
        string text = ReadNamedMember(pile, "Type", "type")?.ToString()
            ?? pile?.ToString()
            ?? string.Empty;
        if (text.Contains("Discard", StringComparison.OrdinalIgnoreCase))
        {
            return "discard_pile";
        }

        if (text.Contains("Draw", StringComparison.OrdinalIgnoreCase))
        {
            return "draw_pile";
        }

        if (text.Contains("Hand", StringComparison.OrdinalIgnoreCase))
        {
            return "hand";
        }

        if (text.Contains("Deck", StringComparison.OrdinalIgnoreCase))
        {
            return "deck";
        }

        return string.IsNullOrWhiteSpace(text) ? null : SanitizeActionId(text);
    }

    private static bool MatchesExpectedOption(
        AdapterCardSelectionOption option,
        string? expectedCardId,
        string? expectedName,
        bool? expectedUpgraded,
        string? expectedPile,
        out string detail)
    {
        if (!string.IsNullOrWhiteSpace(expectedCardId)
            && !string.Equals(NormalizeCardToken(expectedCardId), NormalizeCardToken(option.CardId), StringComparison.Ordinal))
        {
            detail = $"card_id 불일치: expected={expectedCardId}, current={option.CardId}";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedName)
            && !string.Equals(expectedName, option.Name, StringComparison.OrdinalIgnoreCase))
        {
            detail = $"name 불일치: expected={expectedName}, current={option.Name}";
            return false;
        }

        if (expectedUpgraded is not null && expectedUpgraded.Value != option.Upgraded)
        {
            detail = $"upgraded 불일치: expected={expectedUpgraded.Value}, current={option.Upgraded}";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedPile)
            && !string.Equals(NormalizePileName(expectedPile), NormalizePileName(option.Pile), StringComparison.Ordinal))
        {
            detail = $"pile 불일치: expected={expectedPile}, current={option.Pile ?? "<none>"}";
            return false;
        }

        detail = "일치";
        return true;
    }

    private static bool ShouldAutoConfirm(PendingAdapterCardSelection pending)
    {
        return pending.MinSelect == pending.MaxSelect
            && pending.SelectedIndexes.Count >= pending.MinSelect
            && pending.SelectedIndexes.Count >= pending.MaxSelect;
    }

    private static List<object> BuildSelectedCards(PendingAdapterCardSelection pending)
    {
        return pending.SelectedIndexes
            .Select(index => pending.Options[index].Card)
            .ToList();
    }

    private static void CompleteSelectionIfNeeded(
        PendingAdapterCardSelection pending,
        IReadOnlyList<object> selectedCards)
    {
        pending.Completion.TrySetResult(selectedCards);
        pending.OnConfirmed?.Invoke(selectedCards);
        Logger.Info($"어댑터 카드 선택을 자동 확정했습니다. selection={pending.SelectionId}, selected={selectedCards.Count}");
    }

    private static string NormalizeCardToken(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith("CARD.", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[5..];
        }

        return SanitizeActionId(normalized);
    }

    private static string NormalizePileName(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : SanitizeActionId(normalized);
    }

    private static object? ReadNamedMember(object? source, params string[] memberNames)
    {
        if (source is null)
        {
            return null;
        }

        Type type = source.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (string memberName in memberNames)
        {
            PropertyInfo? property = type.GetProperty(memberName, flags);
            if (property is not null && property.GetIndexParameters().Length == 0)
            {
                try
                {
                    return property.GetValue(source);
                }
                catch
                {
                    // 리플렉션 대상 속성이 내부 상태에 따라 예외를 낼 수 있어 다음 후보로 넘어갑니다.
                }
            }

            FieldInfo? field = type.GetField(memberName, flags);
            if (field is not null)
            {
                try
                {
                    return field.GetValue(source);
                }
                catch
                {
                    // 위와 동일하게 다음 후보를 확인합니다.
                }
            }
        }

        return null;
    }

    private static IEnumerable<object?> EnumerateObjects(object? source)
    {
        if (source is IEnumerable enumerable && source is not string)
        {
            foreach (object? item in enumerable)
            {
                yield return item;
            }
        }
    }

    private static string SanitizeActionId(string value)
    {
        char[] chars = value.Select(character =>
            char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_').ToArray();
        return new string(chars).Trim('_');
    }
}

internal sealed record PendingAdapterCardSelection(
    string SelectionId,
    int MinSelect,
    int MaxSelect,
    IReadOnlyList<AdapterCardSelectionOption> Options,
    TaskCompletionSource<IReadOnlyList<object>> Completion,
    long StartedAtMs,
    Action<IReadOnlyList<object>>? OnConfirmed)
{
    public List<int> SelectedIndexes { get; } = new();
}

internal sealed record AdapterCardSelectionOption(
    object Card,
    int Index,
    string CardSelectionId,
    string CardId,
    string Name,
    bool Upgraded,
    string? Pile);

internal enum CardSelectionActionStatus
{
    Applied,
    Failed,
    Stale
}
