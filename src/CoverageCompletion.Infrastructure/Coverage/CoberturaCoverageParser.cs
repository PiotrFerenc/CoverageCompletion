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

        // Per the Cobertura format, <class filename="..."> is relative to the <source> root(s)
        // declared once near the top of the document - coverlet emits "/" as that root on Linux,
        // which makes filename look like an absolute path with the leading slash stripped off.
        // Resolve it here so callers get a real, directly-usable path instead of a bare fragment.
        // Cobertura allows more than one <source>; when it's absent entirely, filename is used as-is.
        var sourceRoots = document.Root?.Element("sources")?.Elements("source")
            .Select(e => e.Value.Trim())
            .Where(root => root.Length > 0)
            .ToList() ?? [];

        foreach (var classElement in document.Descendants("class"))
        {
            var className = (string?)classElement.Attribute("name") ?? string.Empty;
            var rawFilename = (string?)classElement.Attribute("filename") ?? string.Empty;
            var filename = ResolveFilename(rawFilename, sourceRoots);
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

    private static string ResolveFilename(string filename, IReadOnlyList<string> sourceRoots)
    {
        if (sourceRoots.Count == 0 || Path.IsPathRooted(filename))
        {
            return filename;
        }

        if (sourceRoots.Count == 1)
        {
            return Path.Combine(sourceRoots[0], filename);
        }

        // Multiple <source> roots (e.g. multi-module Cobertura reports): try each in turn and
        // prefer the one that actually resolves to a real file, falling back to the first root
        // when none do (e.g. unit tests parsing inline XML with no files backing it on disk).
        foreach (var root in sourceRoots)
        {
            var candidate = Path.Combine(root, filename);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(sourceRoots[0], filename);
    }

    private static (string Namespace, string TypeName) SplitClassName(string className)
    {
        var lastDot = className.LastIndexOf('.');
        return lastDot < 0
            ? (string.Empty, className)
            : (className[..lastDot], className[(lastDot + 1)..]);
    }
}
