using FactoryNodes.SoftNodes.Runtime;

namespace FactoryNodes.SoftNodes;

/// <summary>
/// Un bloque de la pila de llamadas en el instante de un fallo: cuál es, qué instancia (<see cref="Frame.Path"/>), en qué línea
/// estaba y una copia de sus variables (las de la instancia, con los <c>VAR_IN_OUT</c> como estaban).
/// </summary>
public sealed record FaultFrame(string Pou, string? Path, int Line, IReadOnlyDictionary<string, Slot> Variables);

/// <summary>
/// La foto de un fallo (PENDIENTES 79): la CPU pasó a STOP por un error de ejecución (un índice fuera de un array, una división por
/// cero) o por el watchdog. Se hace en el momento, antes de soltar la memoria: la pila de llamadas con las variables de cada
/// bloque y una copia de todas las globales. Después, en STOP, la memoria la siguen cambiando el cuadro, el operario o un cliente
/// OPC UA; la foto no. Vive en memoria hasta el siguiente RUN, recarga, arranque en frío o apagado.
/// </summary>
public sealed class FaultSnapshot
{
    private FaultSnapshot(string message, string file, int line, long cycle, long nowMs, IReadOnlyList<FaultFrame> frames,
        IReadOnlyDictionary<string, Slot> globals)
    {
        Message = message;
        File = file;
        Line = line;
        Cycle = cycle;
        NowMs = nowMs;
        Frames = frames;
        Globals = globals;
    }

    public string Message { get; }
    public string File { get; }
    public int Line { get; }

    /// <summary>El ciclo en el que falló (el que estaba a medias) y la hora del PLC.</summary>
    public long Cycle { get; }

    public long NowMs { get; }

    /// <summary>La pila de llamadas: el primero, el bloque donde falló; detrás, quien lo llamó, hasta lo más alto del ciclo.</summary>
    public IReadOnlyList<FaultFrame> Frames { get; }

    /// <summary>Copia de las globales del programa (<c>hw_</c>, <c>io_</c> y cada variable por su nombre).</summary>
    public IReadOnlyDictionary<string, Slot> Globals { get; }

    /// <summary>
    /// Hace la foto. <paramref name="at"/>: la sentencia en la que estaba el ciclo (la que falló); sin ella (el fallo saltó antes de la
    /// primera), solo las globales.
    /// </summary>
    public static FaultSnapshot Capture(StRuntimeException fault, StepPoint? at, SoftProgram program, long cycle)
    {
        // Una sola tabla de copias para todo: un VAR_IN_OUT y la global que apunta son la misma casilla en la foto
        var copies = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        var globals = Copy(program.Globals, copies);
        var frames = new List<FaultFrame>();
        for (var frame = at?.Frame; frame is not null; frame = frame.Parent)
            frames.Add(new FaultFrame(frame.Name, frame.Path, frame == at!.Value.Frame ? at.Value.Line : frame.Line, Copy(frame.Vars, copies)));

        return new FaultSnapshot(fault.InnerMessage(), fault.File, fault.Line, cycle, program.NowMs, frames, globals);
    }

    private static Dictionary<string, Slot> Copy(Dictionary<string, Slot> vars, Dictionary<object, object> copies) =>
        vars.ToDictionary(v => v.Key, v => Copy(v.Value, copies), StringComparer.OrdinalIgnoreCase);

    private static Slot Copy(Slot slot, Dictionary<object, object> copies)
    {
        if (copies.TryGetValue(slot, out var known)) return (Slot)known;
        var copy = new Slot(slot.Type, slot.Value);
        copies[slot] = copy;
        copy.Value = slot.Value switch
        {
            StructValue s => CopyStruct(s, copies),
            ArrayValue a => new ArrayValue(a.Lower, a.Items.Select(i => Copy(i, copies)).ToArray()),
            FbInstance f => CopyInstance(f, copies),
            var elementary => elementary
        };
        return copy;
    }

    private static StructValue CopyStruct(StructValue value, Dictionary<object, object> copies)
    {
        var copy = new StructValue();
        foreach (var (name, member) in value.Members) copy.Members[name] = Copy(member, copies);
        return copy;
    }

    private static FbInstance CopyInstance(FbInstance value, Dictionary<object, object> copies)
    {
        var copy = new FbInstance(value.Type);
        foreach (var (name, member) in value.Vars) copy.Vars[name] = Copy(member, copies);
        return copy;
    }
}
