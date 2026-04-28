using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpireMindMod;

internal static class CombatActionBridgeClient
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<ActionClaimResponse?> TryClaimAsync(
        CombatStateBridgePoster.PostedStateSnapshot postedState,
        CancellationToken cancellationToken)
    {
        SpireMindBridgeSettings.BridgeSettingsSnapshot settings =
            SpireMindBridgeSettings.GetSnapshot(Environment.TickCount64);
        if (!settings.Enabled)
        {
            return null;
        }

        Uri claimUri = BuildBridgeUri(settings.StateUrl, "/action/claim");
        ActionClaimRequest request = new()
        {
            ExecutorId = "sts2-mod-main",
            ObservedStateId = postedState.StateId,
            ObservedStateVersion = postedState.StateVersion,
            SupportedActionTypes = new[] { "end_turn", "play_card" }
        };

        return await PostJsonAsync<ActionClaimRequest, ActionClaimResponse>(
            claimUri,
            request,
            settings.RequestTimeoutMs,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ActionResultResponse?> ReportResultAsync(
        ClaimedAction action,
        string result,
        CombatStateBridgePoster.PostedStateSnapshot postedState,
        string note,
        CancellationToken cancellationToken)
    {
        SpireMindBridgeSettings.BridgeSettingsSnapshot settings =
            SpireMindBridgeSettings.GetSnapshot(Environment.TickCount64);
        if (!settings.Enabled)
        {
            return null;
        }

        Uri resultUri = BuildBridgeUri(settings.StateUrl, "/action/result");
        ActionResultRequest request = new()
        {
            SubmissionId = action.SubmissionId,
            ClaimToken = action.ClaimToken,
            ExecutorId = "sts2-mod-main",
            Result = result,
            ObservedStateId = postedState.StateId,
            ObservedStateVersion = postedState.StateVersion,
            Note = note
        };

        return await PostJsonAsync<ActionResultRequest, ActionResultResponse>(
            resultUri,
            request,
            settings.RequestTimeoutMs,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TResponse?> PostJsonAsync<TRequest, TResponse>(
        Uri uri,
        TRequest request,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeoutMs);

        string json = JsonSerializer.Serialize(request, JsonOptions);
        using StringContent content = new(json, Encoding.UTF8, "application/json");
        using HttpRequestMessage httpRequest = new(HttpMethod.Post, uri)
        {
            Content = content
        };

        using HttpResponseMessage response = await HttpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutSource.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TResponse>(body, JsonOptions);
    }

    private static Uri BuildBridgeUri(string stateUrl, string endpointPath)
    {
        Uri stateUri = new(stateUrl);
        UriBuilder builder = new(stateUri)
        {
            Path = endpointPath,
            Query = string.Empty
        };

        return builder.Uri;
    }
}

internal sealed record ActionClaimRequest
{
    [JsonPropertyName("executor_id")]
    public string ExecutorId { get; init; } = string.Empty;

    [JsonPropertyName("observed_state_id")]
    public string ObservedStateId { get; init; } = string.Empty;

    [JsonPropertyName("observed_state_version")]
    public int ObservedStateVersion { get; init; }

    [JsonPropertyName("supported_action_types")]
    public IReadOnlyList<string> SupportedActionTypes { get; init; } = Array.Empty<string>();
}

internal sealed record ActionClaimResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("claim_token")]
    public string? ClaimToken { get; init; }

    [JsonPropertyName("action")]
    public ClaimedAction? Action { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

internal sealed record ClaimedAction
{
    [JsonPropertyName("submission_id")]
    public string SubmissionId { get; init; } = string.Empty;

    [JsonPropertyName("state_id")]
    public string StateId { get; init; } = string.Empty;

    [JsonPropertyName("state_version")]
    public int StateVersion { get; init; }

    [JsonPropertyName("selected_action_id")]
    public string SelectedActionId { get; init; } = string.Empty;

    [JsonPropertyName("claim_token")]
    public string ClaimToken { get; init; } = string.Empty;

    [JsonPropertyName("action_type")]
    public string? ActionType { get; init; }
}

internal sealed record ActionResultRequest
{
    [JsonPropertyName("submission_id")]
    public string SubmissionId { get; init; } = string.Empty;

    [JsonPropertyName("claim_token")]
    public string ClaimToken { get; init; } = string.Empty;

    [JsonPropertyName("executor_id")]
    public string ExecutorId { get; init; } = string.Empty;

    [JsonPropertyName("result")]
    public string Result { get; init; } = string.Empty;

    [JsonPropertyName("observed_state_id")]
    public string ObservedStateId { get; init; } = string.Empty;

    [JsonPropertyName("observed_state_version")]
    public int ObservedStateVersion { get; init; }

    [JsonPropertyName("note")]
    public string Note { get; init; } = string.Empty;
}

internal sealed record ActionResultResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;
}
