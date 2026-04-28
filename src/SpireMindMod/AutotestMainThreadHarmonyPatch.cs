using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace SpireMindMod;

/// <summary>
/// 자동 테스트 ticker 설치는 CombatStateHarmonyPatch 경로에서만 수행한다.
/// </summary>
[HarmonyPatch]
public static class AutotestMainThreadHarmonyPatch
{
    private static readonly SpireMindLogger Logger = new("SpireMind.Autotest.MainThread");

    static AutotestMainThreadHarmonyPatch()
    {
        Logger.Info("autotest ticker patch type initialized");
    }

    /// <summary>
    /// 어셈블리 로드 확인용 로그만 남긴다.
    /// </summary>
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void LogModuleLoaded()
    {
        Logger.Info("autotest ticker patch module loaded");
    }

    /// <summary>
    /// 중복 패치를 막기 위해 독립 ticker 패치 클래스는 비활성화한다.
    /// </summary>
    public static bool Prepare()
    {
        Logger.Info("autotest ticker standalone patch disabled");
        return false;
    }

    /// <summary>
    /// 독립 패치 경로를 사용하지 않으므로 대상 메서드를 반환하지 않는다.
    /// </summary>
    public static IEnumerable<MethodBase> TargetMethods()
    {
        return Enumerable.Empty<MethodBase>();
    }
}
