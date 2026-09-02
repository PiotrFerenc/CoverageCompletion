using System.Net;
using System.Net.Http.Headers;
using CoverageCompletion.Generation;
using FluentAssertions;

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

        result.Should().Be("public class Foo {}");
    }

    [Fact]
    public async Task CompleteAsync_NoCodeBlock_ReturnsWholeContentAsFallback()
    {
        var content = "public class Foo {}";
        var responseJson = ResponseJsonTemplate.Replace("CONTENT_PLACEHOLDER", content);
        var handler = new FakeHttpMessageHandler(responseJson);
        var client = new OpenAiClient(new HttpClient(handler));

        var result = await client.CompleteAsync("some prompt", CancellationToken.None);

        result.Should().Be("public class Foo {}");
    }

    [Fact]
    public async Task CompleteAsync_SendsBearerAuthAndModelFromEnvironment()
    {
        Environment.SetEnvironmentVariable("OPENAI_MODEL", "gpt-custom");
        var responseJson = ResponseJsonTemplate.Replace("CONTENT_PLACEHOLDER", "```csharp\\ncode\\n```");
        var handler = new FakeHttpMessageHandler(responseJson);
        var client = new OpenAiClient(new HttpClient(handler));

        await client.CompleteAsync("prompt text", CancellationToken.None);

        handler.LastAuthorization.Should().NotBeNull();
        handler.LastAuthorization!.Scheme.Should().Be("Bearer");
        handler.LastAuthorization.Parameter.Should().Be("test-key");
        handler.LastRequestBody.Should().Contain("gpt-custom");
        handler.LastRequestBody.Should().Contain("prompt text");

        Environment.SetEnvironmentVariable("OPENAI_MODEL", null);
    }

    [Fact]
    public async Task CompleteAsync_MissingApiKey_ThrowsInvalidOperationException()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        var handler = new FakeHttpMessageHandler(ResponseJsonTemplate);
        var client = new OpenAiClient(new HttpClient(handler));

        var act = () => client.CompleteAsync("prompt", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*OPENAI_API_KEY*");

        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
    }
}
