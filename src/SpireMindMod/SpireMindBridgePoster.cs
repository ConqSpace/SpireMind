using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace SpireMindMod;

/// <summary>
/// 전투 상태를 로컬 브리지로 보낼지 여부와 전송 대상을 런타임에서 조절합니다.
/// </summary>
public static class SpireMindBridgeSettings
{
    private const string DefaultBridgeStateUrl = "http://127.0.0.1:17832/state";
    private const int DefaultRequestTimeoutMs = 800;
    private const int DefaultRetryCooldownMs = 1000;
    private const int ConfigRefreshMs = 2000;

    private static readonly object ConfigLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static int bridgePostingEnabled = 1;
    private static long lastConfigRefreshAtMs = -ConfigRefreshMs;
    private static bool fileConfigEnabled = true;
    private static bool hasWrittenDefaultConfig;
    private static string bridgeStateUrl = DefaultBridgeStateUrl;
    private static int requestTimeoutMs = DefaultRequestTimeoutMs;
    private static int retryCooldownMs = DefaultRetryCooldownMs;

    /// <summary>
    /// false로 두면 전투 상태를 브리지로 보내지 않습니다.
    /// </summary>
    public static bool BridgePostingEnabled
    {
        get => Volatile.Read(ref bridgePostingEnabled) == 1;
        set
        {
            Volatile.Write(ref bridgePostingEnabled, value ? 1 : 0);
            CombatStateBridgePoster.ClearHistory();
        }
    }

    internal static BridgeSettingsSnapshot GetSnapshot(long nowMs)
    {
        RefreshConfigIfNeeded(nowMs);

        lock (ConfigLock)
        {
            return new BridgeSettingsSnapshot(
                Enabled: BridgePostingEnabled && fileConfigEnabled,
                StateUrl: bridgeStateUrl,
                RequestTimeoutMs: requestTimeoutMs,
                RetryCooldownMs: retryCooldownMs);
        }
    }

    private static void RefreshConfigIfNeeded(long nowMs)
    {
        if (nowMs - lastConfigRefreshAtMs < ConfigRefreshMs)
        {
            return;
        }

        lastConfigRefreshAtMs = nowMs;
        BridgeConfig config = LoadConfig().Normalize();

        string? enabledText = Environment.GetEnvironmentVariable("SPIREMIND_BRIDGE_ENABLED");
        if (!string.IsNullOrWhiteSpace(enabledText) && bool.TryParse(enabledText, out bool envEnabled))
        {
            config = config with { Enabled = envEnabled };
        }

        string? stateUrl = Environment.GetEnvironmentVariable("SPIREMIND_BRIDGE_STATE_URL");
        if (!string.IsNullOrWhiteSpace(stateUrl))
        {
            config = config with { StateUrl = stateUrl };
        }

        lock (ConfigLock)
        {
            fileConfigEnabled = config.Enabled;
            bridgeStateUrl = config.StateUrl;
            requestTimeoutMs = config.RequestTimeoutMs;
            retryCooldownMs = config.RetryCooldownMs;
        }
    }

    private static BridgeConfig LoadConfig()
    {
        string configPath = GetConfigPath();
        TryWriteDefaultConfig(configPath);

        try
        {
            if (!File.Exists(configPath))
            {
                return BridgeConfig.Default;
            }

            string json = File.ReadAllText(configPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<BridgeConfig>(json, JsonOptions) ?? BridgeConfig.Default;
        }
        catch
        {
            return BridgeConfig.Default;
        }
    }

    private static void TryWriteDefaultConfig(string configPath)
    {
        if (hasWrittenDefaultConfig || File.Exists(configPath))
        {
            return;
        }

        hasWrittenDefaultConfig = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            string json = JsonSerializer.Serialize(BridgeConfig.Default, JsonOptions);
            File.WriteAllText(configPath, json, Encoding.UTF8);
        }
        catch
        {
            // 설정 파일 생성 실패는 게임 진행과 무관하다. 기본값으로 계속 동작한다.
        }
    }

    private static string GetConfigPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "SlayTheSpire2", "SpireMind", "bridge_config.json");
    }

    internal sealed record BridgeSettingsSnapshot(
        bool Enabled,
        string StateUrl,
        int RequestTimeoutMs,
        int RetryCooldownMs);

    private sealed record BridgeConfig
    {
        public static BridgeConfig Default { get; } = new()
        {
            Enabled = true,
            StateUrl = DefaultBridgeStateUrl,
            RequestTimeoutMs = DefaultRequestTimeoutMs,
            RetryCooldownMs = DefaultRetryCooldownMs
        };

        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; } = true;

        [JsonPropertyName("state_url")]
        public string StateUrl { get; init; } = DefaultBridgeStateUrl;

        [JsonPropertyName("request_timeout_ms")]
        public int RequestTimeoutMs { get; init; } = DefaultRequestTimeoutMs;

        [JsonPropertyName("retry_cooldown_ms")]
        public int RetryCooldownMs { get; init; } = DefaultRetryCooldownMs;

        public BridgeConfig Normalize()
        {
            return this with
            {
                StateUrl = string.IsNullOrWhiteSpace(StateUrl) ? DefaultBridgeStateUrl : StateUrl,
                RequestTimeoutMs = Math.Clamp(RequestTimeoutMs, 100, 5000),
                RetryCooldownMs = Math.Clamp(RetryCooldownMs, 250, 10000)
            };
        }
    }
}

internal static class CombatStateBridgePoster
{
    private const int FailureLogCooldownMs = 30000;

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly SpireMindLogger Logger = new("SpireMind.R4.Bridge");
    private static readonly object FailureLogLock = new();
    private static long lastAttemptAtMs;
    private static long lastFailureLogAtMs;
    private static int suppressedFailureLogs;
    private static string lastSuccessfulJson = string.Empty;
    private static string lastAttemptedJson = string.Empty;

    internal static void ClearHistory()
    {
        lastAttemptAtMs = 0;
        lastFailureLogAtMs = 0;
        suppressedFailureLogs = 0;
        lastSuccessfulJson = string.Empty;
        lastAttemptedJson = string.Empty;
    }

    public static void TryPost(string json)
    {
        long nowMs = Environment.TickCount64;
        SpireMindBridgeSettings.BridgeSettingsSnapshot settings = SpireMindBridgeSettings.GetSnapshot(nowMs);

        if (!settings.Enabled || string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        if (json == lastSuccessfulJson)
        {
            return;
        }

        if (json == lastAttemptedJson && nowMs - lastAttemptAtMs < settings.RetryCooldownMs)
        {
            return;
        }

        if (nowMs - lastAttemptAtMs < settings.RetryCooldownMs)
        {
            return;
        }

        lastAttemptAtMs = nowMs;
        lastAttemptedJson = json;
        _ = PostStateAsync(json, settings);
    }

    internal static async Task<bool> PostStateAsync(
        string json,
        SpireMindBridgeSettings.BridgeSettingsSnapshot? settings = null,
        HttpMessageHandler? handler = null)
    {
        SpireMindBridgeSettings.BridgeSettingsSnapshot effectiveSettings =
            settings ?? SpireMindBridgeSettings.GetSnapshot(Environment.TickCount64);

        if (!effectiveSettings.Enabled || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using CancellationTokenSource timeoutSource = new(effectiveSettings.RequestTimeoutMs);
            using StringContent content = new(json, Encoding.UTF8, "application/json");

            if (handler is null)
            {
                using HttpRequestMessage request = new(HttpMethod.Post, effectiveSettings.StateUrl)
                {
                    Content = content
                };

                using HttpResponseMessage response = await HttpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            else
            {
                using HttpClient localClient = new(handler, disposeHandler: true)
                {
                    Timeout = Timeout.InfiniteTimeSpan
                };

                using HttpRequestMessage request = new(HttpMethod.Post, effectiveSettings.StateUrl)
                {
                    Content = content
                };

                using HttpResponseMessage response = await localClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }

            lastSuccessfulJson = json;
            return true;
        }
        catch (OperationCanceledException exception)
        {
            LogFailure(exception);
            return false;
        }
        catch (HttpRequestException exception)
        {
            LogFailure(exception);
            return false;
        }
        catch (Exception exception)
        {
            LogFailure(exception);
            return false;
        }
    }

    private static void LogFailure(Exception exception)
    {
        long nowMs = Environment.TickCount64;
        lock (FailureLogLock)
        {
            if (nowMs - lastFailureLogAtMs < FailureLogCooldownMs)
            {
                suppressedFailureLogs++;
                return;
            }

            int suppressedCount = suppressedFailureLogs;
            suppressedFailureLogs = 0;
            lastFailureLogAtMs = nowMs;

            string suppressedText = suppressedCount > 0
                ? $" (이전 실패 {suppressedCount}건은 생략됨)"
                : string.Empty;

            Logger.Warning(
                $"로컬 브리지로 combat_state.json 전송에 실패했습니다{suppressedText}. {exception.GetType().Name}: {exception.Message}");
        }
    }
}
