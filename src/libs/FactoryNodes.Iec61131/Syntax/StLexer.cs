using System.Text;

namespace FactoryNodes.Iec61131.Syntax;

public enum StTokenKind
{
    Identifier,

    /// <summary><c>WORD#16#0001</c>, <c>T#600ms</c>, <c>UDINT#3600</c>: tipo + '#' + valor, en un solo token.</summary>
    TypedLiteral,

    /// <summary><c>20</c>, <c>0.0</c>, <c>3600.0</c>, <c>16#FF</c> (con base), <c>1E3</c>.</summary>
    Number,

    /// <summary>Entre comillas simples o dobles (en TIA, las dobles son nombres de bloque: <c>"AlarmManager"</c>).</summary>
    String,

    /// <summary><c>// …</c> hasta el final de la línea (incluye las directivas <c>///</c>).</summary>
    LineComment,

    /// <summary><c>(* … *)</c>, puede ocupar varias líneas.</summary>
    BlockComment,

    Whitespace,
    NewLine,

    /// <summary>Operadores y signos: <c>:=</c>, <c>&lt;&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c>, <c>=&gt;</c>, <c>**</c> o un solo carácter.</summary>
    Symbol
}

/// <summary>Un trozo del texto fuente. Unir el <see cref="Text"/> de todos los tokens devuelve el texto original.</summary>
public readonly record struct StToken(StTokenKind Kind, string Text)
{
    public bool IsTrivia => Kind is StTokenKind.Whitespace or StTokenKind.NewLine or StTokenKind.LineComment or StTokenKind.BlockComment;

    public bool IsDirective => Kind == StTokenKind.LineComment && Text.StartsWith("///", StringComparison.Ordinal);

    public bool Is(string symbol) => Kind == StTokenKind.Symbol && Text == symbol;
}

/// <summary>
/// Lexer de Texto Estructurado sin pérdidas: no descarta nada (espacios, saltos de línea, comentarios), así que
/// los emisores pueden reescribir identificadores o llamadas y conservar exactamente el formato y los comentarios
/// del fuente, que es lo que se validó en los PLC reales. Ver WHY.md.
/// </summary>
public static class StLexer
{
    private static readonly string[] MultiCharSymbols = [":=", "<>", "<=", ">=", "=>", "**"];

    public static List<StToken> Tokenize(string text)
    {
        var tokens = new List<StToken>();
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var c = text[i];

            if (c == '\n')
            {
                tokens.Add(new(StTokenKind.NewLine, "\n"));
                i++;
                continue;
            }

            if (c == '\r')
            {
                var len = i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                tokens.Add(new(StTokenKind.NewLine, text.Substring(i, len)));
                i += len;
                continue;
            }

            if (c is ' ' or '\t')
            {
                while (i < text.Length && text[i] is ' ' or '\t') i++;
                tokens.Add(new(StTokenKind.Whitespace, text[start..i]));
                continue;
            }

            if (c == '/' && Peek(text, i + 1) == '/')
            {
                while (i < text.Length && text[i] is not '\n' and not '\r') i++;
                tokens.Add(new(StTokenKind.LineComment, text[start..i]));
                continue;
            }

            if (c == '(' && Peek(text, i + 1) == '*')
            {
                var end = text.IndexOf("*)", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                tokens.Add(new(StTokenKind.BlockComment, text[start..i]));
                continue;
            }

            if (c is '\'' or '"')
            {
                i++;
                while (i < text.Length && text[i] != c && text[i] is not '\n' and not '\r') i++;
                if (i < text.Length && text[i] == c) i++;
                tokens.Add(new(StTokenKind.String, text[start..i]));
                continue;
            }

            if (IsIdentifierStart(c))
            {
                while (i < text.Length && IsIdentifierPart(text[i])) i++;
                if (Peek(text, i) == '#')
                {
                    // Literal con tipo: todo lo que sigue al '#' que sea letra, dígito, '#', '_' o '.' (T#1h2m, 16#FF).
                    i++;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '#' or '_' or '.')) i++;
                    tokens.Add(new(StTokenKind.TypedLiteral, text[start..i]));
                }
                else
                {
                    tokens.Add(new(StTokenKind.Identifier, text[start..i]));
                }
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '_')) i++;
                if (Peek(text, i) == '#')
                {
                    i++;   // número con base: 16#FF_FF
                    while (i < text.Length && (char.IsAsciiHexDigit(text[i]) || text[i] == '_')) i++;
                }
                else
                {
                    if (Peek(text, i) == '.' && char.IsAsciiDigit(Peek(text, i + 1)))
                    {
                        i++;
                        while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] == '_')) i++;
                    }

                    if (Peek(text, i) is 'e' or 'E' && (char.IsAsciiDigit(Peek(text, i + 1)) ||
                                                         (Peek(text, i + 1) is '+' or '-' && char.IsAsciiDigit(Peek(text, i + 2)))))
                    {
                        i += 2;
                        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                    }
                }

                tokens.Add(new(StTokenKind.Number, text[start..i]));
                continue;
            }

            var symbol = MultiCharSymbols.FirstOrDefault(s => string.CompareOrdinal(text, i, s, 0, s.Length) == 0);
            symbol ??= c.ToString();
            tokens.Add(new(StTokenKind.Symbol, symbol));
            i += symbol.Length;
        }

        return tokens;
    }

    public static string Join(IEnumerable<StToken> tokens)
    {
        var sb = new StringBuilder();
        foreach (var token in tokens) sb.Append(token.Text);
        return sb.ToString();
    }

    public static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    public static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static char Peek(string text, int index) => index < text.Length ? text[index] : '\0';
}
