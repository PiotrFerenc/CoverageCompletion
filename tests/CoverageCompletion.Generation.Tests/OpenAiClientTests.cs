using System.Net;
using System.Net.Http.Headers;
using CoverageCompletion.Generation;
using Shouldly;

namespace CoverageCompletion.Generation.Tests;

public class OpenAiClientTests
{
    private class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public AuthenticationHeaderValue? LastAuthorization { get; private set; }

        public string? LastRequestBody { get; private set; }

        public FakeHttpMessageHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Capture what we need now: the request (and its Content) is disposed by the
            // caller's `using` block once SendAsync returns.
            LastAuthorization = request.Headers.Authorization;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody),
            };
            return response;
        }
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

        handler.LastAuthorization.ShouldNotBeNull();
        handler.LastAuthorization!.Scheme.ShouldBe("Bearer");
        handler.LastAuthorization.Parameter.ShouldBe("test-key");
        handler.LastRequestBody!.ShouldContain("gpt-custom");
        handler.LastRequestBody!.ShouldContain("prompt text");

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
            return Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body) });
        }
    }

    // Zero-length delays keep these tests fast; they only verify retry counts/outcomes, not timing.
    private static readonly TimeSpan[] NoDelay = { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero };

    [Fact]
    public async Task CompleteAsync_SucceedsAfterTwoTransientServerErrors()
    {
        var responseJson = ResponseJsonTemplate.Replace("CONTENT_PLACEHOLDER", "```csharp\\ncode\\n```");
        var handler = new ScriptedHttpMessageHandler(
            responseJson, HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var client = new OpenAiClient(new HttpClient(handler), NoDelay);

        var result = await client.CompleteAsync("prompt", CancellationToken.None);

        result.ShouldBe("code");
        handler.CallCount.ShouldBe(3);
    }

    [Fact]
    public async Task CompleteAsync_Unauthorized_DoesNotRetry()
    {
        var handler = new ScriptedHttpMessageHandler(ResponseJsonTemplate, HttpStatusCode.Unauthorized);
        var client = new OpenAiClient(new HttpClient(handler), NoDelay);

        var act = () => client.CompleteAsync("prompt", CancellationToken.None);

        await Should.ThrowAsync<HttpRequestException>(act);
        handler.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task CompleteAsync_PersistentRateLimit_ExhaustsRetriesAndThrows()
    {
        var handler = new ScriptedHttpMessageHandler(ResponseJsonTemplate, HttpStatusCode.TooManyRequests);
        var client = new OpenAiClient(new HttpClient(handler), NoDelay);

        var act = () => client.CompleteAsync("prompt", CancellationToken.None);

        var ex = await Should.ThrowAsync<HttpRequestException>(act);
        ex.Message.ShouldContain("after 4 attempts");
    }

    [Fact]
    public async Task CompleteAsync_RetryIsBounded_DoesNotCallMoreThanMaxAttempts()
    {
        // Persistent 500s should stop after exactly retryDelays.Length + 1 attempts, never looping forever.
        var handler = new ScriptedHttpMessageHandler(ResponseJsonTemplate, HttpStatusCode.InternalServerError);
        var client = new OpenAiClient(new HttpClient(handler), NoDelay);

        var act = () => client.CompleteAsync("prompt", CancellationToken.None);

        await Should.ThrowAsync<HttpRequestException>(act);
        handler.CallCount.ShouldBe(NoDelay.Length + 1);
    }
}
