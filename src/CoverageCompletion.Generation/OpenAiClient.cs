using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;
using OpenAI.Chat;

namespace CoverageCompletion.Generation;

/// <summary>
/// Thin wrapper over the official OpenAI .NET SDK's ChatClient. httpClient/retryPolicy are
/// injected so tests can substitute a fake transport instead of hitting the network, and a
/// zero-delay retry policy instead of waiting on real backoff timers. Transient-failure retry
/// (429/5xx/network) is handled by the SDK's own ClientRetryPolicy - default 3 retries, 4
/// attempts total - so no custom retry loop is needed here.
/// </summary>
public class OpenAiClient
{
    private const string DefaultModel = "gpt-4.1";

    private readonly HttpClient? _httpClient;
    private readonly PipelinePolicy? _retryPolicy;

    public OpenAiClient(HttpClient? httpClient = null, PipelinePolicy? retryPolicy = null)
    {
        _httpClient = httpClient;
        _retryPolicy = retryPolicy;
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

        var options = new OpenAIClientOptions();
        if (_httpClient is not null)
        {
            options.Transport = new HttpClientPipelineTransport(_httpClient);
        }

        if (_retryPolicy is not null)
        {
            options.RetryPolicy = _retryPolicy;
        }

        var chatClient = new ChatClient(model, new ApiKeyCredential(apiKey), options);
        var completion = await chatClient.CompleteChatAsync(
            [new UserChatMessage(prompt)], cancellationToken: ct);

        var content = completion.Value.Content.Count > 0 ? completion.Value.Content[0].Text : string.Empty;
        return ExtractCodeBlock(content);
    }

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
