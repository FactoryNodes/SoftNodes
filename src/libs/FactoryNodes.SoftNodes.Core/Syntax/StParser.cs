using System.Globalization;
using System.Text.RegularExpressions;
using FactoryNodes.Iec61131.Model;
using FactoryNodes.Iec61131.Syntax;

namespace FactoryNodes.SoftNodes.Syntax;

/// <summary>
/// Lee el cuerpo de una POU (líneas de texto que dejó <c>MasterLibraryReader</c>) a sentencias. Subconjunto ST de la
/// librería maestra: asignación, IF/ELSIF/ELSE, CASE, FOR, WHILE, REPEAT, EXIT, RETURN y llamadas. Lo que no reconoce
/// es un <see cref="StSyntaxException"/> con su línea, nunca se salta.
/// </summary>
public sealed class StParser
{
    private readonly List<(StToken Token, int Line)> _tokens = [];
    private readonly string _file;
    private int _pos;

    private StParser(IReadOnlyList<string> lines, IReadOnlyList<int> fileLines, string file)
    {
        _file = file;
        var text = string.Join("\n", lines);
        var line = 0;
        foreach (var token in StLexer.Tokenize(text))
        {
            var start = line;
            line += token.Text.Count(c => c == '\n');
            if (token.Kind == StTokenKind.NewLine && token.Text == "\r") line++;   // un '\r' suelto cuenta como salto
            if (token.IsTrivia) continue;
            _tokens.Add((token, fileLines.Count == 0 ? start + 1 : fileLines[Math.Min(start, fileLines.Count - 1)]));
        }
    }

    /// <summary>El cuerpo de una POU de la librería.</summary>
    public static IReadOnlyList<Stmt> ParseBody(StBody body, string file)
    {
        var lines = new List<string>();
        var numbers = new List<int>();
        foreach (var line in body.Lines)
        {
            lines.Add(line is CodeLine code ? code.Text : "");
            numbers.Add(line.Location.Line);
        }

        return new StParser(lines, numbers, file).ParseUntilEnd();
    }

    /// <summary>Un texto suelto (código de prueba o de usuario); las líneas se numeran desde 1.</summary>
    public static IReadOnlyList<Stmt> ParseText(string text, string file = "<texto>") =>
        new StParser(text.Replace("\r\n", "\n").Split('\n'), [], file).ParseUntilEnd();

    /// <summary>Una expresión suelta (valor inicial de una declaración).</summary>
    public static Expr ParseExpression(string text, string file = "<expresión>")
    {
        var parser = new StParser([text], [], file);
        var expr = parser.Expression();
        if (!parser.AtEnd) throw parser.Error("Sobra texto detrás de la expresión.");
        return expr;
    }

    private bool AtEnd => _pos >= _tokens.Count;
    private StToken Current => AtEnd ? default : _tokens[_pos].Token;
    private int CurrentLine => _tokens.Count == 0 ? 1 : _tokens[Math.Min(_pos, _tokens.Count - 1)].Line;

    private StSyntaxException Error(string message) => new(message, _file, CurrentLine);

    private bool IsKeyword(string word) =>
        !AtEnd && Current.Kind == StTokenKind.Identifier && Current.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private bool IsSymbol(string symbol) => !AtEnd && Current.Is(symbol);

    private bool AcceptKeyword(string word)
    {
        if (!IsKeyword(word)) return false;
        _pos++;
        return true;
    }

    private bool AcceptSymbol(string symbol)
    {
        if (!IsSymbol(symbol)) return false;
        _pos++;
        return true;
    }

    private void ExpectKeyword(string word)
    {
        if (!AcceptKeyword(word)) throw Error($"Se esperaba {word}.");
    }

    private void ExpectSymbol(string symbol)
    {
        if (!AcceptSymbol(symbol)) throw Error($"Se esperaba «{symbol}».");
    }

    private string ExpectIdentifier()
    {
        if (AtEnd || Current.Kind != StTokenKind.Identifier) throw Error("Se esperaba un nombre.");
        return _tokens[_pos++].Token.Text;
    }

    // ---------------------------------------------------------------- Sentencias

    private IReadOnlyList<Stmt> ParseUntilEnd()
    {
        var list = Block();
        if (!AtEnd) throw Error($"No se esperaba «{Current.Text}».");
        return list;
    }

    private static readonly string[] BlockEnds =
        ["END_IF", "ELSIF", "ELSE", "END_CASE", "END_FOR", "END_WHILE", "END_REPEAT", "UNTIL"];

    private bool AtBlockEnd()
    {
        if (AtEnd) return true;
        if (Current.Kind != StTokenKind.Identifier) return false;
        return BlockEnds.Any(IsKeyword);
    }

    /// <summary>Sentencias hasta un cierre (END_IF, ELSE…) o el final del texto.</summary>
    private List<Stmt> Block()
    {
        var list = new List<Stmt>();
        while (!AtBlockEnd())
        {
            if (AcceptSymbol(";")) continue;
            list.Add(Statement());
        }

        return list;
    }

    private Stmt Statement()
    {
        var line = CurrentLine;
        if (AcceptKeyword("IF")) return If(line);
        if (AcceptKeyword("CASE")) return Case(line);
        if (AcceptKeyword("FOR")) return For(line);
        if (AcceptKeyword("WHILE")) return While(line);
        if (AcceptKeyword("REPEAT")) return Repeat(line);
        if (AcceptKeyword("EXIT")) { AcceptSymbol(";"); return new ExitStmt(line); }
        if (AcceptKeyword("RETURN")) { AcceptSymbol(";"); return new ReturnStmt(line); }

        var path = Path();
        Stmt stmt;
        if (AcceptSymbol(":="))
        {
            stmt = new AssignStmt(path, Expression(), line);
        }
        else if (IsSymbol("("))
        {
            var args = Arguments();
            stmt = new FbCallStmt(path, args, line);
        }
        else
        {
            throw Error($"Se esperaba «:=» o una llamada, y hay «{(AtEnd ? "fin" : Current.Text)}».");
        }

        ExpectSymbol(";");
        return stmt;
    }

    private IfStmt If(int line)
    {
        var branches = new List<IfBranch>();
        var condition = Expression();
        ExpectKeyword("THEN");
        branches.Add(new IfBranch(condition, Block(), line));
        IReadOnlyList<Stmt>? other = null;
        while (true)
        {
            var branchLine = CurrentLine;
            if (AcceptKeyword("ELSIF"))
            {
                var c = Expression();
                ExpectKeyword("THEN");
                branches.Add(new IfBranch(c, Block(), branchLine));
            }
            else if (AcceptKeyword("ELSE"))
            {
                other = Block();
            }
            else
            {
                break;
            }
        }

        ExpectKeyword("END_IF");
        ExpectSymbol(";");
        return new IfStmt(branches, other, line);
    }

    private CaseStmt Case(int line)
    {
        var selector = Expression();
        ExpectKeyword("OF");
        var branches = new List<CaseBranch>();
        IReadOnlyList<Stmt>? other = null;
        while (!IsKeyword("END_CASE"))
        {
            if (AtEnd) throw Error("Falta END_CASE.");
            var branchLine = CurrentLine;
            if (AcceptKeyword("ELSE"))
            {
                other = Block();
                continue;
            }

            var labels = new List<CaseRange>();
            do
            {
                var from = Additive();
                Expr? to = null;
                if (IsSymbol(".") && _pos + 1 < _tokens.Count && _tokens[_pos + 1].Token.Is("."))
                {
                    _pos += 2;
                    to = Additive();
                }

                labels.Add(new CaseRange(from, to));
            }
            while (AcceptSymbol(","));

            ExpectSymbol(":");
            branches.Add(new CaseBranch(labels, CaseBody(), branchLine));
        }

        ExpectKeyword("END_CASE");
        ExpectSymbol(";");
        return new CaseStmt(selector, branches, other, line);
    }

    /// <summary>El cuerpo de una etiqueta de CASE: hasta la etiqueta siguiente (número, nombre y «:»), ELSE o END_CASE.</summary>
    private List<Stmt> CaseBody()
    {
        var list = new List<Stmt>();
        while (!AtBlockEnd() && !AtCaseLabel())
        {
            if (AcceptSymbol(";")) continue;
            list.Add(Statement());
        }

        return list;
    }

    /// <summary>¿Empieza aquí una etiqueta (<c>3:</c>, <c>1, 2:</c>, <c>1..5:</c>)? Un nombre solo (<c>x := …</c>) no lo es.</summary>
    private bool AtCaseLabel()
    {
        if (AtEnd || Current.Kind != StTokenKind.Number && !(Current.Is("-") && _pos + 1 < _tokens.Count)) return false;
        // número (con signo) seguido de «:» «,» o «..»
        var i = _pos;
        if (_tokens[i].Token.Is("-")) i++;
        if (i >= _tokens.Count || _tokens[i].Token.Kind != StTokenKind.Number) return false;
        i++;
        return i < _tokens.Count && (_tokens[i].Token.Is(":") || _tokens[i].Token.Is(",") || _tokens[i].Token.Is("."));
    }

    private ForStmt For(int line)
    {
        var variable = ExpectIdentifier();
        ExpectSymbol(":=");
        var from = Expression();
        ExpectKeyword("TO");
        var to = Expression();
        Expr? by = null;
        if (AcceptKeyword("BY")) by = Expression();
        ExpectKeyword("DO");
        var body = Block();
        ExpectKeyword("END_FOR");
        ExpectSymbol(";");
        return new ForStmt(variable, from, to, by, body, line);
    }

    private WhileStmt While(int line)
    {
        var condition = Expression();
        ExpectKeyword("DO");
        var body = Block();
        ExpectKeyword("END_WHILE");
        ExpectSymbol(";");
        return new WhileStmt(condition, body, line);
    }

    private RepeatStmt Repeat(int line)
    {
        var body = Block();
        ExpectKeyword("UNTIL");
        var until = Expression();
        ExpectKeyword("END_REPEAT");
        ExpectSymbol(";");
        return new RepeatStmt(body, until, line);
    }

    // ---------------------------------------------------------------- Expresiones

    private Expr Expression() => Or();

    private Expr Or()
    {
        var left = Xor();
        while (AcceptKeyword("OR")) left = new BinaryExpr("OR", left, Xor());
        return left;
    }

    private Expr Xor()
    {
        var left = And();
        while (AcceptKeyword("XOR")) left = new BinaryExpr("XOR", left, And());
        return left;
    }

    private Expr And()
    {
        var left = Equality();
        while (true)
        {
            if (AcceptKeyword("AND") || AcceptSymbol("&")) left = new BinaryExpr("AND", left, Equality());
            else return left;
        }
    }

    private Expr Equality()
    {
        var left = Relational();
        while (true)
        {
            if (AcceptSymbol("=")) left = new BinaryExpr("=", left, Relational());
            else if (AcceptSymbol("<>")) left = new BinaryExpr("<>", left, Relational());
            else return left;
        }
    }

    private Expr Relational()
    {
        var left = Additive();
        while (true)
        {
            if (AcceptSymbol("<=")) left = new BinaryExpr("<=", left, Additive());
            else if (AcceptSymbol(">=")) left = new BinaryExpr(">=", left, Additive());
            else if (AcceptSymbol("<")) left = new BinaryExpr("<", left, Additive());
            else if (AcceptSymbol(">")) left = new BinaryExpr(">", left, Additive());
            else return left;
        }
    }

    private Expr Additive()
    {
        var left = Multiplicative();
        while (true)
        {
            if (AcceptSymbol("+")) left = new BinaryExpr("+", left, Multiplicative());
            else if (AcceptSymbol("-")) left = new BinaryExpr("-", left, Multiplicative());
            else return left;
        }
    }

    private Expr Multiplicative()
    {
        var left = Power();
        while (true)
        {
            if (AcceptSymbol("*")) left = new BinaryExpr("*", left, Power());
            else if (AcceptSymbol("/")) left = new BinaryExpr("/", left, Power());
            else if (AcceptKeyword("MOD")) left = new BinaryExpr("MOD", left, Power());
            else return left;
        }
    }

    private Expr Power()
    {
        var left = Unary();
        while (AcceptSymbol("**")) left = new BinaryExpr("**", left, Unary());
        return left;
    }

    private Expr Unary()
    {
        if (AcceptKeyword("NOT")) return new UnaryExpr("NOT", Unary());
        if (AcceptSymbol("-")) return new UnaryExpr("-", Unary());
        if (AcceptSymbol("+")) return Unary();
        return Primary();
    }

    private Expr Primary()
    {
        if (AtEnd) throw Error("Falta una expresión.");
        var token = Current;
        switch (token.Kind)
        {
            case StTokenKind.Number:
                _pos++;
                return new LiteralExpr(NumberValue(token.Text));
            case StTokenKind.TypedLiteral:
                _pos++;
                return new LiteralExpr(TypedValue(token.Text));
            case StTokenKind.String:
                _pos++;
                return new LiteralExpr(token.Text[1..^1]);
            case StTokenKind.Identifier:
                if (token.Text.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) { _pos++; return new LiteralExpr(true); }
                if (token.Text.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) { _pos++; return new LiteralExpr(false); }
                var path = Path();
                if (IsSymbol("(") && path.Parts.Count == 1 && path.Parts[0].Index is null)
                    return new CallExpr(path.Parts[0].Name, Arguments());
                return path;
        }

        if (AcceptSymbol("("))
        {
            var inner = Expression();
            ExpectSymbol(")");
            return inner;
        }

        throw Error($"No se esperaba «{token.Text}».");
    }

    private PathExpr Path()
    {
        var parts = new List<PathPart>();
        do
        {
            var name = ExpectIdentifier();
            Expr? index = null;
            if (AcceptSymbol("["))
            {
                index = Expression();
                ExpectSymbol("]");
            }

            parts.Add(new PathPart(name, index));
        }
        while (IsSymbol(".") && !(_pos + 1 < _tokens.Count && _tokens[_pos + 1].Token.Is(".")) && AcceptSymbol("."));

        return new PathExpr(parts);
    }

    private List<CallArg> Arguments()
    {
        ExpectSymbol("(");
        var args = new List<CallArg>();
        if (AcceptSymbol(")")) return args;
        do
        {
            if (!AtEnd && Current.Kind == StTokenKind.Identifier && _pos + 1 < _tokens.Count &&
                (_tokens[_pos + 1].Token.Is(":=") || _tokens[_pos + 1].Token.Is("=>")))
            {
                var name = ExpectIdentifier();
                var output = Current.Is("=>");
                _pos++;
                args.Add(new CallArg(name, output ? Path() : Expression(), output));
            }
            else
            {
                args.Add(new CallArg(null, Expression()));
            }
        }
        while (AcceptSymbol(","));

        ExpectSymbol(")");
        return args;
    }

    // ---------------------------------------------------------------- Literales

    private object NumberValue(string text)
    {
        text = text.Replace("_", "");
        var hash = text.IndexOf('#');
        if (hash > 0)
        {
            var radix = int.Parse(text[..hash], CultureInfo.InvariantCulture);
            return Convert.ToInt64(text[(hash + 1)..], radix);
        }

        if (text.Contains('.') || text.Contains('e') || text.Contains('E'))
            return double.Parse(text, CultureInfo.InvariantCulture);
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
        throw Error($"Número «{text}» no válido.");
    }

    private static readonly Regex TimePart = new(@"(?<n>\d+(?:\.\d+)?)(?<u>ms|d|h|m|s)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Literal con tipo (<c>WORD#16#0001</c>, <c>T#600ms</c>, <c>UDINT#3600</c>): el valor, el TIME en ms.</summary>
    private object TypedValue(string text)
    {
        var hash = text.IndexOf('#');
        var type = text[..hash].ToUpperInvariant();
        var rest = text[(hash + 1)..].Replace("_", "");
        if (type is "T" or "TIME")
        {
            var matches = TimePart.Matches(rest);
            if (matches.Count == 0 || string.Concat(matches.Select(m => m.Value)).Length != rest.Length)
                throw Error($"Tiempo «{text}» no válido.");
            double ms = 0;
            foreach (Match m in matches)
            {
                var n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                ms += m.Groups["u"].Value.ToLowerInvariant() switch
                {
                    "ms" => n,
                    "s" => n * 1000,
                    "m" => n * 60_000,
                    "h" => n * 3_600_000,
                    _ => n * 86_400_000
                };
            }

            return (long)Math.Round(ms);
        }

        if (type == "BOOL") return rest is "1" || rest.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
        if (type is "REAL" or "LREAL") return double.Parse(rest, CultureInfo.InvariantCulture);
        return NumberValue(rest);
    }
}
