using FactoryNodes.Iec61131.Syntax;
using FactoryNodes.StEditing.Syntax;

namespace FactoryNodes.StEditing;

/// <summary>Un trozo que se resalta: de <see cref="Offset"/>, <see cref="Length"/> caracteres.</summary>
public readonly record struct StRange(int Offset, int Length);

/// <summary>
/// Lo que se resalta con el cursor en un sitio (PENDIENTES 90): en una palabra de un bloque (<c>IF</c>, <c>ELSIF</c>, <c>ELSE</c>,
/// <c>END_IF</c>; <c>CASE</c>…<c>END_CASE</c>; <c>REPEAT</c>, <c>UNTIL</c>, <c>END_REPEAT</c>; las regiones…), las demás de ese mismo
/// bloque; en un nombre, sus otras apariciones. Nada en comentarios, cadenas ni números.
/// </summary>
public static class StHighlights
{
    private static readonly Dictionary<string, string> Closers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["END_IF"] = "IF", ["END_CASE"] = "CASE", ["END_FOR"] = "FOR", ["END_WHILE"] = "WHILE", ["END_REPEAT"] = "REPEAT",
        ["END_REGION"] = "REGION", ["END_VAR"] = "VAR", ["END_STRUCT"] = "STRUCT", ["END_TYPE"] = "TYPE",
        ["END_FUNCTION_BLOCK"] = "FUNCTION_BLOCK", ["END_FUNCTION"] = "FUNCTION"
    };

    public static IReadOnlyList<StRange> At(string text, int caret)
    {
        var tokens = PToken.From(text);
        var here = tokens.LastOrDefault(t => t.Kind == StTokenKind.Identifier && caret >= t.Offset && caret <= t.End);
        if (here.Text is null) return [];

        foreach (var group in Blocks(tokens))
            if (group.Any(t => t.Offset == here.Offset))
                return group.Select(t => new StRange(t.Offset, t.Text.Length)).ToList();
        if (IsKeyword(here.Text)) return [];

        var same = tokens.Where(t => t.Kind == StTokenKind.Identifier && t.Text.Equals(here.Text, StringComparison.OrdinalIgnoreCase))
            .Select(t => new StRange(t.Offset, t.Text.Length)).ToList();
        return same.Count > 1 ? same : [];
    }

    /// <summary>Cada bloque cerrado con sus palabras: la que abre, las de en medio (ELSIF, ELSE, UNTIL) y la que cierra.</summary>
    private static List<List<PToken>> Blocks(IReadOnlyList<PToken> tokens)
    {
        var done = new List<List<PToken>>();
        var open = new List<(string Kind, List<PToken> Words)>();
        foreach (var t in tokens)
        {
            if (t.Kind != StTokenKind.Identifier) continue;
            var word = t.Text.ToUpperInvariant();
            if (Closers.TryGetValue(word, out var kind))
            {
                var index = open.FindLastIndex(o => o.Kind == kind);
                if (index < 0) continue;
                open[index].Words.Add(t);
                done.Add(open[index].Words);
                open.RemoveRange(index, open.Count - index);
            }
            else if (Closers.ContainsValue(word) || word.StartsWith("VAR", StringComparison.Ordinal) && IsVarSection(word))
                open.Add((word.StartsWith("VAR", StringComparison.Ordinal) ? "VAR" : word, [t]));
            else if (word is "ELSIF" or "ELSE" && open.FindLastIndex(o => o.Kind is "IF" or "CASE") is var owner and >= 0)
            {
                if (word == "ELSIF" && open[owner].Kind != "IF") continue;
                open[owner].Words.Add(t);
            }
            else if (word == "UNTIL" && open.FindLastIndex(o => o.Kind == "REPEAT") is var repeat and >= 0)
                open[repeat].Words.Add(t);
        }

        return done;
    }

    private static bool IsVarSection(string word) => word is "VAR" or "VAR_INPUT" or "VAR_OUTPUT" or "VAR_IN_OUT" or "VAR_TEMP" or "VAR_GLOBAL" or "VAR_EXTERNAL";

    private static bool IsKeyword(string word) =>
        StBodyParser.Keywords.Contains(word) || Closers.ContainsKey(word) || Closers.ContainsValue(word.ToUpperInvariant()) || IsVarSection(word.ToUpperInvariant()) ||
        word.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || word.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
}
