using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using CoverageCompletion.Generation;
using Shouldly;

namespace CoverageCompletion.Generation.Tests;

public class OpenAiClientTests
{
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public string? LastAuthorization { get; private set; }

        public string? LastRequestBody { get; private set; }

        public FakeHttpMessageHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Capture what we need now: the request (and its Content) is disposed by the
            // caller's pipeline once SendAsync returns.
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// Handler that returns a scripted sequence of status codes (one per call, last one repeats
    /// once exhausted) so retry behaviour can be tested without hitting the network.
    /// </summary>
    private class ScriptedHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode[] _statusCodes;
        private readonly string _successBody;

        public int CallCount { get; private set; }

        public ScriptedHttpMessageHandler(string successBody, params HttpStatusCode[] statusCodes)
        {
            _successBody = successBody;
            _statusCodes = statusCodes;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var index = Math.Min(CallCount, _statusCodes.Length - 1);
            CallCount++;
            var statusCode = _statusCodes[index];
            var body = statusCode == HttpStatusCode.OK ? _successBody : "{}";
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    // Overrides the SDK's default exponential backoff with a zero delay so retry tests run fast;
    // retry/no-retry decisions (429/5xx retry, 401 doesn't) are still the SDK's real ClientRetryPolicy logic.
    private class NoDelayRetryPolicy : ClientRetryPolicy
    {
        public NoDelayRetryPolicy(int maxRetries) : base(maxRetries)
        {
        }

        protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount) => TimeSpan.Zero;
    }

    private const string ResponseJsonTemplate = """
        {
          "choices": [
            { "message": { "role": "assistant", "content": "CONTENT_PLACEHOLDER" } }
          ]
        }
        """;

    public OpenAiClientTests()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        Environment.SetEnvironmentVariable("OPENAI_MODEL", null);
    }

    [Fact]
    public async Task CompleteAsync_ExtractsCodeBlockFromResponse()
    {
        var content = "Here is the test:\\n```csharp\\npublic class Foo {}\\n```\\nHope this helps.";
        var responseJson = ResponseJsonTemplate.Replace("CONTENT_PLACEHOLDER", content);
        var handler = new FakeHttpMessageHandler(responseJson);
        var client = new OpenAiClient(new HttpClient(handler));

        var result = await client.CompleteAsync("some prompt", CancellationToken.None);

        result.ShouldBe("public class Foo {}");
    }

    [Fact]
    public async Task CompleteAsync_NoCodeBlock_ReturnsWholeContentAsFallback()
    {
        var content = "public class Foo {}";
        var responseJson = ResponseJsonTemplate.Replace("CONTENT_PLACEHOLDER", content);
        var handler = new FakeHttpMessageHandler(responseJson);
        var client = new OpenAiClient(new HttpClient(handler));

        var result = await client.CompleteAsync("some prompt", CancellationToken.None);

        result.ShouldBe("public class Foo {}");
    }

    [Fact]
    public async Task CompleteAsync_SendsBearerAuthAndModelFromEnvironment()
    {
        Environment.SetEnvironmentVariable("OPENAI_MODEL", "gpt-custom");
        var responseJson = ResponseJsonTemplate.Replace("CONTENT_PLACEHOLDER", "```csharp\\ncode\\n```");
        var handler = new FakeHttpMessageHandler(responseJson);
        var client = new OpenAiClient(new HttpClient(handler));

        await client.CompleteAsync("prompt text", CancellationToken.None);

        handler.LastAuthorization.ShouldBe("Bearer test-key");
        handler.LastRequestBody.ShouldNotBeNull();
        handler.LastRequestBody!.ShouldContain("gpt-custom");
        handler.LastRequestBody.ShouldContain("prompt text");

        Environment.SetEnvironmentVariable("OPENAI_MODEL", null);
    }

    [Fact]
    public async Task CompleteAsync_MissingApiKey_ThrowsInvalidOperationException()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        var handler = new FakeHttpMessageHandler(ResponseJsonTemplate);
        var client = new OpenAiClient(new HttpClient(handler));

        var act = () => client.CompleteAsync("prompt", CancellationToken.None);

        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("OPENAI_API_KEY");

        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
    }

    [Fact]
    public async Task CompleteAsync_SucceedsAfterTwoTransientServerErrors()
    {
        var responseJson = ResponseJsonTemplate.Replace("CONTENT_PLACEHOLDER", "```csharp\\ncode\\n```");
        var handler = new ScriptedHttpMessageHandler(
            responseJson, HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var client = new OpenAiClient(new HttpClient(handler), new NoDelayRetryPolicy(3));

        var result = await client.CompleteAsync("prompt", CancellationToken.None);

        result.ShouldBe("code");
        handler.CallCount.ShouldBe(3);
    }

    [Fact]
    public async Task CompleteAsync_Unauthorized_DoesNotRetry()
    {
        var handler = new ScriptedHttpMessageHandler(ResponseJsonTemplate, HttpStatusCode.Unauthorized);
        var client = new OpenAiClient(new HttpClient(handler), new NoDelayRetryPolicy(3));

        var act = () => client.CompleteAsync("prompt", CancellationToken.None);

        var ex = await Should.ThrowAsync<ClientResultException>(act);
        ex.Status.ShouldBe(401);
        handler.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task CompleteAsync_PersistentRateLimit_ExhaustsRetriesAndThrows()
    {
        var handler = new ScriptedHttpMessageHandler(ResponseJsonTemplate, HttpStatusCode.TooManyRequests);
        var client = new OpenAiClient(new HttpClient(handler), new NoDelayRetryPolicy(3));

        var act = () => client.CompleteAsync("prompt", CancellationToken.None);

        var ex = await Should.ThrowAsync<ClientResultException>(act);
        ex.Status.ShouldBe(429);
        // 1 initial attempt + 3 retries.
        handler.CallCount.ShouldBe(4);
    }

    [Fact]
    public async Task CompleteAsync_RetryIsBounded_DoesNotCallMoreThanMaxAttempts()
    {
        // Persistent 500s should stop after exactly maxRetries + 1 attempts, never looping forever.
        var handler = new ScriptedHttpMessageHandler(ResponseJsonTemplate, HttpStatusCode.InternalServerError);
        var client = new OpenAiClient(new HttpClient(handler), new NoDelayRetryPolicy(3));

        var act = () => client.CompleteAsync("prompt", CancellationToken.None);

        await Should.ThrowAsync<ClientResultException>(act);
        handler.CallCount.ShouldBe(4);
    }
}
