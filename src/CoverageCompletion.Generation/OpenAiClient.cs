using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CoverageCompletion.Generation;

/// <summary>
/// Thin wrapper over the OpenAI Chat Completions API. HttpClient is injected so tests
/// can substitute the HttpMessageHandler instead of hitting the network.
/// </summary>
public class OpenAiClient
{
    private const string DefaultModel = "gpt-4.1";
    private const string Endpoint = "https://api.openai.com/v1/chat/completions";

    // Simple exponential backoff for transient failures (429 / 5xx / network timeouts).
    // 3 retries after the initial attempt = 4 attempts total.
    private static readonly TimeSpan[] DefaultRetryDelays =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
    };

    private readonly HttpClient _httpClient;
    private readonly TimeSpan[] _retryDelays;

    // A single constructor with an optional param, not an overload: AddHttpClient<T>()'s typed-client
    // factory throws "Multiple constructors accepting all given argument types" when a type registered
    // this way has more than one public constructor starting with HttpClient - even though only one of
    // them would ever actually be satisfiable via DI. retryDelays lets tests use short delays instead of
    // waiting on real backoff timers.
    public OpenAiClient(HttpClient httpClient, TimeSpan[]? retryDelays = null)
    {
        _httpClient = httpClient;
        _retryDelays = retryDelays ?? DefaultRetryDelays;
    }

    public async Task<string> CompleteAsync(string prompt, CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "OPENAI_API_KEY environment variable is not set. Set it to a valid OpenAI API key before generating tests.");
        }

        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL");
        if (string.IsNullOrWhiteSpace(model))
        {
            model = DefaultModel;
        }

        var requestBody = new
        {
            model,
            messages = new[] { new { role = "user", content = prompt } },
        };
        var requestJson = JsonSerializer.Serialize(requestBody);

        var maxAttempts = _retryDelays.Length + 1;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(_retryDelays[attempt - 2], ct);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                // Network-level failure (connection reset, DNS blip, request timeout) - treat as transient.
                lastError = ex;
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync(ct);
                    using var document = JsonDocument.Parse(responseJson);
                    var content = document.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("message")
                        .GetProperty("content")
                        .GetString() ?? string.Empty;

                    return ExtractCodeBlock(content);
                }

                if (!IsTransientStatusCode(response.StatusCode))
                {
                    // Non-transient (401/403/400/etc.) - config/key problem, retrying won't help.
                    response.EnsureSuccessStatusCode();
                }

                lastError = new HttpRequestException(
                    $"OpenAI API returned transient status {(int)response.StatusCode} ({response.StatusCode}).");
            }
        }

        throw new HttpRequestException(
            $"OpenAI API call failed after {maxAttempts} attempts due to transient errors. Last error: {lastError?.Message}",
            lastError);
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static string ExtractCodeBlock(string content)
    {
        const string marker = "```csharp";
        var start = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return content.Trim();
        }

        start += marker.Length;
        var end = content.IndexOf("```", start, StringComparison.Ordinal);
        return (end < 0 ? content[start..] : content[start..end]).Trim();
    }
}
