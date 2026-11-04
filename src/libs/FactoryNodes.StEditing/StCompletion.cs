using FactoryNodes.Iec61131.Model;
using FactoryNodes.Iec61131.Syntax;
using FactoryNodes.StEditing.Semantics;
using FactoryNodes.StEditing.Syntax;

namespace FactoryNodes.StEditing;

public enum StCompletionKind
{
    Variable,
    Parameter,
    Constant,
    Member,
    Function,
    Keyword
}

/// <summary>Una propuesta: lo que se ve (<see cref="Label"/>), lo que se escribe y la ayuda (tipo y comentario).</summary>
public sealed record StCompletionItem(string Label, StCompletionKind Kind, string InsertText, string Detail);

/// <summary>Propuestas para sustituir el texto de <see cref="Start"/> a <see cref="Start"/> + <see cref="Length"/> (lo ya escrito del nombre).</summary>
public sealed record StCompletionList(int Start, int Length, IReadOnlyList<StCompletionItem> Items)
{
    public static readonly StCompletionList Empty = new(0, 0, []);
}

/// <summary>
/// La ayuda de una llamada: <paramref name="Header"/> («FUNCTION_BLOCK TON», «FUNCTION LIMIT_REAL : REAL»), sus parámetros y cuál
/// se está escribiendo (<paramref name="Current"/>, índice en <paramref name="Parameters"/>; -1 si no se sabe).
/// </summary>
public sealed record StSignature(string Name, string Header, IReadOnlyList<StParameter> Parameters, int Current, string? Description);

/// <summary>
/// Autocompletado del cuerpo: tras un punto, los miembros de lo que hay delante (estructura, bloque o array); dentro de
/// la llamada a un bloque o a una función, sus parámetros («IN := »); en otro sitio, las variables, constantes y
/// funciones visibles y las palabras clave. Todo sale de la librería maestra (<see cref="StScope"/>).
/// </summary>
public static class StCompletion
{
    /// <summary>¿Abre el autocompletado al escribir este carácter? (el punto, y la primera letra de un nombre lo abre el editor con Ctrl+Espacio).</summary>
    public static bool OpensOn(char typed) => typed == '.';

    private static readonly string[] StatementKeywords =
        ["IF", "THEN", "ELSIF", "ELSE", "END_IF", "CASE", "OF", "END_CASE", "FOR", "TO", "BY", "DO", "END_FOR", "WHILE", "END_WHILE",
         "REPEAT", "UNTIL", "END_REPEAT", "EXIT", "RETURN", "AND", "OR", "XOR", "NOT", "MOD", "TRUE", "FALSE"];

    public static StCompletionList Complete(StScope scope, string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret;
        while (start > 0 && StLexer.IsIdentifierPart(text[start - 1])) start--;
        if (start < caret && char.IsDigit(text[start]) && (start == 0 || text[start - 1] != '.')) return StCompletionList.Empty;

        var tokens = PToken.From(text[..start]).Where(t => t.Kind is not (StTokenKind.Whitespace or StTokenKind.NewLine)).ToList();
        if (tokens.Count > 0 && tokens[^1].Kind is StTokenKind.LineComment or StTokenKind.BlockComment or StTokenKind.String)
            return StCompletionList.Empty;   // dentro de un comentario o una cadena

        tokens = tokens.Where(t => t.Kind is not (StTokenKind.LineComment or StTokenKind.BlockComment)).ToList();
        if (tokens.Count > 0 && tokens[^1].Is("."))
            return new StCompletionList(start, caret - start, Members(scope, tokens[..^1]));

        var items = new List<StCompletionItem>();
        if (ArgumentPosition(tokens) is { } callee) items.AddRange(Parameters(scope, callee));
        items.AddRange(scope.Locals.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => new StCompletionItem(s.Name,
            s.Kind switch
            {
                StSymbolKind.Input or StSymbolKind.Output or StSymbolKind.InOut => StCompletionKind.Parameter,
                StSymbolKind.Constant => StCompletionKind.Constant,
                _ => StCompletionKind.Variable
            }, s.Name, Detail(s.TypeText, s.Comment, s.Kind))));
        items.AddRange(scope.Library.Sizes.Select(s => new StCompletionItem(s.Name, StCompletionKind.Constant, s.Name, $"Constante de tamaño = {s.Value}")));
        items.AddRange(scope.Library.Pous.Where(p => p.Kind == PouKind.Function)
            .Select(p => new StCompletionItem(p.Name, StCompletionKind.Function, p.Name,
                $"FUNCTION : {p.ReturnType} ({string.Join(", ", p.VariablesOf(VarSectionKind.Input).Select(v => $"{v.Name} : {v.TypeName}"))})")));
        items.AddRange(StandardFunctions.All.Select(f => new StCompletionItem(f.Name, StCompletionKind.Function, f.Name, f.Description)));

        var typed = text[start..caret];
        if (typed.Contains('_'))   // REAL_TO_…: las conversiones solo cuando se empieza a escribir una
            items.AddRange(StandardFunctions.ConvertibleTypes.SelectMany(from => StandardFunctions.ConvertibleTypes.Where(to => to != from)
                    .Select(to => $"{from}_TO_{to}"))
                .Where(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                .Select(n => new StCompletionItem(n, StCompletionKind.Function, n, "Conversión de tipo")));

        items.AddRange(StatementKeywords.Select(k => new StCompletionItem(k, StCompletionKind.Keyword, k, "Palabra clave")));
        return new StCompletionList(start, caret - start, items.DistinctBy(i => i.Label, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static string Detail(string type, string? comment, StSymbolKind? kind = null)
    {
        var where = kind switch
        {
            StSymbolKind.Input => "VAR_INPUT · ",
            StSymbolKind.Output => "VAR_OUTPUT · ",
            StSymbolKind.InOut => "VAR_IN_OUT · ",
            StSymbolKind.Constant => "VAR CONSTANT · ",
            StSymbolKind.ReturnValue => "valor de la función · ",
            _ => ""
        };
        return string.IsNullOrWhiteSpace(comment) ? where + type : $"{where}{type} — {comment}";
    }

    /// <summary>Miembros de la expresión que acaba en <paramref name="before"/> (lo que hay justo antes del punto).</summary>
    private static IReadOnlyList<StCompletionItem> Members(StScope scope, IReadOnlyList<PToken> before)
    {
        var start = DesignatorStart(before);
        if (start < 0) return [];
        var source = string.Concat(before.Skip(start).Select(t => t.Text));
        var type = TypeOfDesignator(scope, source);
        return scope.MembersOf(type)
            .Select(m => new StCompletionItem(m.Name, StCompletionKind.Member, m.Name, Detail(m.TypeText, m.Comment) +
                (m.Direction == VarSectionKind.Output ? " (salida)" : m.Direction == VarSectionKind.Input ? " (entrada)" : "")))
            .ToList();
    }

    /// <summary>Índice del primer token de <c>a.b[i].c</c> que acaba al final de la lista, o -1.</summary>
    private static int DesignatorStart(IReadOnlyList<PToken> t)
    {
        var k = t.Count - 1;
        while (k >= 0)
        {
            if (t[k].Is("]"))
            {
                var depth = 0;
                for (; k >= 0; k--)
                {
                    if (t[k].Is("]")) depth++;
                    else if (t[k].Is("[") && --depth == 0) break;
                }

                k--;
                continue;
            }

            if (t[k].Kind is not (StTokenKind.Identifier or StTokenKind.Number)) return -1;
            if (k > 0 && t[k - 1].Is("."))
            {
                k -= 2;
                continue;
            }

            return t[k].Kind == StTokenKind.Identifier ? k : -1;
        }

        return -1;
    }

    /// <summary>Tipo de <c>a.b[i].c</c> leyéndolo como una asignación de prueba (sin errores a la vista).</summary>
    private static StType TypeOfDesignator(StScope scope, string designator)
    {
        var syntax = StBodyParser.Parse(designator + " := 0;");
        if (syntax.Statements.FirstOrDefault() is not AssignStatement assign) return StType.Unknown;
        return Resolve(scope, assign.Target);
    }

    private static StType Resolve(StScope scope, StExpr expr) => expr switch
    {
        NameExpr n => scope.Lookup(n.Name.Text)?.Type ?? StType.Unknown,
        MemberExpr m => scope.MembersOf(Resolve(scope, m.Target))
            .FirstOrDefault(x => x.Name.Equals(m.Member.Text, StringComparison.OrdinalIgnoreCase))?.Type ?? StType.Unknown,
        IndexExpr i => Resolve(scope, i.Target) is { Kind: StTypeKind.Array, Element: { } element } ? element : StType.Unknown,
        _ => StType.Unknown
    };

    /// <summary>Si el cursor está donde va el nombre de un argumento («tStart(» o «, »), lo que se llama.</summary>
    private static StExpr? ArgumentPosition(IReadOnlyList<PToken> t)
    {
        if (t.Count == 0 || !(t[^1].Is("(") || t[^1].Is(","))) return null;
        var depth = 0;
        for (var k = t.Count - 1; k >= 0; k--)
        {
            if (t[k].Is(")")) depth++;
            else if (t[k].Is("("))
            {
                if (depth-- > 0) continue;
                var start = DesignatorStart(t.Take(k).ToList());
                if (start < 0) return null;
                var syntax = StBodyParser.Parse(string.Concat(t.Skip(start).Take(k - start).Select(x => x.Text)) + " := 0;");
                return syntax.Statements.FirstOrDefault() is AssignStatement a ? a.Target : null;
            }
        }

        return null;
    }

    /// <summary>
    /// La ayuda de la llamada en la que está el cursor (<c>tStart(IN := x, |</c>): el bloque o la función, sus parámetros y cuál se
    /// escribe (el que se nombra antes de <c>:=</c>/<c>=&gt;</c>, o por su posición). Null fuera de una llamada a algo conocido.
    /// </summary>
    public static StSignature? Signature(StScope scope, string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var all = PToken.From(text[..caret]).Where(t => t.Kind is not (StTokenKind.Whitespace or StTokenKind.NewLine)).ToList();
        if (all.Count > 0 && all[^1].Kind is StTokenKind.LineComment or StTokenKind.BlockComment or StTokenKind.String) return null;
        var t = all.Where(x => x.Kind is not (StTokenKind.LineComment or StTokenKind.BlockComment)).ToList();

        var depth = 0;
        for (var k = t.Count - 1; k >= 0; k--)
        {
            if (t[k].Is(")")) depth++;
            else if (t[k].Is(";") && depth == 0) return null;   // la sentencia ya acabó
            else if (t[k].Is("("))
            {
                if (depth-- > 0) continue;
                var start = DesignatorStart(t.Take(k).ToList());
                if (start < 0) return null;
                var syntax = StBodyParser.Parse(string.Concat(t.Skip(start).Take(k - start).Select(x => x.Text)) + " := 0;");
                if (syntax.Statements.FirstOrDefault() is not AssignStatement a || Owner(scope, a.Target) is not { } owner) return null;
                var parameters = scope.ParametersOf(owner.Name);
                return new StSignature(owner.Name, owner.Header, parameters, CurrentParameter(t.Skip(k + 1).ToList(), parameters), owner.Description);
            }
        }

        return null;
    }

    /// <summary>El parámetro que se escribe: el del trozo tras la última coma (nombrado, o por su posición).</summary>
    private static int CurrentParameter(IReadOnlyList<PToken> inside, IReadOnlyList<StParameter> parameters)
    {
        var segments = new List<List<PToken>> { new() };
        var depth = 0;
        foreach (var token in inside)
        {
            if (token.Is("(") || token.Is("[")) depth++;
            else if (token.Is(")") || token.Is("]")) depth--;
            if (token.Is(",") && depth == 0) segments.Add([]);
            else segments[^1].Add(token);
        }

        var last = segments[^1];
        if (last.Count >= 2 && last[0].Kind == StTokenKind.Identifier && (last[1].Is(":=") || last[1].Is("=>")))
            return parameters.ToList().FindIndex(p => p.Name.Equals(last[0].Text, StringComparison.OrdinalIgnoreCase));
        if (last.Count == 1 && last[0].Kind == StTokenKind.Identifier)
        {
            // escribiendo el nombre del parámetro: el que empieza así
            var named = parameters.ToList().FindIndex(p => p.Name.StartsWith(last[0].Text, StringComparison.OrdinalIgnoreCase));
            if (named >= 0) return named;
        }

        var position = segments.Count - 1;
        return position < parameters.Count ? position : -1;
    }

    private static (string Name, string Header, string? Description)? Owner(StScope scope, StExpr callee)
    {
        if (callee is NameExpr n && scope.Lookup(n.Name.Text) is { } symbol)
            switch (symbol.Kind)
            {
                case StSymbolKind.Function when scope.Library.Pou(symbol.Name) is { } function:
                    return (function.Name, $"FUNCTION {function.Name} : {function.ReturnType}", null);
                case StSymbolKind.StandardFunction:
                    return (symbol.Name, "FUNCTION " + symbol.Name, symbol.Comment);
            }

        if (Resolve(scope, callee) is { Kind: StTypeKind.FunctionBlock } fb)
            return (fb.Name, "FUNCTION_BLOCK " + fb.Name, StandardBlocks.Get(fb.Name)?.Description);
        return null;
    }

    /// <summary>
    /// A qué bloque o tipo de la librería lleva el nombre de la posición <paramref name="offset"/> (Ctrl+clic): el propio nombre si es
    /// un bloque, una función o un TYPE, o el tipo de lo nombrado si es una instancia de un bloque o una estructura de la librería
    /// (<c>Data</c> → <c>UDT_Motor</c>, <c>modes</c> → <c>ModeManager</c>). Null si no lleva a nada de la librería.
    /// </summary>
    public static string? DefinitionAt(StScope scope, string text, int offset)
    {
        var all = PToken.From(text);
        var index = -1;
        for (var i = 0; i < all.Count; i++)
            if (all[i].Kind == StTokenKind.Identifier && offset >= all[i].Offset && offset <= all[i].End) index = i;
        if (index < 0) return null;
        var word = all[index].Text;
        if (scope.Library.Pou(word) is { } pou) return pou.Name;
        if (scope.Library.Type(word) is { } type) return type.Name;

        var before = all.Take(index + 1).Where(t => t.Kind is not (StTokenKind.Whitespace or StTokenKind.NewLine or StTokenKind.LineComment or StTokenKind.BlockComment)).ToList();
        var start = DesignatorStart(before);
        if (start < 0) return null;
        var resolved = TypeOfDesignator(scope, string.Concat(before.Skip(start).Select(t => t.Text)));
        if (resolved is { Kind: StTypeKind.Array, Element: { } element }) resolved = element;
        return resolved.Kind switch
        {
            StTypeKind.FunctionBlock when scope.Library.Pou(resolved.Name) is { } block => block.Name,
            StTypeKind.Struct when scope.Library.Type(resolved.Name) is { } structure => structure.Name,
            _ => null
        };
    }

    private static IEnumerable<StCompletionItem> Parameters(StScope scope, StExpr callee)
    {
        string? owner = null;
        if (callee is NameExpr n && scope.Lookup(n.Name.Text) is { Kind: StSymbolKind.Function } f) owner = f.Name;
        else if (Resolve(scope, callee) is { Kind: StTypeKind.FunctionBlock } fb) owner = fb.Name;
        if (owner is null) return [];
        return scope.ParametersOf(owner).Select(p => new StCompletionItem(p.Name, StCompletionKind.Parameter,
            p.Name + (p.Direction == VarSectionKind.Output ? " => " : " := "),
            Detail(p.TypeName, p.Comment) + p.Direction switch
            {
                VarSectionKind.Output => " (salida)",
                VarSectionKind.InOut => " (VAR_IN_OUT, obligatorio)",
                _ => " (entrada)"
            }));
    }
}
