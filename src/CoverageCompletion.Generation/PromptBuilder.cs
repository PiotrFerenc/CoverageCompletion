using System.Text;
using CoverageCompletion.Contracts;

namespace CoverageCompletion.Generation;

/// <summary>
/// Builds LLM prompts for test generation. Pure functions, no side effects.
/// </summary>
public class PromptBuilder
{
    public string BuildInitialPrompt(CoverageGap gap, string targetSourceCode, string? exampleTestCode)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            $"Wygeneruj test jednostkowy xUnit + FluentAssertions + NSubstitute dla poniższej klasy/metody, " +
            $"pokrywający niepokryte linie {FormatLines(gap.UncoveredLines)}.");
        sb.AppendLine();
        sb.AppendLine($"Namespace: {gap.Namespace}");
        sb.AppendLine($"Typ: {gap.TypeName}");
        sb.AppendLine($"Członek do przetestowania: {gap.MemberName}");
        sb.AppendLine();
        sb.AppendLine("Kod źródłowy klasy:");
        sb.AppendLine("```csharp");
        sb.AppendLine(targetSourceCode);
        sb.AppendLine("```");

        if (exampleTestCode is not null)
        {
            sb.AppendLine();
            sb.AppendLine(
                "Wzorzec stylu asercji do naśladowania (zwróć uwagę na sposób asercji na Result/IsSuccess/IsFailed, jeśli występuje):");
            sb.AppendLine("```csharp");
            sb.AppendLine(exampleTestCode);
            sb.AppendLine("```");
        }

        sb.AppendLine();
        sb.AppendLine("Odpowiedz WYŁĄCZNIE kodem C# w jednym bloku ```csharp ... ```, bez żadnego dodatkowego tekstu.");

        return sb.ToString();
    }

    public string BuildRegenerationPrompt(CoverageGap gap, GeneratedTest previousAttempt, string buildOrTestError)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            $"Poprzednio wygenerowany test dla {gap.TypeName}.{gap.MemberName} nie przeszedł budowy/testów. Popraw błąd.");
        sb.AppendLine();
        sb.AppendLine("Poprzednia treść pliku testowego:");
        sb.AppendLine("```csharp");
        sb.AppendLine(previousAttempt.Content);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Treść błędu buildu/testu:");
        sb.AppendLine("```");
        sb.AppendLine(buildOrTestError);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine(
            "Zwróć pełną poprawioną treść pliku testowego. Odpowiedz WYŁĄCZNIE kodem C# w jednym bloku ```csharp ... ```, " +
            "bez żadnego dodatkowego tekstu.");

        return sb.ToString();
    }

    private static string FormatLines(IReadOnlyList<int> lines)
    {
        if (lines.Count == 0)
        {
            return "brak";
        }

        return lines.Count == 1 ? lines[0].ToString() : $"{lines.Min()}-{lines.Max()}";
    }
}
