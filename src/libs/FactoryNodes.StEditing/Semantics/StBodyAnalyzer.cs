using FactoryNodes.Iec61131.Model;
using FactoryNodes.Iec61131.Syntax;
using FactoryNodes.StEditing.Syntax;

namespace FactoryNodes.StEditing.Semantics;

/// <summary>Qué es un nombre del cuerpo, para colorearlo.</summary>
public enum StNameRole
{
    Variable,
    Parameter,
    Constant,
    Function,
    Member,
    Unknown
}

/// <summary>Dónde aparece un nombre y qué es.</summary>
public readonly record struct StNameUse(int Offset, int Length, StNameRole Role);

/// <summary>Resultado de analizar un cuerpo: sintaxis, errores y avisos (ordenados) y qué es cada nombre.</summary>
public sealed record StBodyAnalysis(StBodySyntax Syntax, IReadOnlyList<StDiagnostic> Diagnostics, IReadOnlyList<StNameUse> Names)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == StSeverity.Error);
}

/// <summary>
/// Comprueba el cuerpo de una POU contra lo que se ve desde él (<see cref="StScope"/>): nombres sin declarar (con
/// «¿quisiste decir…?»), miembros que no existen, tipos que no casan, llamadas a bloques y funciones, escrituras en
/// constantes o en salidas de otro bloque, condiciones que no son BOOL, EXIT fuera de un bucle y variables sin usar.
/// No sustituye al compilador del PLC: lo que dice que está mal, está mal; lo que deja pasar lo terminan de decir el
/// validador (<c>ML1xx</c>) y el PLC. Códigos <c>ST0xx</c>.
/// </summary>
public static class StBodyAnalyzer
{
    public static StBodyAnalysis Analyze(string body, StScope scope)
    {
        var syntax = StBodyParser.Parse(body);
        var checker = new Checker(body, scope);
        checker.Run(syntax);
        var diagnostics = syntax.Errors.Concat(checker.Diagnostics)
            .OrderBy(d => d.Offset).ThenBy(d => d.Code, StringComparer.Ordinal)
            .ToList();
        return new StBodyAnalysis(syntax, diagnostics, checker.Names);
    }

    private sealed class Checker(string text, StScope scope)
    {
        private readonly LineMap _lines = new(text);
        private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);
        private int _loops;

        public List<StDiagnostic> Diagnostics { get; } = [];
        public List<StNameUse> Names { get; } = [];

        public void Run(StBodySyntax syntax)
        {
            Statements(syntax.Statements);
            foreach (var symbol in scope.Locals.Where(s => s.Kind is StSymbolKind.Variable or StSymbolKind.Constant && !_used.Contains(s.Name)))
            {
                var declared = scope.Pou.Variables.FirstOrDefault(v => v.Name.Equals(symbol.Name, StringComparison.OrdinalIgnoreCase));
                Warning(0, 0, "ST070", $"«{symbol.Name}» está declarada y no se usa en el código" +
                                        (declared is not null ? $" (línea {declared.Location.Line} del fichero)." : "."));
            }
        }

        // ------------------------------------------------------------ diagnósticos

        private void Add(int offset, int length, StSeverity severity, string code, string message)
        {
            if (Diagnostics.Any(d => d.Offset == offset && d.Code == code && d.Message == message)) return;
            var (line, column) = _lines.At(Math.Clamp(offset, 0, Math.Max(0, text.Length)));
            Diagnostics.Add(new StDiagnostic(offset, Math.Max(length, 0), line, column, severity, code, message));
        }

        private void Error(StExpr at, string code, string message) => Add(at.Offset, Math.Max(1, at.End - at.Offset), StSeverity.Error, code, message);

        private void Error(PToken at, string code, string message) => Add(at.Offset, Math.Max(1, at.Text.Length), StSeverity.Error, code, message);

        private void Warning(int offset, int length, string code, string message) => Add(offset, length, StSeverity.Warning, code, message);

        private static string WithSuggestion(string message, string? suggestion) =>
            suggestion is null ? message + "." : $"{message}. ¿Quisiste decir «{suggestion}»?";

        // ------------------------------------------------------------ sentencias

        private void Statements(IEnumerable<StStatement> statements)
        {
            foreach (var statement in statements) Statement(statement);
        }

        private void Statement(StStatement statement)
        {
            switch (statement)
            {
                case AssignStatement a:
                    Assign(a);
                    break;
                case CallStatement c:
                    Call(c.Call, needsValue: false);
                    break;
                case ExpressionStatement e:
                    TypeOf(e.Expression);
                    break;
                case IfStatement i:
                    foreach (var branch in i.Branches)
                    {
                        Condition(branch.Condition, "del IF");
                        Statements(branch.Body);
                    }

                    if (i.Else is not null) Statements(i.Else);
                    break;
                case CaseStatement c:
                    Case(c);
                    break;
                case ForStatement f:
                    For(f);
                    break;
                case WhileStatement w:
                    Condition(w.Condition, "del WHILE");
                    _loops++;
                    Statements(w.Body);
                    _loops--;
                    break;
                case RepeatStatement r:
                    _loops++;
                    Statements(r.Body);
                    _loops--;
                    Condition(r.Until, "del UNTIL");
                    break;
                case JumpStatement j when !j.Keyword.IsWord("RETURN") && _loops == 0:
                    Error(j.Keyword, "ST050", $"{j.Keyword.Text.ToUpperInvariant()} solo vale dentro de un FOR, WHILE o REPEAT.");
                    break;
            }
        }

        private void Condition(StExpr condition, string where)
        {
            var type = TypeOf(condition);
            if (!type.IsUnknown && type.Kind != StTypeKind.Bool)
                Error(condition, "ST031", $"La condición {where} tiene que ser BOOL y es {StType.Describe(type)}.");
        }

        private void Assign(AssignStatement a)
        {
            var target = WriteTarget(a.Target);
            var value = TypeOf(a.Value);
            if (StType.CannotAssign(target, value) is { } why) Error(a.Value, "ST030", why);
        }

        /// <summary>Tipo de lo que se escribe, comprobando que se puede escribir.</summary>
        private StType WriteTarget(StExpr target)
        {
            switch (target)
            {
                case NameExpr n:
                {
                    var symbol = scope.Lookup(n.Name.Text);
                    if (symbol is { IsConstant: true })
                        Error(n.Name, "ST021", $"«{symbol.Name}» es una constante: no se puede escribir.");
                    else if (symbol is { IsCallable: true })
                        Error(n.Name, "ST020", $"«{symbol.Name}» es una función: no se le asigna nada.");
                    else if (symbol is { Kind: StSymbolKind.Input })
                        Warning(n.Name.Offset, n.Name.Text.Length, "ST022",
                            $"«{symbol.Name}» es una entrada (VAR_INPUT): lo que se escriba aquí se pierde en la siguiente llamada.");
                    return TypeOf(target);
                }
                case MemberExpr m:
                {
                    var owner = TypeOf(m.Target);
                    var type = TypeOf(target);
                    if (owner.Kind == StTypeKind.FunctionBlock &&
                        scope.MembersOf(owner).FirstOrDefault(x => x.Name.Equals(m.Member.Text, StringComparison.OrdinalIgnoreCase)) is { Direction: VarSectionKind.Output })
                        Error(m.Member, "ST023", $"«{m.Member.Text}» es una salida de {owner.Name}: la escribe el bloque, aquí solo se lee.");
                    if (Root(m) is { } root && scope.Lookup(root.Name.Text) is { IsConstant: true } constant)
                        Error(root.Name, "ST021", $"«{constant.Name}» es una constante: no se puede escribir.");
                    return type;
                }
                case IndexExpr i:
                    if (Root(i) is { } r && scope.Lookup(r.Name.Text) is { IsConstant: true } c)
                        Error(r.Name, "ST021", $"«{c.Name}» es una constante: no se puede escribir.");
                    return TypeOf(target);
                case ErrorExpr:
                    return StType.Unknown;
                default:
                    Error(target, "ST020", "Aquí no se puede asignar: a la izquierda de «:=» va una variable.");
                    TypeOf(target);
                    return StType.Unknown;
            }
        }

        private static NameExpr? Root(StExpr e) => e switch
        {
            NameExpr n => n,
            MemberExpr m => Root(m.Target),
            IndexExpr i => Root(i.Target),
            _ => null
        };

        private void Case(CaseStatement c)
        {
            var selector = TypeOf(c.Selector);
            if (!selector.IsUnknown && selector.Kind is not (StTypeKind.Integer or StTypeKind.AnyInt or StTypeKind.Bits))
                Error(c.Selector, "ST033", $"El selector del CASE tiene que ser un entero y es {StType.Describe(selector)}.");

            var seen = new Dictionary<string, CaseLabel>(StringComparer.OrdinalIgnoreCase);
            foreach (var branch in c.Branches)
            {
                foreach (var label in branch.Labels)
                {
                    foreach (var part in new[] { label.From, label.To }.OfType<StExpr>())
                    {
                        var type = TypeOf(part);
                        if (!type.IsUnknown && !selector.IsUnknown && !StType.Comparable(selector, type))
                            Error(part, "ST030", $"La etiqueta es {StType.Describe(type)} y el selector {StType.Describe(selector)}.");
                    }

                    var key = text[label.From.Offset..(label.To ?? label.From).End].Replace(" ", "");
                    if (!seen.TryAdd(key, label))
                        Error(label.From, "ST051", $"La etiqueta «{key}» ya está en este CASE: la segunda no se ejecutaría nunca.");
                }

                Statements(branch.Body);
            }

            if (c.Else is not null) Statements(c.Else);
        }

        private void For(ForStatement f)
        {
            var variable = StType.Unknown;
            if (f.Variable.Kind == StTokenKind.Identifier && !StBodyParser.Keywords.Contains(f.Variable.Text))
            {
                variable = TypeOf(new NameExpr(f.Variable));
                if (!variable.IsUnknown && variable.Kind != StTypeKind.Integer)
                    Error(f.Variable, "ST034", $"La variable del FOR tiene que ser un entero y «{f.Variable.Text}» es {StType.Describe(variable)}.");
                if (scope.Lookup(f.Variable.Text) is { IsConstant: true })
                    Error(f.Variable, "ST021", $"«{f.Variable.Text}» es una constante: no puede ser la variable del FOR.");
            }

            foreach (var limit in new[] { f.From, f.To, f.By }.OfType<StExpr>())
                if (StType.CannotAssign(variable, TypeOf(limit)) is { } why) Error(limit, "ST030", why);

            _loops++;
            Statements(f.Body);
            _loops--;
        }

        // ------------------------------------------------------------ expresiones

        public StType TypeOf(StExpr expr)
        {
            switch (expr)
            {
                case LiteralExpr l:
                    return StType.OfLiteral(l.Token.Text, scope.Library);
                case NameExpr n:
                    return Name(n);
                case MemberExpr m:
                    return Member(m);
                case IndexExpr i:
                    return Index(i);
                case CallExpr c:
                    return Call(c, needsValue: true);
                case ParenExpr p:
                    return TypeOf(p.Inner);
                case UnaryExpr u:
                    return Unary(u);
                case BinaryExpr b:
                    return Binary(b);
                default:
                    return StType.Unknown;
            }
        }

        private StType Name(NameExpr n)
        {
            var symbol = scope.Lookup(n.Name.Text);
            if (symbol is null)
            {
                Names.Add(new StNameUse(n.Name.Offset, n.Name.Text.Length, StNameRole.Unknown));
                Error(n.Name, "ST010", WithSuggestion($"«{n.Name.Text}» no está declarada", StScope.Suggest(n.Name.Text, scope.VisibleNames)));
                return StType.Unknown;
            }

            _used.Add(symbol.Name);
            Names.Add(new StNameUse(n.Name.Offset, n.Name.Text.Length, symbol.Kind switch
            {
                StSymbolKind.Input or StSymbolKind.Output or StSymbolKind.InOut => StNameRole.Parameter,
                StSymbolKind.Constant or StSymbolKind.SizeConstant => StNameRole.Constant,
                StSymbolKind.Function or StSymbolKind.StandardFunction or StSymbolKind.Conversion => StNameRole.Function,
                _ => StNameRole.Variable
            }));
            if (symbol.IsCallable)
            {
                Error(n.Name, "ST044", $"«{symbol.Name}» es una función: se llama con paréntesis, «{symbol.Name}(…)».");
                return StType.Unknown;
            }

            return symbol.Type;
        }

        private StType Member(MemberExpr m)
        {
            var owner = TypeOf(m.Target);
            Names.Add(new StNameUse(m.Member.Offset, m.Member.Text.Length, StNameRole.Member));
            if (owner.IsUnknown) return StType.Unknown;

            if (m.Member.Kind == StTokenKind.Number)
            {
                if (owner.Kind is StTypeKind.Bits or StTypeKind.Integer) return StType.Bool;
                Error(m.Member, "ST012", $"Solo se accede a un bit («.{m.Member.Text}») de un entero o una palabra, no de {StType.Describe(owner)}.");
                return StType.Unknown;
            }

            if (owner.Kind is not (StTypeKind.Struct or StTypeKind.FunctionBlock))
            {
                Error(m.Member, "ST012", $"{StType.Describe(owner)} no tiene miembros: «.{m.Member.Text}» sobra.");
                return StType.Unknown;
            }

            var members = scope.MembersOf(owner);
            var member = members.FirstOrDefault(x => x.Name.Equals(m.Member.Text, StringComparison.OrdinalIgnoreCase));
            if (member is not null) return member.Type;

            var inOut = owner.Kind == StTypeKind.FunctionBlock &&
                        scope.ParametersOf(owner.Name).Any(p => p.Direction == VarSectionKind.InOut && p.Name.Equals(m.Member.Text, StringComparison.OrdinalIgnoreCase));
            Error(m.Member, "ST011", inOut
                ? $"«{m.Member.Text}» es VAR_IN_OUT de {owner.Name}: no se lee desde fuera, se pasa en la llamada."
                : WithSuggestion($"{owner.Name} no tiene «{m.Member.Text}»", StScope.Suggest(m.Member.Text, members.Select(x => x.Name))));
            return StType.Unknown;
        }

        private StType Index(IndexExpr i)
        {
            var owner = TypeOf(i.Target);
            foreach (var index in i.Indices)
            {
                var type = TypeOf(index);
                if (!type.IsUnknown && type.Kind is not (StTypeKind.Integer or StTypeKind.AnyInt))
                    Error(index, "ST013", $"El índice tiene que ser un entero y es {StType.Describe(type)}.");
            }

            if (owner.IsUnknown) return StType.Unknown;
            if (owner.Kind != StTypeKind.Array)
            {
                Error(i, "ST013", $"Solo un ARRAY lleva índice «[ ]», y esto es {StType.Describe(owner)}.");
                return StType.Unknown;
            }

            if (i.Indices.Count == 1 && i.Indices[0] is LiteralExpr { Token.Kind: StTokenKind.Number } literal &&
                int.TryParse(literal.Token.Text, out var value) && owner.Bounds is { } bounds &&
                scope.Library.BoundValue(bounds.Lower) is { } lo && scope.Library.BoundValue(bounds.Upper) is { } hi &&
                (value < lo || value > hi))
                Error(literal, "ST014", $"El índice {value} está fuera del array [{lo}..{hi}].");
            return owner.Element ?? StType.Unknown;
        }

        private StType Unary(UnaryExpr u)
        {
            var type = TypeOf(u.Operand);
            if (type.IsUnknown) return type;
            if (u.Operator.IsWord("NOT"))
            {
                if (type.Kind is StTypeKind.Bool or StTypeKind.Bits or StTypeKind.Integer or StTypeKind.AnyInt) return type;
                Error(u, "ST032", $"NOT se aplica a BOOL o a palabras, no a {StType.Describe(type)}.");
                return StType.Unknown;
            }

            if (type.IsNumeric || type.Kind == StTypeKind.Time) return type;
            Error(u, "ST032", $"«{u.Operator.Text}» se aplica a números, no a {StType.Describe(type)}.");
            return StType.Unknown;
        }

        private StType Binary(BinaryExpr b)
        {
            var left = TypeOf(b.Left);
            var right = TypeOf(b.Right);
            var op = b.Operator.Text.ToUpperInvariant();
            if (left.IsUnknown || right.IsUnknown)
                return op is "=" or "<>" or "<" or ">" or "<=" or ">=" ? StType.Bool : StType.Unknown;

            switch (op)
            {
                case "AND" or "&" or "OR" or "XOR":
                    if (left.Kind == StTypeKind.Bool && right.Kind == StTypeKind.Bool) return StType.Bool;
                    if (left.Kind is StTypeKind.Bits or StTypeKind.Integer or StTypeKind.AnyInt &&
                        right.Kind is StTypeKind.Bits or StTypeKind.Integer or StTypeKind.AnyInt)
                        return StType.Arithmetic(left, right);
                    Error(b, "ST032", $"{op} entre {StType.Describe(left)} y {StType.Describe(right)}: los dos tienen que ser BOOL (o los dos palabras).");
                    return StType.Bool;
                case "=" or "<>" or "<" or ">" or "<=" or ">=":
                    if (!StType.Comparable(left, right))
                        Error(b, "ST030", $"No se puede comparar {StType.Describe(left)} con {StType.Describe(right)} sin convertir uno de los dos.");
                    else if (op is not ("=" or "<>") && (left.Kind == StTypeKind.Bool || right.Kind == StTypeKind.Bool))
                        Error(b, "ST032", $"«{op}» no compara BOOL: usa = o <>.");
                    return StType.Bool;
                default:
                    if ((left.IsNumeric || left.Kind == StTypeKind.Time) && (right.IsNumeric || right.Kind == StTypeKind.Time))
                    {
                        if (left.Kind is StTypeKind.Integer or StTypeKind.Real && right.Kind is StTypeKind.Integer or StTypeKind.Real &&
                            !StType.Same(left, right) && StType.CannotAssign(left, right) is not null && StType.CannotAssign(right, left) is not null)
                            Error(b, "ST030", $"«{op}» entre {left.Name} y {right.Name}: convierte uno de los dos para que sean del mismo tipo.");
                        return StType.Arithmetic(left, right);
                    }

                    Error(b, "ST032", $"«{op}» se aplica a números o tiempos, no a {StType.Describe(left.IsNumeric || left.Kind == StTypeKind.Time ? right : left)}.");
                    return StType.Unknown;
            }
        }

        // ------------------------------------------------------------ llamadas

        private StType Call(CallExpr call, bool needsValue)
        {
            if (call.Target is NameExpr n)
            {
                var symbol = scope.Lookup(n.Name.Text);
                if (symbol is null)
                {
                    Names.Add(new StNameUse(n.Name.Offset, n.Name.Text.Length, StNameRole.Unknown));
                    Error(n.Name, "ST010", WithSuggestion($"«{n.Name.Text}» no está declarada", StScope.Suggest(n.Name.Text, scope.VisibleNames)));
                    Arguments(call);
                    return StType.Unknown;
                }

                _used.Add(symbol.Name);
                if (symbol.IsCallable)
                {
                    Names.Add(new StNameUse(n.Name.Offset, n.Name.Text.Length, StNameRole.Function));
                    return Function(call, symbol);
                }

                Names.Add(new StNameUse(n.Name.Offset, n.Name.Text.Length, StNameRole.Variable));
                return Instance(call, symbol.Type, needsValue, n.Name);
            }

            var type = TypeOf(call.Target);
            return Instance(call, type, needsValue, null);
        }

        /// <summary>Llamada a una instancia de bloque (<c>tStart(IN := …)</c>).</summary>
        private StType Instance(CallExpr call, StType type, bool needsValue, PToken? name)
        {
            if (type.IsUnknown)
            {
                Arguments(call);
                return StType.Unknown;
            }

            if (type.Kind != StTypeKind.FunctionBlock)
            {
                Error(call.Target, "ST044", $"{(name is { } t ? $"«{t.Text}»" : "Esto")} es {StType.Describe(type)}: no es un bloque ni una función, no se llama.");
                Arguments(call);
                return StType.Unknown;
            }

            var parameters = scope.ParametersOf(type.Name);
            var given = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var argument in call.Arguments)
            {
                if (argument.Name is not { } argName)
                {
                    Error(argument.Value, "ST042", $"A un bloque se le pasan los parámetros por su nombre: «{parameters.FirstOrDefault()?.Name ?? "IN"} := …».");
                    TypeOf(argument.Value);
                    continue;
                }

                Names.Add(new StNameUse(argName.Offset, argName.Text.Length, StNameRole.Member));
                if (!given.Add(argName.Text)) Error(argName, "ST047", $"«{argName.Text}» está dos veces en la llamada.");
                var parameter = parameters.FirstOrDefault(p => p.Name.Equals(argName.Text, StringComparison.OrdinalIgnoreCase));
                if (parameter is null)
                {
                    Error(argName, "ST041", WithSuggestion($"{type.Name} no tiene el parámetro «{argName.Text}»", StScope.Suggest(argName.Text, parameters.Select(p => p.Name))));
                    TypeOf(argument.Value);
                    continue;
                }

                Argument(argument, parameter, type.Name);
            }

            foreach (var missing in parameters.Where(p => p.Direction == VarSectionKind.InOut && !given.Contains(p.Name)))
                Error(call.Target, "ST043", $"Falta «{missing.Name}» en la llamada: un VAR_IN_OUT de {type.Name} se pasa siempre.");

            if (needsValue)
                Error(call, "ST045", $"La llamada a un bloque no da un valor: se llama sola y se leen sus salidas (instancia.Q).");
            return StType.Unknown;
        }

        private void Argument(StArgument argument, StParameter parameter, string owner)
        {
            var parameterType = StType.Of(parameter.TypeName, scope.Library);   // un ARRAY da Unknown: no se comprueba
            if (argument.IsOutput)
            {
                if (parameter.Direction != VarSectionKind.Output)
                    Error(argument.Name!.Value, "ST046", $"«{parameter.Name}» no es una salida de {owner}: se pasa con «:=».");
                var target = WriteTarget(argument.Value);
                if (StType.CannotAssign(target, parameterType) is { } why) Error(argument.Value, "ST030", why);
                return;
            }

            if (parameter.Direction == VarSectionKind.Output)
                Error(argument.Name!.Value, "ST046", $"«{parameter.Name}» es una salida de {owner}: se recoge con «=>» o se lee después como instancia.{parameter.Name}.");
            var value = parameter.Direction == VarSectionKind.InOut ? WriteTarget(argument.Value) : TypeOf(argument.Value);
            if (StType.CannotAssign(parameterType, value) is { } problem) Error(argument.Value, "ST030", $"{parameter.Name}: {problem}");
        }

        private void Arguments(CallExpr call)
        {
            foreach (var argument in call.Arguments) TypeOf(argument.Value);
        }

        /// <summary>Llamada a una función: de la librería, estándar o conversión.</summary>
        private StType Function(CallExpr call, StSymbol symbol)
        {
            switch (symbol.Kind)
            {
                case StSymbolKind.Conversion:
                {
                    var (from, to) = StandardFunctions.Conversion(symbol.Name)!.Value;
                    if (call.Arguments.Count != 1)
                        Error(call, "ST048", $"{symbol.Name} recibe un solo valor.");
                    foreach (var argument in call.Arguments)
                    {
                        var value = TypeOf(argument.Value);
                        if (StType.CannotAssign(StType.Of(from, scope.Library), value) is not null && !value.IsUnknown)
                            Error(argument.Value, "ST030", $"{symbol.Name} convierte un {from} y esto es {StType.Describe(value)}.");
                    }

                    return StType.Of(to, scope.Library);
                }
                case StSymbolKind.StandardFunction:
                {
                    var function = StandardFunctions.Get(symbol.Name)!;
                    if (call.Arguments.Count < function.MinArguments || call.Arguments.Count > function.MaxArguments)
                        Error(call, "ST048", function.MinArguments == function.MaxArguments
                            ? $"{function.Name} recibe {function.MinArguments} valor(es) y hay {call.Arguments.Count}."
                            : $"{function.Name} recibe de {function.MinArguments} a {function.MaxArguments} valores y hay {call.Arguments.Count}.");
                    var types = call.Arguments.Select(a => TypeOf(a.Value)).ToList();
                    return function.Result switch
                    {
                        GenericResult.Integer => StType.AnyInt,
                        GenericResult.Bool => StType.Bool,
                        GenericResult.Second when types.Count > 1 => types[1],
                        GenericResult.First when types.Count > 0 => types.Skip(1).Aggregate(types[0], (a, b) => a.IsUnknown ? b : a),
                        _ => StType.Unknown
                    };
                }
                default:
                {
                    var pou = scope.Library.Pou(symbol.Name)!;
                    var inputs = pou.VariablesOf(VarSectionKind.Input).ToList();
                    var parameters = scope.ParametersOf(pou.Name);
                    var given = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (var k = 0; k < call.Arguments.Count; k++)
                    {
                        var argument = call.Arguments[k];
                        StParameter? parameter;
                        if (argument.Name is { } argName)
                        {
                            Names.Add(new StNameUse(argName.Offset, argName.Text.Length, StNameRole.Member));
                            parameter = parameters.FirstOrDefault(p => p.Name.Equals(argName.Text, StringComparison.OrdinalIgnoreCase));
                            if (parameter is null)
                            {
                                Error(argName, "ST041", WithSuggestion($"{pou.Name} no tiene el parámetro «{argName.Text}»", StScope.Suggest(argName.Text, parameters.Select(p => p.Name))));
                                TypeOf(argument.Value);
                                continue;
                            }
                        }
                        else if (k < inputs.Count)
                        {
                            parameter = parameters.First(p => p.Name == inputs[k].Name);
                        }
                        else
                        {
                            Error(argument.Value, "ST048", $"{pou.Name} recibe {inputs.Count} valor(es).");
                            TypeOf(argument.Value);
                            continue;
                        }

                        given.Add(parameter.Name);
                        Argument(argument, parameter, pou.Name);
                    }

                    foreach (var missing in inputs.Where(i => !given.Contains(i.Name)))
                        Error(call.Target, "ST043", $"Falta «{missing.Name}» en la llamada a {pou.Name}.");
                    return pou.ReturnType is { } ret ? StType.Of(ret, scope.Library) : StType.Unknown;
                }
            }
        }
    }
}
