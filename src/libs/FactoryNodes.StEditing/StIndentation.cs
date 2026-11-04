using FactoryNodes.Iec61131.Syntax;
using FactoryNodes.StEditing.Syntax;

namespace FactoryNodes.StEditing;

/// <summary>Cambiar la sangría de una línea: de <see cref="LineStart"/>, los <see cref="OldLength"/> blancos por <see cref="Indent"/>.</summary>
public readonly record struct StReindent(int LineStart, int OldLength, string Indent);

/// <summary>
/// La sangría automática de los editores (PENDIENTES 90): tras <c>THEN</c>, <c>DO</c>, <c>OF</c>, <c>ELSE</c>, <c>REPEAT</c>, una etiqueta
/// de <c>CASE</c> o la apertura de una región o de una declaración, la línea nueva entra un nivel; <c>END_IF</c>, <c>ELSE</c>,
/// <c>UNTIL</c>… vuelven a la altura de lo que cierran al acabar de escribirlos. Solo mira el texto (el mismo lexer que los emisores).
/// </summary>
public static class StIndentation
{
    private static readonly string[] OpenAtEnd = ["THEN", "DO", "OF", "ELSE", "REPEAT"];

    private static readonly HashSet<string> OpenAtStart = new(StringComparer.OrdinalIgnoreCase)
    {
        "VAR", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT", "VAR_TEMP", "VAR_GLOBAL", "VAR_EXTERNAL", "STRUCT", "TYPE", "REGION", "BEGIN"
    };

    private static readonly Dictionary<string, string> Closers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["END_IF"] = "IF", ["ELSIF"] = "IF", ["END_CASE"] = "CASE", ["END_FOR"] = "FOR", ["END_WHILE"] = "WHILE",
        ["UNTIL"] = "REPEAT", ["END_REPEAT"] = "REPEAT", ["END_REGION"] = "REGION", ["END_VAR"] = "VAR", ["END_STRUCT"] = "STRUCT",
        ["END_TYPE"] = "TYPE"
    };

    /// <summary>La sangría de la línea que sigue a <paramref name="previousLine"/> al pulsar Intro (<paramref name="unit"/>: un nivel).</summary>
    public static string ForNewLine(string previousLine, string unit)
    {
        var indent = LeadingBlanks(previousLine);
        var code = Code(previousLine);
        if (code.Length == 0) return previousLine.TrimStart().StartsWith("/// REGION", StringComparison.OrdinalIgnoreCase) ? indent + unit : indent;
        var words = code.Split([' ', '\t', '('], StringSplitOptions.RemoveEmptyEntries);
        var last = words[^1];
        if (OpenAtEnd.Any(w => w.Equals(last, StringComparison.OrdinalIgnoreCase))) return indent + unit;
        if (OpenAtStart.Contains(words[0]) || words[0].Equals("FUNCTION_BLOCK", StringComparison.OrdinalIgnoreCase) && words.Length > 1)
            return indent + unit;
        if (IsCaseLabel(code)) return indent + unit;
        return indent;
    }

    /// <summary>
    /// Tras escribir un carácter: si la línea del cursor empieza por algo que cierra (<c>END_IF</c>, <c>ELSE</c>, <c>UNTIL</c>,
    /// <c>/// END_REGION</c>…) o es una etiqueta de <c>CASE</c> (<c>2:</c>), su sangría correcta; null si no hay que tocarla.
    /// </summary>
    public static StReindent? AfterTyping(string text, int caret, string unit)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var typed = text[lineStart..caret];
        var indent = LeadingBlanks(typed);
        var word = typed.Trim();
        if (word.Length == 0) return null;

        // solo se lee lo de arriba cuando la línea puede cerrar algo (se llama con cada tecla)
        var closes = word.Equals("/// END_REGION", StringComparison.OrdinalIgnoreCase) || word.Equals("ELSE", StringComparison.OrdinalIgnoreCase) ||
                     Closers.ContainsKey(word) || IsCaseLabel(word);
        if (!closes) return null;

        string? wanted;
        var openers = Openers(text[..lineStart]);
        if (word.Equals("/// END_REGION", StringComparison.OrdinalIgnoreCase))
            wanted = openers.LastOrDefault(o => o.Kind == "/// REGION").Indent;
        else if (word.Equals("ELSE", StringComparison.OrdinalIgnoreCase))
        {
            var owner = openers.LastOrDefault(o => o.Kind is "IF" or "CASE");
            wanted = owner.Kind == "CASE" ? owner.Indent + unit : owner.Indent;   // en un CASE, ELSE va a la altura de las etiquetas
        }
        else if (Closers.TryGetValue(word, out var kind))
            wanted = openers.LastOrDefault(o => o.Kind == kind).Indent;
        else if (IsCaseLabel(word) && openers.Count > 0 && openers[^1].Kind == "CASE")
            wanted = openers[^1].Indent + unit;
        else
            return null;

        return wanted is null || wanted == indent ? null : new StReindent(lineStart, indent.Length, wanted);
    }

    /// <summary>Los bloques abiertos al final de <paramref name="text"/>, con la sangría de la línea que los abre.</summary>
    private static List<(string Kind, string Indent)> Openers(string text)
    {
        var open = new List<(string Kind, string Indent)>();
        foreach (var t in PToken.From(text))
        {
            if (t.Kind == StTokenKind.LineComment)
            {
                var directive = t.Text.StartsWith("///", StringComparison.Ordinal) ? t.Text[3..].TrimStart() : "";
                if (directive.StartsWith("REGION", StringComparison.OrdinalIgnoreCase)) open.Add(("/// REGION", IndentAt(text, t.Offset)));
                else if (directive.StartsWith("END_REGION", StringComparison.OrdinalIgnoreCase)) Pop(open, "/// REGION");
                continue;
            }

            if (t.Kind != StTokenKind.Identifier) continue;
            var word = t.Text.ToUpperInvariant();
            if (word is "IF" or "CASE" or "FOR" or "WHILE" or "REPEAT" or "REGION" or "STRUCT" or "TYPE" || word.StartsWith("VAR", StringComparison.Ordinal) && OpenAtStart.Contains(word))
                open.Add((word.StartsWith("VAR", StringComparison.Ordinal) ? "VAR" : word, IndentAt(text, t.Offset)));
            else if (word.StartsWith("END_", StringComparison.Ordinal) && Closers.TryGetValue(word, out var kind))
                Pop(open, kind);
        }

        return open;
    }

    private static void Pop(List<(string Kind, string Indent)> open, string kind)
    {
        var index = open.FindLastIndex(o => o.Kind == kind);
        if (index >= 0) open.RemoveRange(index, open.Count - index);
    }

    private static string IndentAt(string text, int offset)
    {
        var start = offset > 0 ? text.LastIndexOf('\n', offset - 1) + 1 : 0;
        return LeadingBlanks(text[start..offset]);
    }

    private static string LeadingBlanks(string line) => line[..(line.Length - line.TrimStart(' ', '\t').Length)];

    /// <summary>La línea sin comentarios ni blancos al final (lo que cuenta para saber si abre un bloque).</summary>
    private static string Code(string line)
    {
        var code = string.Concat(PToken.From(line).Where(t => t.Kind is not (StTokenKind.LineComment or StTokenKind.BlockComment)).Select(t => t.Text));
        return code.Trim();
    }

    /// <summary><c>1:</c>, <c>2, 3:</c>, <c>10..20:</c>, <c>STATE_IDLE:</c>: una etiqueta de CASE (y no <c>x : INT;</c> ni <c>a := b</c>).</summary>
    private static bool IsCaseLabel(string code) =>
        code.EndsWith(':') && !code.Contains(":=", StringComparison.Ordinal) && !code.Contains(';') &&
        code[..^1].All(c => char.IsLetterOrDigit(c) || c is '_' or ',' or '.' or ' ' or '#' or '-');
}
