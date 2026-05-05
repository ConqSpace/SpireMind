using System.Reflection;
using HarmonyLib;

namespace SpireMindMod;

internal static class CardMovementHarmonyPatch
{
    private static readonly SpireMindLogger Logger = new("SpireMind.R2.CardMovePatch");
    private static bool hasLoggedTargets;

    // PatchAll이 대상 없는 Prefix/Postfix를 자동 발견하면 모드 로드가 실패합니다.
    // 이 연구용 패치는 기본 배포에서 HarmonyPatch 특성을 붙이지 않고 수동 연결할 때만 사용합니다.
    public static IEnumerable<MethodBase> GetTargetMethodsForManualPatch()
    {
        List<MethodBase> targets = new();
        if (!CardMovementObserver.Enabled)
        {
            if (!hasLoggedTargets)
            {
                hasLoggedTargets = true;
                Logger.Info("카드 이동 관찰 Harmony 패치는 기본 비활성화되어 있습니다.");
            }

            return targets;
        }

        AddMethod(targets, "MegaCrit.Sts2.Core.Models.CardModel", "RemoveFromCurrentPile");
        AddMethod(targets, "MegaCrit.Sts2.Core.Entities.Cards.CardPile", "AddInternal");

        if (!hasLoggedTargets)
        {
            hasLoggedTargets = true;
            Logger.Info($"카드 이동 관찰 Harmony 지점 {targets.Count}개를 찾았습니다.");
            if (targets.Count == 0)
            {
                Logger.Warning("카드 이동 관찰 지점을 찾지 못했습니다. 손패 누락 보정은 비활성 상태로 유지됩니다.");
            }
        }

        return targets;
    }

    public static void ObserveBeforeCardMovement(MethodBase __originalMethod, object __instance)
    {
        try
        {
            if (__originalMethod.Name.Equals("RemoveFromCurrentPile", StringComparison.OrdinalIgnoreCase))
            {
                CardMovementObserver.ObserveRemoveFromCurrentPile(__instance);
            }
        }
        catch (Exception exception)
        {
            Logger.Warning($"RemoveFromCurrentPile 관찰 실패. 게임 진행은 멈추지 않습니다. {exception.GetType().Name}: {exception.Message}");
        }
    }

    public static void ObserveAfterCardMovement(MethodBase __originalMethod, object __instance, object[] __args)
    {
        try
        {
            if (!__originalMethod.Name.Equals("AddInternal", StringComparison.OrdinalIgnoreCase)
                || __args.Length == 0)
            {
                return;
            }

            CardMovementObserver.ObserveAddInternal(__instance, __args[0]);
        }
        catch (Exception exception)
        {
            Logger.Warning($"AddInternal 관찰 실패. 게임 진행은 멈추지 않습니다. {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void AddMethod(List<MethodBase> targets, string typeName, string methodName)
    {
        Type? type = AccessTools.TypeByName(typeName);
        if (type is null)
        {
            return;
        }

        MethodInfo? method = AccessTools.Method(type, methodName);
        if (method is not null && !method.IsAbstract && !method.ContainsGenericParameters)
        {
            targets.Add(method);
        }
    }
}
