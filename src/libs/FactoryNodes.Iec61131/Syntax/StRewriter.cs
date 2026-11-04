using FactoryNodes.Iec61131.Model;

namespace FactoryNodes.Iec61131.Syntax;

/// <summary>
/// Reescrituras de código ST sobre los tokens de <see cref="StLexer"/> (nunca con expresiones regulares sobre el texto):
/// solo tocan lo que es código, no comentarios ni cadenas, y conservan el resto carácter a carácter. Son las piezas
/// con las que cada emisor adapta el cuerpo de la librería maestra a su plataforma.
/// </summary>
public static class StRewriter
{
    /// <summary>
    /// Sustituye cada llamada <c>name(args)</c> por lo que devuelva <paramref name="replace"/> a partir del texto de los
    /// argumentos. No toca un miembro (<c>x.name(</c>) ni un nombre entre comillas (<c>"name"(</c>, ya convertido).
    /// Como el prototipo, vuelve a buscar desde el principio tras cada cambio, así que también convierte llamadas
    /// anidadas en los argumentos.
    /// </summary>
    public static string ReplaceCalls(string code, string name, Func<string, string> replace)
    {
        for (var guard = 0; guard < 10_000; guard++)
        {
            var tokens = StLexer.Tokenize(code);
            var call = FindCall(tokens, name);
            if (call is null) return code;

            var (start, open, close) = call.Value;
            var args = StLexer.Join(tokens.Skip(open + 1).Take(close - open - 1));
            code = StLexer.Join(tokens.Take(start)) + replace(args) + StLexer.Join(tokens.Skip(close + 1));
        }

        throw new InvalidOperationException($"La sustitución de {name}(…) no termina: el reemplazo vuelve a contener la llamada.");
    }

    /// <summary>Índices del nombre, del '(' y de su ')' en la primera llamada a <paramref name="name"/>.</summary>
    private static (int Start, int Open, int Close)? FindCall(List<StToken> tokens, string name)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != StTokenKind.Identifier || tokens[i].Text != name) continue;
            if (i > 0 && tokens[i - 1].Is(".")) continue;

            var open = i + 1;
            while (open < tokens.Count && tokens[open].Kind == StTokenKind.Whitespace) open++;
            if (open >= tokens.Count || !tokens[open].Is("(")) continue;

            var depth = 0;
            for (var k = open; k < tokens.Count; k++)
            {
                if (tokens[k].Is("(")) depth++;
                else if (tokens[k].Is(")") && --depth == 0) return (i, open, k);
            }
        }

        return null;
    }

    /// <summary>
    /// Cambia el nombre de una función cuando se llama (<c>REAL_TO_LREAL(x)</c> → <c>TO_LREAL(x)</c>), sin tocar los
    /// argumentos.
    /// </summary>
    public static string RenameCalls(string code, string name, string newName) =>
        ReplaceCalls(code, name, args => $"{newName}({args})");

    /// <summary>
    /// Antepone <paramref name="prefix"/> (el <c>#</c> de TIA) a cada identificador que sea una variable local de la POU,
    /// salvo: miembros tras un punto (<c>Data.Status</c> → solo <c>#Data</c>) y parámetros formales dentro de una llamada
    /// (<c>tStart(IN := …)</c>: <c>IN</c> no). Los literales con tipo, los comentarios y las cadenas no se tocan.
    /// La profundidad de paréntesis se sigue a lo largo de todo el texto, así que una llamada partida en varias líneas
    /// se trata bien.
    /// </summary>
    public static string PrefixLocals(string code, IReadOnlySet<string> locals, string prefix)
    {
        var tokens = StLexer.Tokenize(code);
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Is("(")) depth++;
            else if (token.Is(")")) depth--;
            if (token.Kind != StTokenKind.Identifier || !locals.Contains(token.Text)) continue;
            if (i > 0 && tokens[i - 1].Is(".")) continue;
            if (depth > 0 && NextOnLineIs(tokens, i, ":=")) continue;   // parámetro formal
            tokens[i] = token with { Text = prefix + token.Text };
        }

        return StLexer.Join(tokens);
    }

    /// <summary>
    /// Cambia cada nombre suelto por lo que devuelva <paramref name="replace"/> (null = se deja): el código de una sección de
    /// usuario, donde TIA escribe <c>"hw_P".M101</c> por <c>M101</c>. Como <see cref="PrefixLocals"/>, no toca miembros tras un
    /// punto ni parámetros formales de una llamada, ni comentarios, cadenas o literales con tipo.
    /// </summary>
    public static string ReplaceNames(string code, Func<string, string?> replace)
    {
        var tokens = StLexer.Tokenize(code);
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Is("(")) depth++;
            else if (token.Is(")")) depth--;
            if (token.Kind != StTokenKind.Identifier) continue;
            if (i > 0 && tokens[i - 1].Is(".")) continue;
            if (depth > 0 && (NextOnLineIs(tokens, i, ":=") || NextOnLineIs(tokens, i, "=>"))) continue;   // parámetro formal
            if (replace(token.Text) is { } text) tokens[i] = token with { Text = text };
        }

        return StLexer.Join(tokens);
    }

    /// <summary>
    /// Cambia cada miembro <c>padre.miembro</c> por lo que devuelva <paramref name="replace"/>(padre, miembro) (null = se deja): el código de
    /// usuario de un loop, que nombra sus variables como <c>Data.Extern.Peso</c> o <c>Data.User.Kg</c>. Comentarios y cadenas no se tocan.
    /// </summary>
    public static string ReplaceMembers(string code, Func<string, string, string?> replace)
    {
        var tokens = StLexer.Tokenize(code);
        for (var i = 2; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != StTokenKind.Identifier || !tokens[i - 1].Is(".") || tokens[i - 2].Kind != StTokenKind.Identifier) continue;
            if (replace(tokens[i - 2].Text, tokens[i].Text) is { } text) tokens[i] = tokens[i] with { Text = text };
        }

        return StLexer.Join(tokens);
    }

    /// <summary>Los nombres sueltos del código (sin miembros tras un punto ni parámetros formales), en orden y sin repetir.</summary>
    public static IReadOnlyList<string> Names(string code)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReplaceNames(code, name =>
        {
            if (seen.Add(name)) names.Add(name);
            return null;
        });
        return names;
    }

    /// <summary>
    /// Inserta <paramref name="firstArgument"/> como primer argumento en cada llamada a una instancia de
    /// <paramref name="instances"/>: <c>alarms(</c> → <c>alarms(EN_IN := TRUE,⏎</c> + <paramref name="continuation"/>
    /// delante del primer argumento original, que pasa a la línea siguiente.
    /// </summary>
    public static string InsertFirstArgument(string code, IReadOnlySet<string> instances, string firstArgument, string continuation)
    {
        var tokens = StLexer.Tokenize(code);
        for (var i = tokens.Count - 2; i >= 0; i--)
        {
            if (tokens[i].Kind != StTokenKind.Identifier || !instances.Contains(tokens[i].Text)) continue;
            if (i > 0 && tokens[i - 1].Is(".")) continue;
            if (!tokens[i + 1].Is("(")) continue;
            tokens.Insert(i + 2, new StToken(StTokenKind.Symbol, $"{firstArgument},\n{continuation}"));
        }

        return StLexer.Join(tokens);
    }

    /// <summary>
    /// Cambia cada constante de tamaño (<c>/// SIZE</c>) por su número: <c>FOR i := 1 TO TOPO_NODES DO</c> →
    /// <c>FOR i := 1 TO 38 DO</c>. Solo identificadores sueltos (no miembros tras un punto); comentarios y cadenas no se tocan.
    /// </summary>
    public static string ReplaceSizes(string code, MasterLibrary library)
    {
        var sizes = library.Sizes.ToDictionary(s => s.Name, s => s.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparer.OrdinalIgnoreCase);
        if (sizes.Count == 0) return code;

        var tokens = StLexer.Tokenize(code);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != StTokenKind.Identifier || !sizes.TryGetValue(tokens[i].Text, out var value)) continue;
            if (i > 0 && tokens[i - 1].Is(".")) continue;
            tokens[i] = tokens[i] with { Kind = StTokenKind.Number, Text = value };
        }

        return StLexer.Join(tokens);
    }

    /// <summary>
    /// Cambia el principio de los literales con tipo que empiezan por <paramref name="from"/> (TIA:
    /// <c>WORD#16#0001</c> → <c>16#0001</c> con from "WORD#16#" y to "16#").
    /// </summary>
    public static string ReplaceLiteralPrefix(string code, string from, string to)
    {
        var tokens = StLexer.Tokenize(code);
        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i].Kind == StTokenKind.TypedLiteral && tokens[i].Text.StartsWith(from, StringComparison.Ordinal))
                tokens[i] = tokens[i] with { Text = to + tokens[i].Text[from.Length..] };
        return StLexer.Join(tokens);
    }

    private static bool NextOnLineIs(List<StToken> tokens, int index, string symbol)
    {
        for (var k = index + 1; k < tokens.Count; k++)
        {
            if (tokens[k].Kind == StTokenKind.Whitespace) continue;
            return tokens[k].Is(symbol);
        }

        return false;
    }
}
