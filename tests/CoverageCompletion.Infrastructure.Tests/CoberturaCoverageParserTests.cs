using CoverageCompletion.Infrastructure.Coverage;
using FluentAssertions;

namespace CoverageCompletion.Infrastructure.Tests;

public class CoberturaCoverageParserTests
{
    private const string SampleXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.5" branch-rate="1" version="1.9">
          <packages>
            <package name="MyApp" line-rate="0.5">
              <classes>
                <class name="MyApp.Calculator" filename="/repo/src/MyApp/Calculator.cs" line-rate="0.5">
                  <methods>
                    <method name="Add" signature="(int,int)" line-rate="1">
                      <lines>
                        <line number="10" hits="3" />
                        <line number="11" hits="3" />
                      </lines>
                    </method>
                    <method name="Subtract" signature="(int,int)" line-rate="0">
                      <lines>
                        <line number="20" hits="0" />
                        <line number="21" hits="0" />
                      </lines>
                    </method>
                  </methods>
                  <lines>
                    <line number="10" hits="3" />
                    <line number="11" hits="3" />
                    <line number="20" hits="0" />
                    <line number="21" hits="0" />
                  </lines>
                </class>
              </classes>
            </package>
          </packages>
        </coverage>
        """;

    [Fact]
    public void Parse_SkipsFullyCoveredMethods()
    {
        var gaps = CoberturaCoverageParser.Parse(SampleXml);

        gaps.Should().ContainSingle();
    }

    [Fact]
    public void Parse_ProducesGapForUncoveredMethod_WithNamespaceAndTypeSplitFromClassName()
    {
        var gaps = CoberturaCoverageParser.Parse(SampleXml);

        var gap = gaps.Single();
        gap.Namespace.Should().Be("MyApp");
        gap.TypeName.Should().Be("Calculator");
        gap.MemberName.Should().Be("Subtract");
        gap.FilePath.Should().Be("/repo/src/MyApp/Calculator.cs");
        gap.UncoveredLines.Should().BeEquivalentTo([20, 21]);
    }

    [Fact]
    public void Parse_FallsBackToClassLevelLines_WhenNoMethodsElement()
    {
        const string xml = """
            <coverage>
              <packages>
                <package name="MyApp">
                  <classes>
                    <class name="MyApp.Widget" filename="/repo/src/MyApp/Widget.cs">
                      <lines>
                        <line number="5" hits="0" />
                        <line number="6" hits="1" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        var gaps = CoberturaCoverageParser.Parse(xml);

        var gap = gaps.Single();
        gap.TypeName.Should().Be("Widget");
        gap.MemberName.Should().Be("Widget");
        gap.UncoveredLines.Should().BeEquivalentTo([5]);
    }

    [Fact]
    public void Parse_ReturnsNoGaps_WhenEverythingIsCovered()
    {
        const string xml = """
            <coverage>
              <packages>
                <package name="MyApp">
                  <classes>
                    <class name="MyApp.FullyCovered" filename="/repo/src/MyApp/FullyCovered.cs">
                      <methods>
                        <method name="DoIt">
                          <lines>
                            <line number="1" hits="5" />
                          </lines>
                        </method>
                      </methods>
                      <lines>
                        <line number="1" hits="5" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        var gaps = CoberturaCoverageParser.Parse(xml);

        gaps.Should().BeEmpty();
    }

    [Fact]
    public void Parse_TypeWithoutNamespace_LeavesNamespaceEmpty()
    {
        const string xml = """
            <coverage>
              <packages>
                <package name="">
                  <classes>
                    <class name="TopLevelType" filename="/repo/TopLevelType.cs">
                      <lines>
                        <line number="1" hits="0" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        var gaps = CoberturaCoverageParser.Parse(xml);

        var gap = gaps.Single();
        gap.Namespace.Should().BeEmpty();
        gap.TypeName.Should().Be("TopLevelType");
    }

    [Fact]
    public void Parse_WithSingleSourceRoot_CombinesItWithARelativeFilename()
    {
        // Regression test for the real bug this parser used to have: coverlet on Linux emits
        // "/" as the <source> root, and a relative filename with the leading slash stripped -
        // the single-source case needs to combine the two, not use the filename as-is.
        const string xml = """
            <coverage>
              <sources>
                <source>/repo</source>
              </sources>
              <packages>
                <package name="MyApp">
                  <classes>
                    <class name="MyApp.Calculator" filename="src/MyApp/Calculator.cs">
                      <lines>
                        <line number="1" hits="0" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        var gaps = CoberturaCoverageParser.Parse(xml);

        gaps.Single().FilePath.Should().Be("/repo/src/MyApp/Calculator.cs");
    }

    [Fact]
    public void Parse_WithSourceRootPresent_LeavesAnAlreadyRootedFilenameUnchanged()
    {
        const string xml = """
            <coverage>
              <sources>
                <source>/repo</source>
              </sources>
              <packages>
                <package name="MyApp">
                  <classes>
                    <class name="MyApp.Calculator" filename="/repo/src/MyApp/Calculator.cs">
                      <lines>
                        <line number="1" hits="0" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        var gaps = CoberturaCoverageParser.Parse(xml);

        gaps.Single().FilePath.Should().Be("/repo/src/MyApp/Calculator.cs");
    }

    [Fact]
    public void Parse_WithNoSourcesElement_UsesTheFilenameAsIs_WithoutCrashing()
    {
        // Some Cobertura-emitting tools omit <sources> entirely; the parser must not throw and
        // should just pass the filename through unresolved.
        const string xml = """
            <coverage>
              <packages>
                <package name="MyApp">
                  <classes>
                    <class name="MyApp.Calculator" filename="src/MyApp/Calculator.cs">
                      <lines>
                        <line number="1" hits="0" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        var gaps = CoberturaCoverageParser.Parse(xml);

        gaps.Single().FilePath.Should().Be("src/MyApp/Calculator.cs");
    }

    [Fact]
    public void Parse_WithMultipleSourceRoots_PrefersTheRootThatResolvesToAnExistingFile()
    {
        var tempRoot = Directory.CreateTempSubdirectory("cobertura-multi-source-");
        try
        {
            var rootA = Path.Combine(tempRoot.FullName, "rootA");
            var rootB = Path.Combine(tempRoot.FullName, "rootB");
            Directory.CreateDirectory(Path.Combine(rootB, "src", "MyApp"));
            // Only rootB has the file for real - rootA is a plausible-looking root that just
            // doesn't happen to contain this particular file (the multi-module scenario).
            File.WriteAllText(Path.Combine(rootB, "src", "MyApp", "Calculator.cs"), "// stub");

            var xml = $"""
                <coverage>
                  <sources>
                    <source>{rootA}</source>
                    <source>{rootB}</source>
                  </sources>
                  <packages>
                    <package name="MyApp">
                      <classes>
                        <class name="MyApp.Calculator" filename="src/MyApp/Calculator.cs">
                          <lines>
                            <line number="1" hits="0" />
                          </lines>
                        </class>
                      </classes>
                    </package>
                  </packages>
                </coverage>
                """;

            var gaps = CoberturaCoverageParser.Parse(xml);

            gaps.Single().FilePath.Should().Be(Path.Combine(rootB, "src", "MyApp", "Calculator.cs"));
        }
        finally
        {
            Directory.Delete(tempRoot.FullName, recursive: true);
        }
    }

    [Fact]
    public void Parse_AsyncMethodStateMachine_FoldsBackToTheDeclaringMethod()
    {
        // Regression test: async methods compile to a nested "Outer/<Method>d__N" state-machine
        // type, and Cobertura reports the gap against "MoveNext" on that synthetic type. Left
        // unresolved, TypeName ("CancelOrderHandler/<Handle>d__2") breaks file-path construction
        // downstream because it contains '/' and '<'/'>' - this must fold back to the real method.
        const string xml = """
            <coverage>
              <packages>
                <package name="MyApp">
                  <classes>
                    <class name="MyApp.CancelOrderHandler/&lt;Handle&gt;d__2" filename="/repo/src/MyApp/CancelOrderHandler.cs">
                      <methods>
                        <method name="MoveNext">
                          <lines>
                            <line number="15" hits="0" />
                          </lines>
                        </method>
                        <method name="SetStateMachine">
                          <lines>
                            <line number="30" hits="0" />
                          </lines>
                        </method>
                      </methods>
                      <lines>
                        <line number="15" hits="0" />
                        <line number="30" hits="0" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        var gaps = CoberturaCoverageParser.Parse(xml);

        var gap = gaps.Should().ContainSingle().Subject;
        gap.TypeName.Should().Be("CancelOrderHandler");
        gap.MemberName.Should().Be("Handle");
        gap.UncoveredLines.Should().BeEquivalentTo([15]);
    }

    [Fact]
    public void Parse_WithMultipleSourceRoots_FallsBackToTheFirstRoot_WhenNoneResolveToAnExistingFile()
    {
        const string xml = """
            <coverage>
              <sources>
                <source>/root/a</source>
                <source>/root/b</source>
              </sources>
              <packages>
                <package name="MyApp">
                  <classes>
                    <class name="MyApp.Calculator" filename="src/MyApp/Calculator.cs">
                      <lines>
                        <line number="1" hits="0" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """;

        var gaps = CoberturaCoverageParser.Parse(xml);

        gaps.Single().FilePath.Should().Be("/root/a/src/MyApp/Calculator.cs");
    }
}
