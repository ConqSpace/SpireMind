using System.Reflection;

namespace SpireMindMod;

/// <summary>
/// STS2 모드 로더가 호출할 수 있도록 의도적으로 얇게 유지한 진입점입니다.
/// 실제 게임 상태 접근은 R2에서 로더 구조와 STS2 API를 확인한 뒤 별도 모듈로 분리합니다.
/// </summary>
public static class SpireMindEntrypoint
{
    private static readonly SpireMindLogger Logger = new("SpireMind");
    private static bool isLoaded;

    /// <summary>
    /// 현재 로드된 SpireMind 어셈블리 버전입니다.
    /// </summary>
    public static string Version
    {
        get
        {
            Assembly assembly = typeof(SpireMindEntrypoint).Assembly;
            return assembly.GetName().Version?.ToString() ?? "0.1.0";
        }
    }

    /// <summary>
    /// 모드 로더가 SpireMind를 활성화할 때 호출하는 진입점입니다.
    /// </summary>
    public static void Load()
    {
        if (isLoaded)
        {
            Logger.Info("모드 로드 요청을 다시 받았습니다. 이미 초기화된 상태라서 추가 작업을 건너뜁니다.");
            return;
        }

        isLoaded = true;
        Logger.Info($"SpireMind {Version} 모드 골격이 로드되었습니다. 현재 단계에서는 게임 상태를 변경하지 않습니다.");
    }

    /// <summary>
    /// 모드 로더가 SpireMind를 비활성화할 때 호출하는 정리 지점입니다.
    /// </summary>
    public static void Unload()
    {
        if (!isLoaded)
        {
            Logger.Info("모드 해제 요청을 받았지만 초기화된 상태가 아닙니다.");
            return;
        }

        isLoaded = false;
        Logger.Info("SpireMind 모드 골격이 해제되었습니다.");
    }
}
