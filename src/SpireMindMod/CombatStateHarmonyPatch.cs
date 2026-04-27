using System.Reflection;
using HarmonyLib;

namespace SpireMindMod;

/// <summary>
/// STS2 타입을 직접 참조하지 않고, 문자열 타입 이름으로 읽기 전용 관찰 지점을 찾는다.
/// </summary>
[HarmonyPatch]
internal static class CombatStateHarmonyPatch
{
    private static readonly SpireMindLogger Logger = new("SpireMind.R2.Patch");
    private static bool hasLoggedTargets;

    public static IEnumerable<MethodBase> TargetMethods()
    {
        CombatActionExecutor.StartBackgroundPolling();

        List<MethodBase> targets = new();
        targets.AddRange(GetDirectSts2TargetMethods());

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types = GetTypesSafely(assembly);
            foreach (Type type in types)
            {
                if (!IsCombatRoomType(type))
                {
                    continue;
                }

                targets.AddRange(GetPatchableConstructors(type));
                targets.AddRange(GetPatchableUpdateMethods(type));
            }
        }

        targets = targets.Distinct().ToList();

        if (!hasLoggedTargets)
        {
            hasLoggedTargets = true;
            Logger.Info($"Harmony 전투 상태 관찰 지점 {targets.Count}개를 찾았습니다.");
            if (targets.Count == 0)
            {
                Logger.Warning("전투 상태 관찰 지점을 찾지 못했습니다. STS2 타입 이름과 메서드 이름 확인이 필요합니다.");
            }
        }

        return targets;
    }

    public static void Postfix(object __instance)
    {
        CombatStateExporter.Observe(__instance);
    }

    private static IEnumerable<MethodBase> GetDirectSts2TargetMethods()
    {
        List<MethodBase> targets = new();

        AddMethod(targets, "MegaCrit.Sts2.Core.Entities.Players.Player", "PopulateCombatState");
        AddMethod(targets, "MegaCrit.Sts2.Core.Combat.CombatState", "AddCreature");
        AddMethod(targets, "MegaCrit.Sts2.Core.Combat.CombatState", "RemoveCreature");
        AddPropertySetter(targets, "MegaCrit.Sts2.Core.Combat.CombatState", "CurrentSide");
        AddPropertySetter(targets, "MegaCrit.Sts2.Core.Combat.CombatState", "RoundNumber");
        AddPropertySetter(targets, "MegaCrit.Sts2.Core.Combat.PlayerCombatState", "Energy");
        AddEnergySetterCandidates(targets, "MegaCrit.Sts2.Core.Combat.PlayerCombatState");
        AddMethod(targets, "MegaCrit.Sts2.Core.Entities.Cards.CardPile", "InvokeContentsChanged");

        return targets;
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

    private static void AddPropertySetter(List<MethodBase> targets, string typeName, string propertyName)
    {
        Type? type = AccessTools.TypeByName(typeName);
        if (type is null)
        {
            return;
        }

        MethodInfo? setter = AccessTools.PropertySetter(type, propertyName)
            ?? AccessTools.Method(type, $"set_{propertyName}");
        if (setter is not null && !setter.IsAbstract && !setter.ContainsGenericParameters)
        {
            targets.Add(setter);
        }
    }

    private static void AddEnergySetterCandidates(List<MethodBase> targets, string typeName)
    {
        Type? type = AccessTools.TypeByName(typeName);
        if (type is null)
        {
            return;
        }

        foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.IsAbstract || method.ContainsGenericParameters)
            {
                continue;
            }

            if (method.Name.StartsWith("set_", StringComparison.OrdinalIgnoreCase)
                && method.Name.Contains("Energy", StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(method);
            }
        }
    }

    private static Type[] GetTypesSafely(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static bool IsCombatRoomType(Type type)
    {
        string fullName = type.FullName ?? type.Name;
        if (type.Name.Equals("NCombatRoom", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullName.Contains("CombatRoom", StringComparison.OrdinalIgnoreCase)
            && fullName.Contains("MegaCrit", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<MethodBase> GetPatchableConstructors(Type type)
    {
        return type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(constructor => !constructor.IsStatic);
    }

    private static IEnumerable<MethodBase> GetPatchableUpdateMethods(Type type)
    {
        string[] updateNames =
        {
            "Update",
            "Tick",
            "Process",
            "_Process",
            "OnUpdate",
            "OnTick"
        };

        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => updateNames.Contains(method.Name, StringComparer.OrdinalIgnoreCase))
            .Where(method => !method.IsAbstract && !method.ContainsGenericParameters)
            .Where(method => method.GetParameters().Length <= 1);
    }
}
