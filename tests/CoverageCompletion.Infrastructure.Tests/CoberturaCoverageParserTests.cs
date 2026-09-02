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
}
