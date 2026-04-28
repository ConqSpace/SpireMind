using System.Runtime.CompilerServices;
using Godot;
using GodotTimer = Godot.Timer;

namespace SpireMindMod;

/// <summary>
/// NGame 노드에 자동 테스트 명령 처리용 Godot Timer를 한 번만 연결합니다.
/// </summary>
internal static class AutotestMainThreadTickerInstaller
{
    private const string TickerNodeName = AutotestMainThreadTicker.NodeName;

    private static readonly SpireMindLogger Logger = new("SpireMind.Autotest.MainThread");
    private static readonly ConditionalWeakTable<object, TickerAttachmentMarker> AttachedGames = new();

    public static void EnsureInstalled(object? instance, string source)
    {
        if (instance is not Node gameNode)
        {
            Logger.Warning($"autotest ticker install failed: 패치 인스턴스가 Godot.Node가 아닙니다. source={source}");
            return;
        }

        if (AttachedGames.TryGetValue(gameNode, out _))
        {
            return;
        }

        try
        {
            GodotTimer ticker = GetOrCreateTicker(gameNode, source);
            ConfigureTicker(ticker);
            ticker.Timeout += AutotestMainThreadTicker.OnTimerTimeout;
            ticker.Start();

            AttachedGames.Add(gameNode, new TickerAttachmentMarker());
            Logger.Info(
                $"autotest timer ticker installed: source={source}, parent={gameNode.GetType().FullName}, name={TickerNodeName}, wait_time={AutotestMainThreadTicker.TimerWaitTimeSeconds:0.00}");
        }
        catch (Exception exception)
        {
            Logger.Warning($"autotest ticker install failed: source={source}, {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static GodotTimer GetOrCreateTicker(Node gameNode, string source)
    {
        Node? existingNode = gameNode.GetNodeOrNull<Node>(TickerNodeName);
        if (existingNode is GodotTimer existingTimer)
        {
            Logger.Info($"autotest timer ticker installed: 기존 Timer 노드를 재사용합니다. source={source}, name={TickerNodeName}");
            return existingTimer;
        }

        if (existingNode is not null)
        {
            // 이전 구현의 C# Node subclass가 남아 있으면 Timer로 교체해 콜백 등록 문제를 피합니다.
            gameNode.RemoveChild(existingNode);
            existingNode.QueueFree();
            Logger.Info($"autotest timer ticker installed: 기존 비 Timer 노드를 교체합니다. source={source}, old_type={existingNode.GetType().FullName}");
        }

        GodotTimer ticker = new()
        {
            Name = TickerNodeName
        };
        gameNode.AddChild(ticker);
        return ticker;
    }

    private static void ConfigureTicker(GodotTimer ticker)
    {
        ticker.ProcessMode = Node.ProcessModeEnum.Always;
        ticker.WaitTime = AutotestMainThreadTicker.TimerWaitTimeSeconds;
        ticker.OneShot = false;
        ticker.Autostart = false;
    }

    private sealed class TickerAttachmentMarker
    {
    }
}
