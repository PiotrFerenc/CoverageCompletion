using CoverageCompletion.Contracts;
using CoverageCompletion.Generation;
using FluentAssertions;

namespace CoverageCompletion.Generation.Tests;

public class PromptBuilderTests
{
    private static CoverageGap MakeGap() => new(
        ProjectPath: "/repo/src/Foo/Foo.csproj",
        FilePath: "/repo/src/Foo/OrderHandler.cs",
        Namespace: "Foo.Handlers",
        TypeName: "OrderHandler",
        MemberName: "Handle",
        UncoveredLines: new[] { 10, 11, 12 });

    [Fact]
    public void BuildInitialPrompt_IncludesGapDetailsAndSourceCode()
    {
        var gap = MakeGap();
        var prompt = new PromptBuilder().BuildInitialPrompt(gap, "public class OrderHandler {}", exampleTestCode: null);

        prompt.Should().Contain("OrderHandler");
        prompt.Should().Contain("Handle");
        prompt.Should().Contain("Foo.Handlers");
        prompt.Should().Contain("public class OrderHandler {}");
        prompt.Should().Contain("10-12");
        prompt.Should().Contain("xUnit");
        prompt.Should().Contain("FluentAssertions");
        prompt.Should().Contain("NSubstitute");
        prompt.Should().Contain("```csharp");
    }

    [Fact]
    public void BuildInitialPrompt_WithExample_IncludesExampleAndStyleHint()
    {
        var gap = MakeGap();
        var example = "public class OrderHandlerTests { [Fact] public void Foo() { result.IsSuccess.Should().BeTrue(); } }";

        var prompt = new PromptBuilder().BuildInitialPrompt(gap, "public class OrderHandler {}", example);

        prompt.Should().Contain(example);
        prompt.Should().Contain("Result");
    }

    [Fact]
    public void BuildInitialPrompt_WithoutExample_DoesNotMentionPattern()
    {
        var gap = MakeGap();
        var prompt = new PromptBuilder().BuildInitialPrompt(gap, "public class OrderHandler {}", exampleTestCode: null);

        prompt.Should().NotContain("Wzorzec stylu");
    }

    [Fact]
    public void BuildRegenerationPrompt_IncludesPreviousAttemptAndError()
    {
        var gap = MakeGap();
        var previous = new GeneratedTest("/repo/tests/Foo.Tests/OrderHandlerTests.cs", "public class OrderHandlerTests {}");
        var error = "CS0103: The name 'foo' does not exist in the current context";

        var prompt = new PromptBuilder().BuildRegenerationPrompt(gap, previous, error);

        prompt.Should().Contain("OrderHandler");
        prompt.Should().Contain("Handle");
        prompt.Should().Contain(previous.Content);
        prompt.Should().Contain(error);
        prompt.Should().Contain("```csharp");
    }
}
