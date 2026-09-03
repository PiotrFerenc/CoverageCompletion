using CoverageCompletion.Contracts;
using CoverageCompletion.Generation;
using Shouldly;

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

        prompt.ShouldContain("OrderHandler");
        prompt.ShouldContain("Handle");
        prompt.ShouldContain("Foo.Handlers");
        prompt.ShouldContain("public class OrderHandler {}");
        prompt.ShouldContain("10-12");
        prompt.ShouldContain("xUnit");
        prompt.ShouldContain("Shouldly");
        prompt.ShouldContain("NSubstitute");
        prompt.ShouldContain("```csharp");
    }

    [Fact]
    public void BuildInitialPrompt_WithExample_IncludesExampleAndStyleHint()
    {
        var gap = MakeGap();
        var example = "public class OrderHandlerTests { [Fact] public void Foo() { result.IsSuccess.Should().BeTrue(); } }";

        var prompt = new PromptBuilder().BuildInitialPrompt(gap, "public class OrderHandler {}", example);

        prompt.ShouldContain(example);
        prompt.ShouldContain("Result");
    }

    [Fact]
    public void BuildInitialPrompt_WithoutExample_DoesNotMentionPattern()
    {
        var gap = MakeGap();
        var prompt = new PromptBuilder().BuildInitialPrompt(gap, "public class OrderHandler {}", exampleTestCode: null);

        prompt.ShouldNotContain("Wzorzec stylu");
    }

    [Fact]
    public void BuildRegenerationPrompt_IncludesPreviousAttemptAndError()
    {
        var gap = MakeGap();
        var previous = new GeneratedTest("/repo/tests/Foo.Tests/OrderHandlerTests.cs", "public class OrderHandlerTests {}");
        var error = "CS0103: The name 'foo' does not exist in the current context";

        var prompt = new PromptBuilder().BuildRegenerationPrompt(gap, previous, error);

        prompt.ShouldContain("OrderHandler");
        prompt.ShouldContain("Handle");
        prompt.ShouldContain(previous.Content);
        prompt.ShouldContain(error);
        prompt.ShouldContain("```csharp");
    }
}
