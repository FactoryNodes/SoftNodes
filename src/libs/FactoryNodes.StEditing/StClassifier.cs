using FactoryNodes.Iec61131.Syntax;
using FactoryNodes.Iec61131.Validation;
using FactoryNodes.StEditing.Semantics;
using FactoryNodes.StEditing.Syntax;

namespace FactoryNodes.StEditing;

/// <summary>Clase de un trozo de texto para colorearlo. Cada app elige el color de cada una.</summary>
public enum StSpanKind
{
    Keyword,
    Type,
    Comment,

    /// <summary><c>/// REGION …</c>, <c>/// PARAM</c>, <c>/// IO …</c>.</summary>
    Directive,
    String,
    Number,
    Variable,
    Parameter,
    Constant,
    Function,
    Member,

    /// <summary>Un nombre que no existe (ya lleva su error).</summary>
    Unknown
}

public readonly record struct StSpan(int Offset, int Length, StSpanKind Kind);

/// <summary>
/// Trozos a colorear de un texto ST, con el mismo lexer que los emisores. Con el análisis del cuerpo
/// (<see cref="StBodyAnalysis"/>) los nombres salen por lo que son (variable, parámetro, constante, función, miembro);
/// sin él, solo palabras clave, tipos, comentarios, literales y cadenas.
/// </summary>
public static class StClassifier
{
    private static readonly HashSet<string> DeclarationKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "TYPE", "END_TYPE", "STRUCT", "END_STRUCT", "FUNCTION", "END_FUNCTION", "FUNCTION_BLOCK", "END_FUNCTION_BLOCK", "VAR",
        "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT", "VAR_TEMP", "END_VAR", "CONSTANT", "RETAIN", "ARRAY"
    };

    public static IReadOnlyList<StSpan> Classify(string text, StBodyAnalysis? analysis = null)
    {
        var names = analysis?.Names.GroupBy(n => n.Offset).ToDictionary(g => g.Key, g => g.Last().Role) ?? [];
        var spans = new List<StSpan>();
        foreach (var t in PToken.From(text))
        {
            var kind = t.Kind switch
            {
                StTokenKind.LineComment when t.Text.StartsWith("///", StringComparison.Ordinal) => StSpanKind.Directive,
                StTokenKind.LineComment or StTokenKind.BlockComment => StSpanKind.Comment,
                StTokenKind.String => StSpanKind.String,
                StTokenKind.Number or StTokenKind.TypedLiteral => StSpanKind.Number,
                StTokenKind.Identifier => Identifier(t, names),
                _ => (StSpanKind?)null
            };
            if (kind is { } k) spans.Add(new StSpan(t.Offset, t.Text.Length, k));
        }

        return spans;
    }

    private static StSpanKind? Identifier(PToken t, Dictionary<int, StNameRole> names)
    {
        if (t.IsWord("TRUE") || t.IsWord("FALSE")) return StSpanKind.Number;
        if (StBodyParser.Keywords.Contains(t.Text) || DeclarationKeywords.Contains(t.Text)) return StSpanKind.Keyword;
        if (names.TryGetValue(t.Offset, out var role))
            return role switch
            {
                StNameRole.Variable => StSpanKind.Variable,
                StNameRole.Parameter => StSpanKind.Parameter,
                StNameRole.Constant => StSpanKind.Constant,
                StNameRole.Function => StSpanKind.Function,
                StNameRole.Member => StSpanKind.Member,
                _ => StSpanKind.Unknown
            };
        return ElementaryTypes.IsElementary(t.Text) ? StSpanKind.Type : null;
    }
}
