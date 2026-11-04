using FactoryNodes.Iec61131.Model;
using FactoryNodes.SoftNodes.Syntax;

namespace FactoryNodes.SoftNodes.Runtime;

public enum FrameFlow
{
    None,
    Exit,
    Return
}

/// <summary>
/// Las variables de lo que se está ejecutando: las de la instancia de un bloque (que sobreviven entre ciclos) o las de una
/// llamada a una función.
/// </summary>
public sealed class Frame(string name, string file, Dictionary<string, Slot> vars, int depth, Frame? parent = null, string? path = null)
{
    public string Name { get; } = name;
    public string File { get; } = file;
    public Dictionary<string, Slot> Vars { get; } = vars;
    public int Depth { get; } = depth;
    public FrameFlow Flow { get; set; }

    /// <summary>El marco que llamó a este (null en lo primero del ciclo): la pila de llamadas, para la foto de un fallo.</summary>
    public Frame? Parent { get; } = parent;

    /// <summary>
    /// La instancia que se ejecuta, por su camino: el equipo, el loop o la sección (<c>M77</c>, <c>Loop_Carro</c>, <c>User_Inicio</c>)
    /// y, dentro, la instancia llamada (<c>M77.alarms</c>). Null si quien ejecuta no lo dice. Lo usan los puntos de parada de una
    /// sola instancia.
    /// </summary>
    public string? Path { get; } = path;

    /// <summary>La línea de la sentencia que se ejecuta ahora en este marco (en un llamador, la de la llamada).</summary>
    public int Line { get; set; }
}

/// <summary>Dónde se detiene el paso a paso: la sentencia que va a ejecutarse y el marco que la contiene.</summary>
public readonly record struct StepPoint(Frame Frame, int Line);

/// <summary>
/// Intérprete de ST estándar IEC 61131-3 sobre la librería maestra leída (docs/PENDIENTES.md 49). Ejecutar un bloque es
/// recorrer un iterador que se detiene antes de cada sentencia (<see cref="StepPoint"/>): quien quiere correr de golpe
/// lo consume entero (<see cref="Run"/>); quien quiere paso a paso lo avanza de uno en uno. El tiempo lo da quien llama
/// (<see cref="NowMs"/>), así que un ciclo es reproducible y el reloj se puede acelerar.
/// </summary>
public sealed partial class StInterpreter
{
    private readonly Dictionary<string, StType> _types = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Pou, IReadOnlyList<Stmt>> _bodies = [];
    private readonly Dictionary<string, Pou> _pous = new(StringComparer.OrdinalIgnoreCase);

    public StInterpreter(MasterLibrary library)
    {
        Library = library;
        foreach (var pou in library.Pous) _pous[pou.Name] = pou;
    }

    public MasterLibrary Library { get; }

    /// <summary>Variables globales (los datos del programa: <c>hw_P</c>…), por nombre.</summary>
    public Dictionary<string, Slot> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>El reloj del PLC en milisegundos. Lo avanza quien llama, entre ciclos.</summary>
    public long NowMs { get; set; }

    // ---------------------------------------------------------------- Tipos y valores iniciales

    public StType TypeOf(string typeName)
    {
        if (StType.Elementary.TryGetValue(typeName, out var elementary)) return elementary;
        if (_types.TryGetValue(typeName, out var known)) return known;

        StType type;
        if (Library.Type(typeName) is { } structure)
            type = new StType { Kind = StTypeKind.Struct, Name = structure.Name, Struct = structure };
        else if (IsStandardBlock(typeName))
            type = new StType { Kind = StTypeKind.FunctionBlock, Name = typeName.ToUpperInvariant() };
        else if (_pous.TryGetValue(typeName, out var pou) && pou.Kind == PouKind.FunctionBlock)
            type = new StType { Kind = StTypeKind.FunctionBlock, Name = pou.Name, Pou = pou };
        else
            throw new InvalidOperationException($"Tipo «{typeName}» desconocido.");

        _types[typeName] = type;
        return type;
    }

    /// <summary>El tipo de un miembro o variable declarado (con su array, si lo es).</summary>
    public StType TypeOf(MemberDeclaration member)
    {
        var type = TypeOf(member.TypeName);
        if (member.Array is not { } bounds) return type;
        var lower = Library.BoundValue(bounds.Lower) ?? throw new InvalidOperationException($"Límite «{bounds.Lower}» de «{member.Name}» desconocido.");
        var upper = Library.BoundValue(bounds.Upper) ?? throw new InvalidOperationException($"Límite «{bounds.Upper}» de «{member.Name}» desconocido.");
        return new StType { Kind = StTypeKind.Array, Name = "ARRAY", Element = type, Lower = lower, Upper = upper };
    }

    private static bool IsStandardBlock(string name) =>
        name.ToUpperInvariant() is "TON" or "TOF" or "TP" or "R_TRIG" or "F_TRIG" or "RS" or "SR";

    /// <summary>El valor inicial de un tipo: ceros, estructuras y arrays con sus miembros, bloques con su instancia.</summary>
    public object NewValue(StType type)
    {
        switch (type.Kind)
        {
            case StTypeKind.Struct:
                var value = new StructValue();
                foreach (var member in type.Struct!.Members) value.Members[member.Name] = NewSlot(member);
                return value;
            case StTypeKind.Array:
                var items = new Slot[type.Upper - type.Lower + 1];
                for (var i = 0; i < items.Length; i++) items[i] = new Slot(type.Element!, NewValue(type.Element!));
                return new ArrayValue(type.Lower, items);
            case StTypeKind.FunctionBlock:
                return NewInstance(type);
            default:
                return type.DefaultElementary();
        }
    }

    /// <summary>Una casilla para un miembro o variable declarado, con su valor inicial (<c>:=</c>) si lo lleva.</summary>
    public Slot NewSlot(MemberDeclaration member)
    {
        var type = TypeOf(member);
        var slot = new Slot(type, NewValue(type));
        if (member.InitialValue is { Length: > 0 } text && type.Kind is not (StTypeKind.Struct or StTypeKind.Array))
            slot.Value = Coerce(Evaluate(StParser.ParseExpression(text), EmptyFrame), type);
        return slot;
    }

    public FbInstance NewInstance(string typeName) => NewInstance(TypeOf(typeName));

    public FbInstance NewInstance(StType type)
    {
        var instance = new FbInstance(type);
        if (type.Pou is { } pou)
        {
            foreach (var section in pou.Sections)
                foreach (var member in section.Members)
                    instance.Vars[member.Name] = NewSlot(member);
        }
        else
        {
            foreach (var (name, slotType) in StandardBlockVars(type.Name))
                instance.Vars[name] = new Slot(slotType, slotType.DefaultElementary());
        }

        return instance;
    }

    private Frame EmptyFrame { get; } = new("", "", new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase), 0);

    // ---------------------------------------------------------------- Ejecución

    /// <summary>
    /// Ejecuta el cuerpo de un bloque de función sobre su instancia, deteniéndose antes de cada sentencia. Las entradas y
    /// las variables <c>VAR_IN_OUT</c> ya deben estar puestas en <c>instance.Vars</c>.
    /// </summary>
    public IEnumerable<StepPoint> Execute(FbInstance instance) => ExecuteBlock(instance, 1, null, null);

    /// <summary>Como <see cref="Execute(FbInstance)"/>, diciendo qué instancia es (<see cref="Frame.Path"/>).</summary>
    public IEnumerable<StepPoint> Execute(FbInstance instance, string path) => ExecuteBlock(instance, 1, null, path);

    /// <summary>Como <see cref="Execute(FbInstance, string)"/>, llamado desde <paramref name="caller"/> (la sección del programa: la pila de llamadas).</summary>
    public IEnumerable<StepPoint> Execute(FbInstance instance, string path, Frame caller) => ExecuteBlock(instance, caller.Depth + 1, caller, path);

    /// <summary>Ejecuta de golpe (un ciclo sin parar).</summary>
    public void Run(FbInstance instance)
    {
        foreach (var _ in Execute(instance)) { }
    }

    private IEnumerable<StepPoint> ExecuteBlock(FbInstance instance, int depth, Frame? parent, string? path)
    {
        if (instance.Type.Pou is not { } pou)
        {
            StepStandard(instance, EmptyFrame);
            yield break;
        }

        // Las variables de ciclo (VAR_TEMP) empiezan cada ejecución a cero: el bloque las escribe antes de leerlas (convenciones §1).
        // Si un bloque contara con el valor del ciclo anterior, aquí se notaría; en el PLC (TIA en VAR, Sysmac retentivas o no) no.
        foreach (var name in CycleVariables(pou))
        {
            var slot = instance.Vars[name];
            slot.Value = slot.Type.DefaultElementary();
        }

        var frame = new Frame(pou.Name, pou.Location.File, instance.Vars, depth, parent, path);
        foreach (var point in ExecuteStatements(Body(pou), frame)) yield return point;
    }

    private readonly Dictionary<Pou, string[]> _cycleVariables = [];

    private string[] CycleVariables(Pou pou)
    {
        if (_cycleVariables.TryGetValue(pou, out var cached)) return cached;
        var names = pou.VariablesOf(VarSectionKind.VarTemp).Select(v => v.Name).ToArray();
        _cycleVariables[pou] = names;
        return names;
    }

    private IReadOnlyList<Stmt> Body(Pou pou)
    {
        if (_bodies.TryGetValue(pou, out var cached)) return cached;
        var parsed = StParser.ParseBody(pou.Body, pou.Location.File);
        _bodies[pou] = parsed;
        return parsed;
    }

    private IEnumerable<StepPoint> ExecuteStatements(IReadOnlyList<Stmt> statements, Frame frame)
    {
        foreach (var statement in statements)
        {
            _line = statement.Line;
            frame.Line = statement.Line;
            yield return new StepPoint(frame, statement.Line);
            switch (statement)
            {
                case AssignStmt assign:
                    Assign(ResolveSlot(assign.Target, frame, assign.Line), Evaluate(assign.Value, frame), frame, assign.Line);
                    break;

                case FbCallStmt call:
                    foreach (var point in ExecuteCall(call, frame)) yield return point;
                    break;

                case IfStmt conditional:
                    IReadOnlyList<Stmt>? chosen = conditional.Else;
                    foreach (var branch in conditional.Branches)
                    {
                        if (!Truth(Evaluate(branch.Condition, frame), frame, branch.Line)) continue;
                        chosen = branch.Body;
                        break;
                    }

                    if (chosen is not null)
                        foreach (var point in ExecuteStatements(chosen, frame)) yield return point;
                    break;

                case CaseStmt choice:
                    var selector = ToLong(Evaluate(choice.Selector, frame), frame, choice.Line);
                    IReadOnlyList<Stmt>? body = choice.Else;
                    foreach (var branch in choice.Branches)
                    {
                        if (!branch.Labels.Any(l => Matches(l, selector, frame, branch.Line))) continue;
                        body = branch.Body;
                        break;
                    }

                    if (body is not null)
                        foreach (var point in ExecuteStatements(body, frame)) yield return point;
                    break;

                case ForStmt loop:
                    var counter = ResolveSlot(new PathExpr([new PathPart(loop.Variable, null)]), frame, loop.Line);
                    var first = ToLong(Evaluate(loop.From, frame), frame, loop.Line);
                    var last = ToLong(Evaluate(loop.To, frame), frame, loop.Line);
                    var step = loop.By is null ? 1 : ToLong(Evaluate(loop.By, frame), frame, loop.Line);
                    if (step == 0) throw Fault("El paso del FOR es 0.", frame, loop.Line);
                    for (var i = first; step > 0 ? i <= last : i >= last; i += step)
                    {
                        counter.Value = Coerce(i, counter.Type);
                        foreach (var point in ExecuteStatements(loop.Body, frame)) yield return point;
                        if (frame.Flow == FrameFlow.Exit) { frame.Flow = FrameFlow.None; break; }
                        if (frame.Flow == FrameFlow.Return) yield break;
                    }

                    break;

                case WhileStmt loop:
                    while (Truth(Evaluate(loop.Condition, frame), frame, loop.Line))
                    {
                        foreach (var point in ExecuteStatements(loop.Body, frame)) yield return point;
                        if (frame.Flow == FrameFlow.Exit) { frame.Flow = FrameFlow.None; break; }
                        if (frame.Flow == FrameFlow.Return) yield break;
                        frame.Line = loop.Line;
                        yield return new StepPoint(frame, loop.Line);
                    }

                    break;

                case RepeatStmt loop:
                    do
                    {
                        foreach (var point in ExecuteStatements(loop.Body, frame)) yield return point;
                        if (frame.Flow == FrameFlow.Exit) { frame.Flow = FrameFlow.None; break; }
                        if (frame.Flow == FrameFlow.Return) yield break;
                        // una parada por vuelta, como en el WHILE: un REPEAT vacío que no acaba también se puede parar (watchdog)
                        frame.Line = loop.Line;
                        yield return new StepPoint(frame, loop.Line);
                    }
                    while (!Truth(Evaluate(loop.Until, frame), frame, loop.Line));

                    break;

                case ExitStmt:
                    frame.Flow = FrameFlow.Exit;
                    yield break;

                case ReturnStmt:
                    frame.Flow = FrameFlow.Return;
                    yield break;
            }

            if (frame.Flow != FrameFlow.None) yield break;
        }
    }

    private bool Matches(CaseRange range, long selector, Frame frame, int line)
    {
        var from = ToLong(Evaluate(range.From, frame), frame, line);
        if (range.To is null) return selector == from;
        return selector >= from && selector <= ToLong(Evaluate(range.To, frame), frame, line);
    }

    /// <summary>Llamada de sentencia: a un bloque de función (se entra en su cuerpo) o a una función (se descarta su resultado).</summary>
    private IEnumerable<StepPoint> ExecuteCall(FbCallStmt call, Frame frame)
    {
        var single = call.Instance.Parts is [{ Index: null } only] ? only.Name : null;
        if (single is not null && !frame.Vars.ContainsKey(single) && !Globals.ContainsKey(single))
        {
            CallFunction(single, call.Args, frame, call.Line);
            yield break;
        }

        var slot = ResolveSlot(call.Instance, frame, call.Line);
        if (slot.Value is not FbInstance instance)
            throw Fault($"«{call.Instance}» no es un bloque de función.", frame, call.Line);

        foreach (var arg in call.Args.Where(a => !a.IsOutput))
        {
            if (arg.Name is null) throw Fault("Las llamadas a bloques llevan los parámetros por nombre.", frame, call.Line);
            if (!instance.Vars.TryGetValue(arg.Name, out var parameter))
                throw Fault($"«{instance.Type.Name}» no tiene el parámetro «{arg.Name}».", frame, call.Line);

            if (IsInOut(instance, arg.Name) && arg.Value is PathExpr path)
                instance.Vars[arg.Name] = ResolveSlot(path, frame, call.Line);   // por referencia: la misma casilla
            else
                Assign(parameter, Evaluate(arg.Value, frame), frame, call.Line);
        }

        if (instance.Type.Pou is null)
        {
            StepStandard(instance, frame);
        }
        else
        {
            var path = frame.Path is null ? null : $"{frame.Path}.{call.Instance}";
            foreach (var point in ExecuteBlock(instance, frame.Depth + 1, frame, path)) yield return point;
        }

        foreach (var arg in call.Args.Where(a => a.IsOutput))
        {
            if (!instance.Vars.TryGetValue(arg.Name!, out var output))
                throw Fault($"«{instance.Type.Name}» no tiene la salida «{arg.Name}».", frame, call.Line);
            Assign(ResolveSlot((PathExpr)arg.Value, frame, call.Line), output.Value, frame, call.Line);
        }
    }

    private static bool IsInOut(FbInstance instance, string name) =>
        instance.Type.Pou?.VariablesOf(VarSectionKind.InOut).Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == true;

    // ---------------------------------------------------------------- Nombres y casillas

    /// <summary>La casilla a la que se refiere un nombre con accesos (<c>Data.Status.Q</c>, <c>Elements[i].Max</c>).</summary>
    public Slot ResolveSlot(PathExpr path, Frame frame, int line)
    {
        var first = path.Parts[0];
        if (!frame.Vars.TryGetValue(first.Name, out var slot) && !Globals.TryGetValue(first.Name, out slot))
            throw Fault($"«{first.Name}» no existe.", frame, line);
        slot = Indexed(slot, first, frame, line);

        for (var i = 1; i < path.Parts.Count; i++)
        {
            var part = path.Parts[i];
            Slot? member = null;
            switch (slot.Value)
            {
                case StructValue structure when structure.Members.TryGetValue(part.Name, out member):
                    break;
                case FbInstance instance when instance.Vars.TryGetValue(part.Name, out member):
                    break;
                default:
                    throw Fault($"«{string.Join(".", path.Parts.Take(i))}» no tiene el miembro «{part.Name}».", frame, line);
            }

            slot = Indexed(member!, part, frame, line);
        }

        return slot;
    }

    private Slot Indexed(Slot slot, PathPart part, Frame frame, int line)
    {
        if (part.Index is null) return slot;
        if (slot.Value is not ArrayValue array) throw Fault($"«{part.Name}» no es un array.", frame, line);
        var index = ToLong(Evaluate(part.Index, frame), frame, line);
        if (index < array.Lower || index > array.Upper)
            throw Fault($"Índice {index} fuera de {part.Name}[{array.Lower}..{array.Upper}].", frame, line);
        return array.Items[index - array.Lower];
    }

    /// <summary>Asigna: las estructuras y los arrays se copian miembro a miembro; lo demás se ajusta al tipo.</summary>
    private void Assign(Slot target, object value, Frame frame, int line)
    {
        try
        {
            AssignValue(target, value);
        }
        catch (InvalidCastException)
        {
            throw Fault($"No se puede asignar «{value}» a un {target.Type}.", frame, line);
        }
    }

    private void AssignValue(Slot target, object value)
    {
        switch (target.Type.Kind)
        {
            case StTypeKind.Struct:
                var from = (StructValue)value;
                var to = (StructValue)target.Value;
                foreach (var (name, member) in to.Members) AssignValue(member, from.Members[name].Value);
                break;
            case StTypeKind.Array:
                var source = (ArrayValue)value;
                var destination = (ArrayValue)target.Value;
                for (var i = 0; i < destination.Items.Length; i++) AssignValue(destination.Items[i], source.Items[i].Value);
                break;
            case StTypeKind.FunctionBlock:
                throw new InvalidCastException();
            default:
                target.Value = Coerce(value, target.Type);
                break;
        }
    }

    /// <summary>Ajusta un valor elemental al tipo de su casilla: envuelve los enteros a su ancho, redondea al entero más cercano.</summary>
    public static object Coerce(object value, StType type)
    {
        switch (type.Kind)
        {
            case StTypeKind.Bool:
                return value switch { bool b => b, long n => n != 0, double d => d != 0, _ => throw new InvalidCastException() };
            case StTypeKind.Int:
                return Wrap(value switch
                {
                    long n => n,
                    bool b => b ? 1L : 0L,
                    double d => checked((long)Math.Round(d, MidpointRounding.ToEven)),
                    _ => throw new InvalidCastException()
                }, type);
            case StTypeKind.Time:
                return value switch { long n => n, bool b => b ? 1L : 0L, double d => (long)Math.Round(d), _ => throw new InvalidCastException() };
            case StTypeKind.Real:
                var real = value switch { double d => d, long n => (double)n, bool b => b ? 1.0 : 0.0, _ => throw new InvalidCastException() };
                return type.Is32BitReal ? (double)(float)real : real;
            case StTypeKind.String:
                return value as string ?? throw new InvalidCastException();
            default:
                return value;
        }
    }

    private static long Wrap(long value, StType type)
    {
        if (type.Bits >= 64) return value;
        var mask = (1L << type.Bits) - 1;
        var wrapped = value & mask;
        if (type.Signed && (wrapped & (1L << (type.Bits - 1))) != 0) wrapped -= 1L << type.Bits;
        return wrapped;
    }

    private int _line;

    private StRuntimeException Fault(string message, Frame frame, int line) => new(message, frame.File, line == 0 ? _line : line);

    private bool Truth(object value, Frame frame, int line) =>
        value is bool b ? b : throw Fault($"Se esperaba un BOOL y hay «{value}».", frame, line);

    private long ToLong(object value, Frame frame, int line) => value switch
    {
        long n => n,
        bool b => b ? 1 : 0,
        double d => (long)Math.Round(d, MidpointRounding.ToEven),
        _ => throw Fault($"Se esperaba un entero y hay «{value}».", frame, line)
    };

}
