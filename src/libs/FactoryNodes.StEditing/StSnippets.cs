using FactoryNodes.Iec61131.Syntax;
using FactoryNodes.StEditing.Syntax;

namespace FactoryNodes.StEditing;

/// <summary>Un trozo de la plantilla: texto fijo, un hueco que se rellena (con su texto de ejemplo) o dónde queda el cursor.</summary>
public abstract record StSnippetPart;

/// <summary>Texto fijo; <c>\n</c> es un salto de línea y <c>\t</c> un nivel de sangría (el editor pone los suyos).</summary>
public sealed record StSnippetText(string Text) : StSnippetPart;

/// <summary>Un hueco: se escribe encima de <see cref="Example"/> y Tab pasa al siguiente.</summary>
public sealed record StSnippetField(string Example) : StSnippetPart;

/// <summary>Dónde queda el cursor al terminar.</summary>
public sealed record StSnippetCaret : StSnippetPart;

/// <summary>Una plantilla: se escribe <see cref="Trigger"/> y Tab la despliega.</summary>
public sealed record StSnippet(string Trigger, string Description, IReadOnlyList<StSnippetPart> Parts);

/// <summary>
/// Las plantillas de los editores de código (autor, 2026-10-21, PENDIENTES 90): <c>if</c> + Tab escribe <c>IF … THEN … END_IF;</c>
/// con el cursor en la condición. Los huecos se escriben en la plantilla entre llaves (<c>{condition}</c>) y el cursor final es <c>{|}</c>.
/// </summary>
public static class StSnippets
{
    public static IReadOnlyList<StSnippet> All { get; } =
    [
        Make("if", "IF … THEN … END_IF", "IF {condition} THEN\n\t{|}\nEND_IF;"),
        Make("ifelse", "IF … THEN … ELSE … END_IF", "IF {condition} THEN\n\t{|}\nELSE\n\t\nEND_IF;"),
        Make("elsif", "ELSIF … THEN", "ELSIF {condition} THEN\n\t{|}"),
        Make("case", "CASE … OF … END_CASE", "CASE {value} OF\n\t{1}:\n\t\t{|}\nEND_CASE;"),
        Make("for", "FOR … TO … DO … END_FOR", "FOR {i} := {1} TO {10} DO\n\t{|}\nEND_FOR;"),
        Make("while", "WHILE … DO … END_WHILE", "WHILE {condition} DO\n\t{|}\nEND_WHILE;"),
        Make("repeat", "REPEAT … UNTIL … END_REPEAT", "REPEAT\n\t{|}\nUNTIL {condition}\nEND_REPEAT;"),
        Make("ton", "Temporizador TON", "{tDelay}(IN := {condition}, PT := {T#1s});{|}"),
        Make("tof", "Temporizador TOF", "{tDelay}(IN := {condition}, PT := {T#1s});{|}"),
        Make("rtrig", "Flanco de subida R_TRIG", "{rEdge}(CLK := {signal});{|}"),
        Make("ftrig", "Flanco de bajada F_TRIG", "{fEdge}(CLK := {signal});{|}"),
        Make("region", "/// REGION … /// END_REGION", "/// REGION {Title}\n{|}\n/// END_REGION")
    ];

    /// <summary>
    /// La plantilla cuyo disparador acaba justo antes del cursor (<c>if|</c>), con dónde empieza; null si no hay, si el nombre sigue
    /// (<c>ifx</c>) o si el cursor está en un comentario o una cadena.
    /// </summary>
    public static (StSnippet Snippet, int Start)? At(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret;
        while (start > 0 && StLexer.IsIdentifierPart(text[start - 1])) start--;
        if (start == caret || (start > 0 && text[start - 1] is '.' or '#')) return null;
        var word = text[start..caret];
        if (All.FirstOrDefault(s => s.Trigger.Equals(word, StringComparison.OrdinalIgnoreCase)) is not { } snippet) return null;
        var last = PToken.From(text[..caret]).LastOrDefault(t => t.Kind is not (StTokenKind.Whitespace or StTokenKind.NewLine));
        if (last.Kind is StTokenKind.LineComment or StTokenKind.BlockComment or StTokenKind.String) return null;
        return (snippet, start);
    }

    private static StSnippet Make(string trigger, string description, string template)
    {
        var parts = new List<StSnippetPart>();
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0)
            {
                parts.Add(new StSnippetText(template[i..]));
                break;
            }

            if (open > i) parts.Add(new StSnippetText(template[i..open]));
            var close = template.IndexOf('}', open);
            var inside = template[(open + 1)..close];
            parts.Add(inside == "|" ? new StSnippetCaret() : new StSnippetField(inside));
            i = close + 1;
        }

        return new StSnippet(trigger, description, parts);
    }
}
