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

    private readonly HttpClient _httpClient;

    public OpenAiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
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

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(responseJson);
        var content = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

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
