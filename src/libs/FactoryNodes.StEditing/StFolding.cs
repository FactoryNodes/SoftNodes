using FactoryNodes.Iec61131.Syntax;
using FactoryNodes.StEditing.Syntax;

namespace FactoryNodes.StEditing;

/// <summary>
/// Un bloque que se puede plegar: lo que se oculta va de <paramref name="Start"/> (el final de la línea que lo abre, que se sigue
/// viendo) a <paramref name="End"/> (el final de la línea que lo cierra).
/// </summary>
public readonly record struct StFold(int Start, int End);

/// <summary>
/// Los bloques plegables de un texto ST, con el mismo lexer que los emisores: <c>IF</c>, <c>CASE</c>, <c>FOR</c>, <c>WHILE</c>,
/// <c>REPEAT</c>, las regiones (<c>REGION</c> de TIA, <c>/// REGION</c> de la librería; en Sysmac, <c>IF TRUE THEN // REGION</c> es un
/// <c>IF</c>), las declaraciones (<c>VAR…END_VAR</c>, <c>STRUCT</c>, <c>TYPE</c>, el bloque entero) y los comentarios <c>(* *)</c> de
/// varias líneas. Lo pinta cada editor (AvalonEdit en el EXE; en el web, Monaco).
/// </summary>
public static class StFolding
{
    private static readonly Dictionary<string, string> Closers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["END_IF"] = "IF", ["END_CASE"] = "CASE", ["END_FOR"] = "FOR", ["END_WHILE"] = "WHILE", ["END_REPEAT"] = "REPEAT",
        ["END_REGION"] = "REGION", ["END_VAR"] = "VAR", ["END_STRUCT"] = "STRUCT", ["END_TYPE"] = "TYPE",
        ["END_FUNCTION_BLOCK"] = "FUNCTION_BLOCK", ["END_FUNCTION"] = "FUNCTION", ["END_PROGRAM"] = "PROGRAM"
    };

    private static readonly HashSet<string> Openers = new(Closers.Values, StringComparer.OrdinalIgnoreCase);

    /// <summary>Ordenados por <see cref="StFold.Start"/>; solo los que ocupan más de una línea. Lo que no cierra no se pliega.</summary>
    public static IReadOnlyList<StFold> Find(string text)
    {
        var folds = new List<StFold>();
        var open = new List<(string Kind, int Offset)>();
        foreach (var t in PToken.From(text))
        {
            switch (t.Kind)
            {
                case StTokenKind.BlockComment:
                    Add(folds, text, t.Offset, t.End);
                    break;
                case StTokenKind.LineComment when Directive(t.Text) is { } word:
                    if (word.Equals("REGION", StringComparison.OrdinalIgnoreCase)) open.Add(("/// REGION", t.Offset));
                    else Close(folds, open, text, "/// REGION", t.End);
                    break;
                case StTokenKind.Identifier when Closers.TryGetValue(t.Text, out var opener):
                    Close(folds, open, text, opener, t.End);
                    break;
                case StTokenKind.Identifier when Openers.Contains(t.Text) || t.Text.StartsWith("VAR_", StringComparison.OrdinalIgnoreCase):
                    open.Add((t.Text.StartsWith("VAR", StringComparison.OrdinalIgnoreCase) ? "VAR" : t.Text.ToUpperInvariant(), t.Offset));
                    break;
            }
        }

        return folds.OrderBy(f => f.Start).ToList();
    }

    /// <summary><c>/// REGION Título</c> → «REGION»; <c>/// END_REGION</c> → «END_REGION»; otro comentario, null.</summary>
    private static string? Directive(string comment)
    {
        if (!comment.StartsWith("///", StringComparison.Ordinal)) return null;
        var word = comment[3..].TrimStart().Split(' ', '\t')[0];
        return word.Equals("REGION", StringComparison.OrdinalIgnoreCase) || word.Equals("END_REGION", StringComparison.OrdinalIgnoreCase) ? word : null;
    }

    /// <summary>Cierra el último bloque abierto de esa clase (y los que quedaran abiertos dentro, que no cerraron).</summary>
    private static void Close(List<StFold> folds, List<(string Kind, int Offset)> open, string text, string kind, int end)
    {
        var index = open.FindLastIndex(o => o.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        Add(folds, text, open[index].Offset, end);
        open.RemoveRange(index, open.Count - index);
    }

    private static void Add(List<StFold> folds, string text, int from, int to)
    {
        var start = LineEnd(text, from);
        var end = LineEnd(text, Math.Max(to - 1, from));
        if (end > start) folds.Add(new StFold(start, end));
    }

    private static int LineEnd(string text, int offset)
    {
        var newline = text.IndexOf('\n', offset);
        if (newline < 0) return text.Length;
        return newline > 0 && text[newline - 1] == '\r' ? newline - 1 : newline;
    }
}
