using FactoryNodes.Iec61131.Syntax;

namespace FactoryNodes.StEditing.Syntax;

/// <summary>
/// Lee el cuerpo de un FUNCTION / FUNCTION_BLOCK de la librería maestra (solo el código, sin declaraciones) a sentencias
/// y expresiones, con recuperación: un error no impide leer lo que viene detrás (se salta hasta el siguiente <c>;</c> o
/// la siguiente palabra clave). Usa los tokens de <see cref="StLexer"/>, el mismo que los emisores. Códigos <c>ST00x</c>.
/// </summary>
public static class StBodyParser
{
    /// <summary>Palabras clave del cuerpo (sin distinguir mayúsculas).</summary>
    public static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "IF", "THEN", "ELSIF", "ELSE", "END_IF", "CASE", "OF", "END_CASE", "FOR", "TO", "BY", "DO", "END_FOR", "WHILE",
        "END_WHILE", "REPEAT", "UNTIL", "END_REPEAT", "EXIT", "RETURN", "CONTINUE", "AND", "OR", "XOR", "NOT", "MOD",
        "TRUE", "FALSE"
    };

    public static StBodySyntax Parse(string text) => new Parser(text).Run();

    private sealed class Parser
    {
        private readonly string _text;
        private readonly IReadOnlyList<PToken> _all;
        private readonly List<PToken> _t;
        private readonly List<StDiagnostic> _errors = [];
        private readonly HashSet<int> _reported = [];
        private readonly LineMap _lines;
        private int _p;

        public Parser(string text)
        {
            _text = text;
            _all = PToken.From(text);
            _t = _all.Where(t => t.Kind is not (StTokenKind.Whitespace or StTokenKind.NewLine or StTokenKind.LineComment or StTokenKind.BlockComment)).ToList();
            _lines = new LineMap(text);
        }

        public StBodySyntax Run()
        {
            foreach (var comment in _all.Where(t => t.Kind == StTokenKind.BlockComment && !t.Text.EndsWith("*)", StringComparison.Ordinal)))
                Error(comment, "ST001", "Comentario «(*» sin cerrar.");
            var statements = Statements(() => false);
            return new StBodySyntax(_text, _all, statements, _errors);
        }

        // ------------------------------------------------------------ utilidades

        private bool AtEnd => _p >= _t.Count;

        private PToken Cur => _p < _t.Count ? _t[_p] : new PToken(StTokenKind.Symbol, "", _text.Length);

        private PToken Peek(int ahead) => _p + ahead < _t.Count ? _t[_p + ahead] : new PToken(StTokenKind.Symbol, "", _text.Length);

        private PToken Previous => _p > 0 ? _t[_p - 1] : new PToken(StTokenKind.Symbol, "", 0);

        private PToken Next() => _t[_p++];

        private bool IsKeyword(PToken t) => t.Kind == StTokenKind.Identifier && Keywords.Contains(t.Text);

        private bool AtWord(string word) => Cur.IsWord(word);

        private static string Show(PToken t) => t.Text.Length == 0 ? "el final del código" : $"«{t.Text}»";

        private void Error(PToken at, string code, string message) => ErrorAt(at.Offset, Math.Max(1, at.Text.Length), code, message);

        private void ErrorAt(int offset, int length, string code, string message)
        {
            if (_text.Length > 0 && offset >= _text.Length)
            {
                offset = _text.Length - 1;
                length = 1;
            }

            if (!_reported.Add(offset)) return;
            var (line, column) = _lines.At(Math.Max(0, offset));
            _errors.Add(new StDiagnostic(offset, length, line, column, StSeverity.Error, code, message));
        }

        /// <summary>Falta algo justo detrás del último token leído: se marca su final.</summary>
        private void Missing(string what)
        {
            var previous = Previous;
            ErrorAt(Math.Max(0, previous.End - 1), 1, "ST001", $"Falta {what} detrás de «{previous.Text}».");
        }

        private void ExpectSemicolon()
        {
            if (Cur.Is(";")) _p++;
            else Missing("«;»");
        }

        private bool ExpectWord(string word, string context)
        {
            if (AtWord(word))
            {
                _p++;
                return true;
            }

            Error(Cur, "ST001", $"Se esperaba {word} {context} y hay {Show(Cur)}.");
            return false;
        }

        /// <summary>Tras un error: hasta el siguiente <c>;</c> (incluido) o la siguiente palabra clave de sentencia.</summary>
        private void Sync()
        {
            while (!AtEnd)
            {
                if (Cur.Is(";"))
                {
                    _p++;
                    return;
                }

                if (IsKeyword(Cur) && !Cur.IsWord("AND") && !Cur.IsWord("OR") && !Cur.IsWord("XOR") && !Cur.IsWord("NOT") &&
                    !Cur.IsWord("MOD") && !Cur.IsWord("TRUE") && !Cur.IsWord("FALSE"))
                    return;
                _p++;
            }
        }

        // ------------------------------------------------------------ sentencias

        private List<StStatement> Statements(Func<bool> stop)
        {
            var list = new List<StStatement>();
            while (!AtEnd && !stop())
            {
                var before = _p;
                if (Statement() is { } statement) list.Add(statement);
                if (_p == before) _p++;   // siempre se avanza
            }

            return list;
        }

        private StStatement? Statement()
        {
            var t = Cur;
            if (t.Is(";"))
            {
                _p++;
                return null;
            }

            if (t.Kind == StTokenKind.Identifier)
            {
                switch (t.Text.ToUpperInvariant())
                {
                    case "IF": return If();
                    case "CASE": return Case();
                    case "FOR": return For();
                    case "WHILE": return While();
                    case "REPEAT": return Repeat();
                    case "EXIT":
                    case "RETURN":
                    case "CONTINUE":
                        _p++;
                        ExpectSemicolon();
                        return new JumpStatement(t);
                    case "END_IF" or "ELSIF" or "ELSE" or "THEN":
                        return Stray(t, "IF");
                    case "END_CASE" or "OF":
                        return Stray(t, "CASE");
                    case "END_FOR" or "TO" or "BY" or "DO":
                        return Stray(t, "FOR / WHILE");
                    case "END_WHILE":
                        return Stray(t, "WHILE");
                    case "END_REPEAT" or "UNTIL":
                        return Stray(t, "REPEAT");
                }

                if (t.Text.StartsWith("END_FUNCTION", StringComparison.OrdinalIgnoreCase) || t.Text.Equals("END_VAR", StringComparison.OrdinalIgnoreCase))
                {
                    Error(t, "ST061", $"«{t.Text}» no va en el código: el bloque lo cierra el editor.");
                    _p++;
                    return null;
                }
            }

            if (t.Kind == StTokenKind.Identifier && !IsKeyword(t) || t.Is("#") || t.Kind == StTokenKind.String && t.Text.StartsWith('"'))
                return AssignmentOrCall();

            Error(t, "ST003", $"Una sentencia no puede empezar por {Show(t)}.");
            _p++;
            Sync();
            return null;
        }

        private StStatement? Stray(PToken t, string owner)
        {
            Error(t, "ST004", $"«{t.Text}» sin su {owner}.");
            _p++;
            if (Cur.Is(";")) _p++;
            return null;
        }

        private StStatement? AssignmentOrCall()
        {
            var target = Postfix();
            if (Cur.Is(":="))
            {
                var op = Next();
                var value = Expression();
                if (value is ErrorExpr)
                {
                    Sync();
                    return new AssignStatement(target, op, value);
                }

                ExpectSemicolon();
                return new AssignStatement(target, op, value);
            }

            if (target is CallExpr call)
            {
                ExpectSemicolon();
                return new CallStatement(call);
            }

            if (Cur.Is("="))
                Error(Cur, "ST002", "Para asignar se escribe «:=»; «=» es comparar.");
            else if (target is not ErrorExpr)
                ErrorAt(target.Offset, Math.Max(1, target.End - target.Offset), "ST003",
                    Cur.Is(";") ? "Esta sentencia no hace nada: falta «:= valor» o la llamada «( … )»." : $"Se esperaba «:=» y hay {Show(Cur)}.");
            Sync();
            return target is ErrorExpr ? null : new ExpressionStatement(target);
        }

        private StStatement If()
        {
            var keyword = Next();
            var branches = new List<IfBranch>();
            var condition = Expression();
            ExpectWord("THEN", "tras la condición del IF");
            var body = Statements(() => AtWord("ELSIF") || AtWord("ELSE") || AtWord("END_IF"));
            branches.Add(new IfBranch(condition, body));
            IReadOnlyList<StStatement>? otherwise = null;
            while (AtWord("ELSIF"))
            {
                _p++;
                var c = Expression();
                ExpectWord("THEN", "tras la condición del ELSIF");
                branches.Add(new IfBranch(c, Statements(() => AtWord("ELSIF") || AtWord("ELSE") || AtWord("END_IF"))));
            }

            if (AtWord("ELSE"))
            {
                _p++;
                otherwise = Statements(() => AtWord("END_IF") || AtWord("ELSIF") || AtWord("ELSE"));
            }

            Close("END_IF", keyword);
            return new IfStatement(keyword, branches, otherwise);
        }

        /// <summary>Cierre de un bloque (<c>END_IF</c>…), con su <c>;</c> opcional.</summary>
        private void Close(string word, PToken opener)
        {
            if (AtWord(word))
            {
                _p++;
                if (Cur.Is(";")) _p++;
                return;
            }

            if (AtEnd) Error(opener, "ST005", $"«{opener.Text}» sin su {word}.");
            else Error(Cur, "ST001", $"Se esperaba {word} y hay {Show(Cur)}.");
        }

        private StStatement Case()
        {
            var keyword = Next();
            var selector = Expression();
            ExpectWord("OF", "tras el selector del CASE");
            var branches = new List<CaseBranch>();
            IReadOnlyList<StStatement>? otherwise = null;
            while (!AtEnd && !AtWord("END_CASE"))
            {
                if (AtWord("ELSE"))
                {
                    _p++;
                    otherwise = Statements(() => AtWord("END_CASE"));
                    break;
                }

                if (!IsLabelStart())
                {
                    Error(Cur, "ST006", $"Se esperaba una etiqueta del CASE («10:», «1..5:», «A, B:») y hay {Show(Cur)}.");
                    var before = _p;
                    Sync();
                    if (_p == before) _p++;
                    continue;
                }

                var labels = new List<CaseLabel>();
                do
                {
                    var from = Unary();
                    StExpr? to = null;
                    if (Cur.Is(".") && Peek(1).Is("."))
                    {
                        _p += 2;
                        to = Unary();
                    }

                    labels.Add(new CaseLabel(from, to));
                }
                while (Cur.Is(",") && Next().Is(","));

                if (Cur.Is(":")) _p++;
                else Missing("«:» tras la etiqueta del CASE");
                branches.Add(new CaseBranch(labels, Statements(() => AtWord("END_CASE") || AtWord("ELSE") || IsLabelStart())));
            }

            Close("END_CASE", keyword);
            return new CaseStatement(keyword, selector, branches, otherwise);
        }

        /// <summary>¿Empieza aquí una etiqueta de CASE? Valores, <c>..</c> y comas hasta un <c>:</c>.</summary>
        private bool IsLabelStart()
        {
            for (var k = _p; k < _t.Count; k++)
            {
                var t = _t[k];
                if (t.Is(":")) return k > _p;
                if (t.Kind is StTokenKind.Number or StTokenKind.TypedLiteral || t.Is("-") || t.Is(".") || t.Is(",") || t.Is("#")) continue;
                if (t.Kind == StTokenKind.Identifier && !IsKeyword(t)) continue;
                return false;
            }

            return false;
        }

        private StStatement For()
        {
            var keyword = Next();
            var variable = Cur;
            if (variable.Kind == StTokenKind.Identifier && !IsKeyword(variable)) _p++;
            else Error(variable, "ST001", $"Se esperaba la variable del FOR y hay {Show(variable)}.");
            if (Cur.Is(":=")) _p++;
            else Missing("«:=» tras la variable del FOR");
            var from = Expression();
            ExpectWord("TO", "en el FOR");
            var to = Expression();
            StExpr? by = null;
            if (AtWord("BY"))
            {
                _p++;
                by = Expression();
            }

            ExpectWord("DO", "en el FOR");
            var body = Statements(() => AtWord("END_FOR"));
            Close("END_FOR", keyword);
            return new ForStatement(keyword, variable, from, to, by, body);
        }

        private StStatement While()
        {
            var keyword = Next();
            var condition = Expression();
            ExpectWord("DO", "tras la condición del WHILE");
            var body = Statements(() => AtWord("END_WHILE"));
            Close("END_WHILE", keyword);
            return new WhileStatement(keyword, condition, body);
        }

        private StStatement Repeat()
        {
            var keyword = Next();
            var body = Statements(() => AtWord("UNTIL") || AtWord("END_REPEAT"));
            StExpr until = new ErrorExpr(Cur.Offset);
            if (ExpectWord("UNTIL", "al final del REPEAT")) until = Expression();
            Close("END_REPEAT", keyword);
            return new RepeatStatement(keyword, body, until);
        }

        // ------------------------------------------------------------ expresiones

        private StExpr Expression() => Binary(0);

        private static readonly string[][] Levels =
        [
            ["OR"], ["XOR"], ["AND", "&"], ["=", "<>"], ["<", ">", "<=", ">="], ["+", "-"], ["*", "/", "MOD"], ["**"]
        ];

        private StExpr Binary(int level)
        {
            if (level == Levels.Length) return Unary();
            var left = Binary(level + 1);
            while (Levels[level].Any(op => Cur.Kind == StTokenKind.Symbol ? Cur.Text == op : Cur.IsWord(op)))
            {
                var op = Next();
                var right = Binary(level + 1);
                left = new BinaryExpr(op, left, right);
            }

            return left;
        }

        private StExpr Unary()
        {
            if (AtWord("NOT") || Cur.Is("-") || Cur.Is("+"))
            {
                var op = Next();
                return new UnaryExpr(op, Unary());
            }

            return Postfix();
        }

        private StExpr Postfix()
        {
            var expr = Primary();
            while (true)
            {
                if (Cur.Is(".") && !Peek(1).Is("."))
                {
                    _p++;
                    if (Cur.Kind is StTokenKind.Identifier or StTokenKind.Number) expr = new MemberExpr(expr, Next());
                    else
                    {
                        Error(Cur, "ST001", $"Se esperaba un nombre detrás del punto y hay {Show(Cur)}.");
                        return expr;
                    }
                }
                else if (Cur.Is("["))
                {
                    _p++;
                    var indices = new List<StExpr> { Expression() };
                    while (Cur.Is(","))
                    {
                        _p++;
                        indices.Add(Expression());
                    }

                    var close = Cur.End;
                    if (Cur.Is("]")) _p++;
                    else Missing("«]»");
                    expr = new IndexExpr(expr, indices, close);
                }
                else if (Cur.Is("("))
                {
                    expr = Call(expr);
                }
                else
                {
                    return expr;
                }
            }
        }

        private CallExpr Call(StExpr target)
        {
            _p++;   // (
            var arguments = new List<StArgument>();
            if (!Cur.Is(")"))
            {
                while (true)
                {
                    if (Cur.Kind == StTokenKind.Identifier && (Peek(1).Is(":=") || Peek(1).Is("=>")))
                    {
                        var name = Next();
                        var output = Next().Is("=>");
                        arguments.Add(new StArgument(name, output, Expression()));
                    }
                    else
                    {
                        arguments.Add(new StArgument(null, false, Expression()));
                    }

                    if (Cur.Is(","))
                    {
                        _p++;
                        continue;
                    }

                    break;
                }
            }

            var close = Cur.End;
            if (Cur.Is(")")) _p++;
            else Missing("«)» al final de la llamada");
            return new CallExpr(target, arguments, close);
        }

        private StExpr Primary()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case StTokenKind.Number or StTokenKind.TypedLiteral:
                    _p++;
                    return new LiteralExpr(t);
                case StTokenKind.String when t.Text.StartsWith('"'):
                    Error(t, "ST060", "En el master no se escriben nombres entre comillas dobles: las pone el emisor de TIA.");
                    _p++;
                    return new NameExpr(t with { Text = t.Text.Trim('"'), Offset = t.Offset + 1 });
                case StTokenKind.String:
                    _p++;
                    return new LiteralExpr(t);
                case StTokenKind.Identifier when t.IsWord("TRUE") || t.IsWord("FALSE"):
                    _p++;
                    return new LiteralExpr(t);
                case StTokenKind.Identifier when !IsKeyword(t):
                    _p++;
                    return new NameExpr(t);
            }

            if (t.Is("#"))
            {
                Error(t, "ST060", "En el master las variables van sin «#»: la pone el emisor de TIA.");
                _p++;
                return Primary();
            }

            if (t.Is("("))
            {
                _p++;
                var inner = Expression();
                var close = Cur.End;
                if (Cur.Is(")")) _p++;
                else Missing("«)»");
                return new ParenExpr(inner, t.Offset, close);
            }

            Error(t, "ST007", $"Se esperaba un valor y hay {Show(t)}.");
            if (!AtEnd && !t.Is(";") && !IsKeyword(t) && !t.Is(")")) _p++;
            return new ErrorExpr(t.Offset);
        }
    }
}
