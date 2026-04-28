using System.Threading;

namespace SpireMindMod;

/// <summary>
/// Godot Timer 신호에서 호출되는 자동 테스트 명령 처리 helper입니다.
/// </summary>
internal static class AutotestMainThreadTicker
{
    public const string NodeName = "SpireMindAutotestMainThreadTicker";
    public const double TimerWaitTimeSeconds = 0.5;

    private const long DiagnosticLogIntervalMs = 5000;

    private static readonly SpireMindLogger Logger = new("SpireMind.Autotest.Ticker");
    private static int tickInProgress;
    private static long tickCount;
    private static long skippedReentrantTickCount;
    private static long lastDiagnosticLogAtMs;

    public static void OnTimerTimeout()
    {
        if (Interlocked.CompareExchange(ref tickInProgress, 1, 0) != 0)
        {
            Interlocked.Increment(ref skippedReentrantTickCount);
            LogDiagnosticsIfNeeded("AutotestMainThreadTicker.Timer.Timeout");
            return;
        }

        try
        {
            Interlocked.Increment(ref tickCount);
            CombatStateExporter.FlushPendingExportIfReady();
            CombatActionExecutor.TickMainThread();
            LogDiagnosticsIfNeeded("AutotestMainThreadTicker.Timer.Timeout");
        }
        catch (Exception exception)
        {
            Logger.Warning($"자동 테스트 주 스레드 처리 중 예외가 발생했습니다. 게임 진행은 멈추지 않습니다. {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref tickInProgress, 0);
        }
    }

    private static void LogDiagnosticsIfNeeded(string source)
    {
        long nowMs = Environment.TickCount64;
        long previousMs = Interlocked.Read(ref lastDiagnosticLogAtMs);
        if (nowMs - previousMs < DiagnosticLogIntervalMs)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref lastDiagnosticLogAtMs, nowMs, previousMs) != previousMs)
        {
            return;
        }

        Logger.Info(
            $"autotest timer ticker Tick: source={source}, total={Interlocked.Read(ref tickCount)}, skipped_reentrant={Interlocked.Read(ref skippedReentrantTickCount)}");
    }
}
