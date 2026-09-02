using System.Xml.Linq;
using CoverageCompletion.Contracts;

namespace CoverageCompletion.Infrastructure.Coverage;

/// <summary>
/// Parses a Cobertura coverage XML document into <see cref="CoverageGap"/>s, one per method
/// that has at least one uncovered line (falling back to one gap per class when the document
/// has no per-method breakdown). Pure/no I/O so it can be unit tested against inline XML.
/// </summary>
public static class CoberturaCoverageParser
{
    public static IReadOnlyList<CoverageGap> Parse(string coberturaXml)
    {
        var document = XDocument.Parse(coberturaXml);
        var gaps = new List<CoverageGap>();

        foreach (var classElement in document.Descendants("class"))
        {
            var className = (string?)classElement.Attribute("name") ?? string.Empty;
            var filename = (string?)classElement.Attribute("filename") ?? string.Empty;
            var (ns, typeName) = SplitClassName(className);

            // Cobertura's default project path guess: the directory the source file lives in.
            // CoverageAnalyzer refines this to the nearest .csproj once it has filesystem access.
            var projectPath = Path.GetDirectoryName(filename) ?? string.Empty;

            var methodsElement = classElement.Element("methods");
            if (methodsElement is not null)
            {
                foreach (var methodElement in methodsElement.Elements("method"))
                {
                    var uncoveredLines = GetUncoveredLineNumbers(methodElement.Element("lines"));
                    if (uncoveredLines.Count == 0)
                    {
                        continue;
                    }

                    var methodName = (string?)methodElement.Attribute("name") ?? string.Empty;
                    gaps.Add(new CoverageGap(projectPath, filename, ns, typeName, methodName, uncoveredLines));
                }
            }
            else
            {
                var uncoveredLines = GetUncoveredLineNumbers(classElement.Element("lines"));
                if (uncoveredLines.Count > 0)
                {
                    gaps.Add(new CoverageGap(projectPath, filename, ns, typeName, typeName, uncoveredLines));
                }
            }
        }

        return gaps;
    }

    private static IReadOnlyList<int> GetUncoveredLineNumbers(XElement? linesElement)
    {
        if (linesElement is null)
        {
            return Array.Empty<int>();
        }

        return linesElement.Elements("line")
            .Where(line => (int?)line.Attribute("hits") == 0)
            .Select(line => (int?)line.Attribute("number"))
            .Where(number => number is > 0)
            .Select(number => number!.Value)
            .ToList();
    }

    private static (string Namespace, string TypeName) SplitClassName(string className)
    {
        var lastDot = className.LastIndexOf('.');
        return lastDot < 0
            ? (string.Empty, className)
            : (className[..lastDot], className[(lastDot + 1)..]);
    }
}
