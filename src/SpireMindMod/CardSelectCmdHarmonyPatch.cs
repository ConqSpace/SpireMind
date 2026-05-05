using System.Collections;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace SpireMindMod;

[HarmonyPatch]
internal static class CardSelectCmdHarmonyPatch
{
    private static readonly SpireMindLogger Logger = new("SpireMind.CardSelectPatch");

    private static IEnumerable<MethodBase> TargetMethods()
    {
        Type? cardSelectCmdType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.CardSelectCmd");
        if (cardSelectCmdType is null)
        {
            yield break;
        }

        foreach (MethodInfo method in cardSelectCmdType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (method.Name == "FromHandForUpgrade" && parameters.Length == 3)
            {
                yield return method;
            }
            else if (method.Name == "FromHand" && parameters.Length == 5)
            {
                yield return method;
            }
            else if (method.Name == "FromHandForDiscard" && parameters.Length == 5)
            {
                yield return method;
            }
            else if (method.Name == "FromSimpleGrid" && parameters.Length == 4)
            {
                yield return method;
            }
            else if (method.Name == "FromChooseACardScreen" && parameters.Length == 4)
            {
                yield return method;
            }
        }
    }

    private static bool Prefix(MethodBase __originalMethod, object[] __args, ref object __result)
    {
        return __originalMethod.Name switch
        {
            "FromHandForUpgrade" => PrefixFromHandForUpgrade(__args[0], __args[1], __args[2], ref __result),
            "FromHand" => PrefixFromHand(__args[0], __args[1], __args[2], __args[3], __args[4], ref __result),
            "FromHandForDiscard" => PrefixFromHandForDiscard(__args[0], __args[1], __args[2], __args[3], __args[4], ref __result),
            "FromSimpleGrid" => PrefixFromSimpleGrid(__args[0], __args[1], __args[2], __args[3], ref __result),
            "FromChooseACardScreen" => PrefixFromChooseACardScreen(__args[0], __args[1], __args[2], __args[3], ref __result),
            _ => true
        };
    }

    private static bool PrefixFromHandForUpgrade(object context, object player, object source, ref object __result)
    {
        List<object> candidates = GetHandCards(player)
            .Where(card => ReadBool(card, "IsUpgradable") == true)
            .ToList();
        if (candidates.Count <= 1)
        {
            return true;
        }

        Type cardType = ResolveCardModelBaseType(candidates);
        MethodInfo method = typeof(CardSelectCmdHarmonyPatch)
            .GetMethod(nameof(SelectOneForUpgradeTyped), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(cardType);
        __result = method.Invoke(null, new object[] { context, candidates, player })!;

        Logger.Info($"손패 강화 선택을 어댑터 선택으로 전환했습니다. options={candidates.Count}, source={ReadModelId(source)}");
        return false;
    }

    private static bool PrefixFromHand(object context, object player, object prefs, object? filter, object source, ref object __result)
    {
        List<object> candidates = GetHandCards(player)
            .Where(card => MatchesCardFilter(card, filter))
            .ToList();
        int minSelect = ReadInt(prefs, "MinSelect") ?? 1;
        int maxSelect = ReadInt(prefs, "MaxSelect") ?? minSelect;
        bool requireManualConfirmation = ReadBool(prefs, "RequireManualConfirmation") == true;
        if (candidates.Count == 0 || (!requireManualConfirmation && candidates.Count <= minSelect))
        {
            return true;
        }

        Type cardType = ResolveCardModelBaseType(candidates);
        MethodInfo method = typeof(CardSelectCmdHarmonyPatch)
            .GetMethod(nameof(SelectFromHandWithPlayerChoiceTyped), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(cardType);
        __result = method.Invoke(null, new object[] { context, candidates, player, minSelect, maxSelect })!;

        Logger.Info($"FromHand 손패 선택을 어댑터 선택으로 전환했습니다. options={candidates.Count}, min={minSelect}, max={maxSelect}, source={ReadModelId(source)}, card_type={cardType.FullName}");
        return false;
    }

    private static bool PrefixFromHandForDiscard(object context, object player, object prefs, object? filter, object source, ref object __result)
    {
        List<object> candidates = GetHandCards(player)
            .Where(card => MatchesCardFilter(card, filter))
            .ToList();
        int minSelect = ReadInt(prefs, "MinSelect") ?? 1;
        int maxSelect = ReadInt(prefs, "MaxSelect") ?? minSelect;
        bool requireManualConfirmation = ReadBool(prefs, "RequireManualConfirmation") == true;
        if (candidates.Count == 0 || (!requireManualConfirmation && candidates.Count <= minSelect))
        {
            return true;
        }

        Type cardType = ResolveCardModelBaseType(candidates);
        MethodInfo method = typeof(CardSelectCmdHarmonyPatch)
            .GetMethod(nameof(SelectFromHandWithPlayerChoiceTyped), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(cardType);
        __result = method.Invoke(null, new object[] { context, candidates, player, minSelect, maxSelect })!;

        Logger.Info($"FromHandForDiscard 손패 버리기 선택을 어댑터 선택으로 전환했습니다. options={candidates.Count}, min={minSelect}, max={maxSelect}, source={ReadModelId(source)}, card_type={cardType.FullName}");
        return false;
    }

    private static bool PrefixFromSimpleGrid(object context, object cardsIn, object player, object prefs, ref object __result)
    {
        List<object> candidates = EnumerateObjects(cardsIn).ToList();
        int minSelect = ReadInt(prefs, "MinSelect") ?? 1;
        int maxSelect = ReadInt(prefs, "MaxSelect") ?? minSelect;
        bool requireManualConfirmation = ReadBool(prefs, "RequireManualConfirmation") == true;
        if (candidates.Count == 0 || (!requireManualConfirmation && candidates.Count <= minSelect))
        {
            return true;
        }

        Type cardType = ResolveCardModelBaseType(candidates);
        MethodInfo method = typeof(CardSelectCmdHarmonyPatch)
            .GetMethod(nameof(SelectFromSimpleGridWithPlayerChoiceTyped), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(cardType);
        __result = method.Invoke(null, new object[] { context, candidates, player, minSelect, maxSelect })!;

        Logger.Info($"Headbutt 버림 더미 선택을 어댑터 선택으로 전환했습니다. 원본 player choice 신호를 유지합니다. options={candidates.Count}, min={minSelect}, max={maxSelect}, card_type={cardType.FullName}");
        return false;
    }

    private static bool PrefixFromChooseACardScreen(object context, object cardsIn, object player, object canSkipValue, ref object __result)
    {
        List<object> candidates = EnumerateObjects(cardsIn).ToList();
        if (candidates.Count == 0)
        {
            return true;
        }

        bool canSkip = canSkipValue is bool value && value;
        Type cardType = ResolveCardModelBaseType(candidates);
        MethodInfo method = typeof(CardSelectCmdHarmonyPatch)
            .GetMethod(nameof(SelectFromChooseACardScreenTyped), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(cardType);
        __result = method.Invoke(null, new object[] { context, candidates, player, canSkip })!;

        Logger.Info($"FromChooseACardScreen 생성 카드 선택을 어댑터 선택으로 전환했습니다. options={candidates.Count}, can_skip={canSkip}, card_type={cardType.FullName}");
        return false;
    }

    private static Type ResolveCardModelBaseType(IReadOnlyList<object> candidates)
    {
        Type? cardModelType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Models.CardModel");
        if (cardModelType is not null)
        {
            return cardModelType;
        }

        Type current = candidates[0].GetType();
        while (current.BaseType is not null
            && candidates.Any(candidate => !current.IsInstanceOfType(candidate)))
        {
            current = current.BaseType;
        }

        return current;
    }

    private static async Task<TCard?> SelectOneForUpgradeTyped<TCard>(object context, List<object> candidateObjects, object player)
        where TCard : class
    {
        List<TCard> candidates = candidateObjects.Cast<TCard>().ToList();
        uint? choiceId = ReservePlayerChoiceId(player);
        await SignalPlayerChoiceBegun(context, "CancelPlayCardActions").ConfigureAwait(false);

        TCard? result;
        try
        {
            Logger.Info($"손패 강화 선택 대기를 생성합니다. options={candidates.Count}, card_type={typeof(TCard).FullName}");
            IReadOnlyList<TCard> selected = await AdapterCardSelectionBridge
                .RequestSelectionAsync(candidates, 1, 1)
                .ConfigureAwait(false);
            result = selected.FirstOrDefault();

            if (choiceId is not null && result is not null)
            {
                SyncLocalChoiceMutableCombatCard(player, choiceId.Value, result);
            }
        }
        finally
        {
            await SignalPlayerChoiceEnded(context).ConfigureAwait(false);
        }

        Logger.Info($"손패 강화 선택이 확정됐습니다. selected={ReadModelId(result)}");
        return result;
    }

    private static async Task<IEnumerable<TCard>> SelectFromHandWithPlayerChoiceTyped<TCard>(
        object context,
        List<object> candidateObjects,
        object player,
        int minSelect,
        int maxSelect)
        where TCard : class
    {
        List<TCard> candidates = candidateObjects.Cast<TCard>().ToList();
        uint? choiceId = ReservePlayerChoiceId(player);
        await SignalPlayerChoiceBegun(context, "CancelPlayCardActions").ConfigureAwait(false);

        List<TCard> selected;
        try
        {
            Logger.Info($"FromHand 손패 선택 대기를 생성합니다. options={candidates.Count}, min={minSelect}, max={maxSelect}, card_type={typeof(TCard).FullName}");
            selected = (await AdapterCardSelectionBridge
                    .RequestSelectionAsync(candidates, minSelect, maxSelect)
                    .ConfigureAwait(false))
                .ToList();

            if (choiceId is not null)
            {
                SyncLocalChoiceMutableCombatCards(player, choiceId.Value, selected);
            }
        }
        finally
        {
            await SignalPlayerChoiceEnded(context).ConfigureAwait(false);
        }

        Logger.Info($"FromHand 손패 선택이 확정됐습니다. selected={string.Join(",", selected.Select(ReadModelId))}");
        return selected;
    }

    private static async Task<IEnumerable<TCard>> SelectFromSimpleGridWithPlayerChoiceTyped<TCard>(
        object context,
        List<object> candidateObjects,
        object player,
        int minSelect,
        int maxSelect)
        where TCard : class
    {
        List<TCard> candidates = candidateObjects.Cast<TCard>().ToList();
        uint? choiceId = ReservePlayerChoiceId(player);
        await SignalPlayerChoiceBegun(context, "None").ConfigureAwait(false);

        List<TCard> selected;
        try
        {
            Logger.Info($"Headbutt 버림 더미 선택 대기를 생성합니다. options={candidates.Count}, card_type={typeof(TCard).FullName}");
            selected = (await AdapterCardSelectionBridge
                    .RequestSelectionAsync(candidates, minSelect, maxSelect)
                    .ConfigureAwait(false))
                .ToList();

            if (choiceId is not null)
            {
                SyncLocalChoiceIndexes(player, choiceId.Value, selected.Select(card => candidates.IndexOf(card)).ToList());
            }
        }
        finally
        {
            await SignalPlayerChoiceEnded(context).ConfigureAwait(false);
        }

        Logger.Info($"Headbutt 버림 더미 선택이 확정됐습니다. selected={string.Join(",", selected.Select(ReadModelId))}");
        return selected;
    }

    private static async Task<TCard?> SelectFromChooseACardScreenTyped<TCard>(
        object context,
        List<object> candidateObjects,
        object player,
        bool canSkip)
        where TCard : class
    {
        List<TCard> candidates = candidateObjects.Cast<TCard>().ToList();
        uint? choiceId = ReservePlayerChoiceId(player);
        await SignalPlayerChoiceBegun(context, "None").ConfigureAwait(false);

        TCard? result;
        try
        {
            int minSelect = canSkip ? 0 : 1;
            Logger.Info($"FromChooseACardScreen 카드 선택 대기를 생성합니다. options={candidates.Count}, can_skip={canSkip}, card_type={typeof(TCard).FullName}");
            IReadOnlyList<TCard> selected = await AdapterCardSelectionBridge
                .RequestSelectionAsync(candidates, minSelect, 1)
                .ConfigureAwait(false);
            result = selected.FirstOrDefault();

            if (choiceId is not null)
            {
                int selectedIndex = result is null ? -1 : candidates.IndexOf(result);
                SyncLocalChoiceIndex(player, choiceId.Value, selectedIndex);
            }
        }
        finally
        {
            await SignalPlayerChoiceEnded(context).ConfigureAwait(false);
        }

        Logger.Info($"FromChooseACardScreen 카드 선택이 확정됐습니다. selected={ReadModelId(result)}");
        return result;
    }

    private static IEnumerable<object> GetHandCards(object player)
    {
        Type? pileTypeType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Cards.PileType");
        Type? cardPileType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Entities.Cards.CardPile");
        if (pileTypeType is null || cardPileType is null)
        {
            yield break;
        }

        object handPileType = Enum.Parse(pileTypeType, "Hand");
        MethodInfo? getMethod = cardPileType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == "Get"
                && method.GetParameters().Length == 2
                && method.GetParameters()[0].ParameterType == pileTypeType);
        object? pile = getMethod?.Invoke(null, new[] { handPileType, player });
        object? cards = ReadNamedMember(pile, "Cards");
        if (cards is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (object? card in enumerable)
        {
            if (card is not null)
            {
                yield return card;
            }
        }
    }

    private static IEnumerable<object> EnumerateObjects(object? source)
    {
        if (source is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (object? item in enumerable)
        {
            if (item is not null)
            {
                yield return item;
            }
        }
    }

    private static bool MatchesCardFilter(object card, object? filter)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter is not Delegate filterDelegate)
        {
            return true;
        }

        try
        {
            return filterDelegate.DynamicInvoke(card) is bool matches && matches;
        }
        catch (Exception exception)
        {
            Logger.Warning($"FromHand filter 실행 중 예외가 발생했습니다. card={ReadModelId(card)}, {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static bool IsHeadbuttSimpleGridCall()
    {
        StackTrace stackTrace = new();
        foreach (StackFrame frame in stackTrace.GetFrames() ?? Array.Empty<StackFrame>())
        {
            MethodBase? method = frame.GetMethod();
            string? declaringTypeName = method?.DeclaringType?.FullName;
            if (string.Equals(declaringTypeName, "MegaCrit.Sts2.Core.Models.Cards.Headbutt", StringComparison.Ordinal)
                || declaringTypeName?.StartsWith("MegaCrit.Sts2.Core.Models.Cards.Headbutt+", StringComparison.Ordinal) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static uint? ReservePlayerChoiceId(object player)
    {
        object? synchronizer = GetPlayerChoiceSynchronizer();
        MethodInfo? method = synchronizer?.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.Name == "ReserveChoiceId"
                && candidate.GetParameters().Length == 1);
        object? result = method?.Invoke(synchronizer, new[] { player });
        return result is uint value ? value : null;
    }

    private static void SyncLocalChoiceIndexes(object player, uint choiceId, IReadOnlyList<int> indexes)
    {
        object? choiceResult = CreatePlayerChoiceResultFromIndexes(indexes);
        if (choiceResult is null)
        {
            return;
        }

        SyncLocalChoice(player, choiceId, choiceResult);
    }

    private static void SyncLocalChoiceIndex(object player, uint choiceId, int index)
    {
        object? choiceResult = CreatePlayerChoiceResultFromIndex(index);
        if (choiceResult is null)
        {
            return;
        }

        SyncLocalChoice(player, choiceId, choiceResult);
    }

    private static void SyncLocalChoiceMutableCombatCard(object player, uint choiceId, object card)
    {
        object? choiceResult = CreatePlayerChoiceResultFromMutableCombatCard(card);
        if (choiceResult is null)
        {
            return;
        }

        SyncLocalChoice(player, choiceId, choiceResult);
    }

    private static void SyncLocalChoiceMutableCombatCards(object player, uint choiceId, object cards)
    {
        object? choiceResult = CreatePlayerChoiceResultFromMutableCombatCards(cards);
        if (choiceResult is null)
        {
            return;
        }

        SyncLocalChoice(player, choiceId, choiceResult);
    }

    private static void SyncLocalChoice(object player, uint choiceId, object choiceResult)
    {
        object? synchronizer = GetPlayerChoiceSynchronizer();
        if (synchronizer is null)
        {
            return;
        }

        MethodInfo? method = synchronizer.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.Name == "SyncLocalChoice"
                && candidate.GetParameters().Length == 3);
        method?.Invoke(synchronizer, new[] { player, choiceId, choiceResult });
    }

    private static object? CreatePlayerChoiceResultFromIndexes(IReadOnlyList<int> indexes)
    {
        Type? resultType = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceResult");
        MethodInfo? method = resultType
            ?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.Name == "FromIndexes"
                && candidate.GetParameters().Length == 1);
        return method?.Invoke(null, new object[] { indexes.ToList() });
    }

    private static object? CreatePlayerChoiceResultFromIndex(int index)
    {
        Type? resultType = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceResult");
        MethodInfo? method = resultType
            ?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.Name == "FromIndex"
                && candidate.GetParameters().Length == 1);
        return method?.Invoke(null, new object[] { index });
    }

    private static object? CreatePlayerChoiceResultFromMutableCombatCard(object card)
    {
        Type? resultType = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceResult");
        MethodInfo? method = resultType
            ?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.Name == "FromMutableCombatCard"
                && candidate.GetParameters().Length == 1);
        return method?.Invoke(null, new[] { card });
    }

    private static object? CreatePlayerChoiceResultFromMutableCombatCards(object cards)
    {
        Type? resultType = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceResult");
        MethodInfo? method = resultType
            ?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.Name == "FromMutableCombatCards"
                && candidate.GetParameters().Length == 1);
        return method?.Invoke(null, new[] { cards });
    }

    private static object? GetPlayerChoiceSynchronizer()
    {
        Type? runManagerType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager");
        object? runManager = runManagerType
            ?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
        return ReadNamedMember(runManager, "PlayerChoiceSynchronizer");
    }

    private static async Task SignalPlayerChoiceBegun(object context, string optionName)
    {
        Type? optionsType = AccessTools.TypeByName("MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceOptions");
        object? option = optionsType is null ? null : Enum.Parse(optionsType, optionName);
        MethodInfo? method = context.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.Name == "SignalPlayerChoiceBegun"
                && candidate.GetParameters().Length == 1);
        object? task = method?.Invoke(context, new[] { option });
        if (task is Task awaited)
        {
            await awaited.ConfigureAwait(false);
        }
    }

    private static async Task SignalPlayerChoiceEnded(object context)
    {
        MethodInfo? method = context.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate => candidate.Name == "SignalPlayerChoiceEnded"
                && candidate.GetParameters().Length == 0);
        object? task = method?.Invoke(context, Array.Empty<object>());
        if (task is Task awaited)
        {
            await awaited.ConfigureAwait(false);
        }
    }

    private static bool? ReadBool(object? source, string memberName)
    {
        object? value = ReadNamedMember(source, memberName);
        return value is bool boolean ? boolean : null;
    }

    private static int? ReadInt(object? source, string memberName)
    {
        object? value = ReadNamedMember(source, memberName);
        return value is int integer ? integer : null;
    }

    private static string ReadModelId(object? model)
    {
        object? id = ReadNamedMember(model, "Id");
        object? entry = ReadNamedMember(id, "Entry");
        return entry?.ToString() ?? id?.ToString() ?? model?.GetType().Name ?? "<none>";
    }

    private static object? ReadNamedMember(object? source, string memberName)
    {
        if (source is null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        PropertyInfo? property = source.GetType().GetProperty(memberName, flags);
        if (property is not null && property.GetIndexParameters().Length == 0)
        {
            try
            {
                return property.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        FieldInfo? field = source.GetType().GetField(memberName, flags);
        if (field is not null)
        {
            try
            {
                return field.GetValue(source);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
