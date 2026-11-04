using System.Globalization;
using System.Text.RegularExpressions;
using FactoryNodes.SoftNodes.Runtime;
using FactoryNodes.SoftNodes.Syntax;

namespace FactoryNodes.SoftNodes;

/// <summary>Una variable y su valor en el momento de parar (<c>Data.Status.Active_Q</c> = <c>TRUE</c>).</summary>
public sealed record WatchValue(string Path, string Type, string Text);

/// <summary>
/// Un punto de parada: bloque (<c>Motor_Block</c>), línea del fichero y, si se da, la instancia en la que para (su camino: <c>M77</c>;
/// en un bloque llamado dentro de otro, el de fuera: <c>M77</c> para el AlarmManager de M77). Null = en todas.
/// </summary>
public sealed record Breakpoint(string Pou, int Line, string? Instance = null)
{
    public static implicit operator Breakpoint((string Pou, int Line) point) => new(point.Pou, point.Line);

    /// <summary>¿Para aquí? El mismo bloque y línea, y la instancia si la lleva.</summary>
    public bool Matches(StepPoint point) =>
        point.Line == Line && point.Frame.Name.Equals(Pou, StringComparison.OrdinalIgnoreCase) &&
        (Instance is null || point.Frame.Path is { } path &&
            (path.Equals(Instance, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Instance + ".", StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// El paso a paso (solo lectura: nunca cambia el programa; PENDIENTES 49 y 78). Con la CPU en STOP, <see cref="Begin"/> prepara un
/// ciclo y se detiene antes de la primera sentencia; si la CPU paró a mitad de un ciclo en RUN (punto de parada o pausa),
/// <see cref="Attach"/> lo recoge. Cada orden ejecuta y para antes de la sentencia siguiente: <see cref="StepOver"/> («Paso»: la
/// sentencia entera), <see cref="StepInto"/> (entra en el bloque llamado), <see cref="StepOut"/> (hasta salir del bloque),
/// <see cref="Continue"/> (hasta un punto de parada o el final del ciclo), <see cref="RunToCursor"/> y <see cref="Cycle"/>.
/// Al terminar el ciclo se hacen las salidas físicas y lo de fuera del programa (la planta), como en uno normal. Un fallo de
/// ejecución deja la CPU en STOP con su foto (<see cref="SoftInstance.ReportFault"/>), como en RUN.
/// </summary>
public sealed class StepDebugger
{
    /// <summary>Sentencias que puede ejecutar de golpe una orden (Continuar, Ciclo…) antes de rendirse: el watchdog del paso a paso.</summary>
    public const int StepBudget = 2_000_000;

    private readonly SoftInstance _instance;
    private IEnumerator<StepPoint>? _steps;
    private Breakpoint[] _snapshot = [];

    public StepDebugger(SoftInstance instance) => _instance = instance;

    /// <summary>Los puntos de parada. Tras cambiarlos, <see cref="BreakpointsChanged"/> los pasa a la CPU para que paren también en RUN.</summary>
    public HashSet<Breakpoint> Breakpoints { get; } = [];

    /// <summary>Los puntos de parada cambiaron: la CPU en RUN para en ellos (<see cref="SoftInstance.BreakAt"/>; sin ninguno, no pregunta).</summary>
    public void BreakpointsChanged()
    {
        _snapshot = [.. Breakpoints];
        var points = _snapshot;
        _instance.BreakAt = points.Length == 0 ? null : point => Array.Exists(points, b => b.Matches(point));
    }

    /// <summary>Hay un ciclo a medias.</summary>
    public bool IsActive => _steps is not null;

    /// <summary>La sentencia que se ejecutará en el próximo paso; null sin ciclo a medias.</summary>
    public StepPoint? Current { get; private set; }

    /// <summary>Sentencias ejecutadas en el ciclo en curso.</summary>
    public int Steps { get; private set; }

    /// <summary>Terminó el ciclo (no hay más sentencias); un nuevo <see cref="Begin"/> empieza otro.</summary>
    public event EventHandler? CycleCompleted;

    /// <summary>Lo que hizo parar la última orden de varios pasos, para decirlo («punto de parada en …», «fallo: …»); vacío si nada especial.</summary>
    public string LastStop { get; private set; } = "";

    /// <summary>Empieza un ciclo y para en su primera sentencia. Con la CPU en RUN no se puede.</summary>
    public void Begin()
    {
        if (_instance.Mode == SoftMode.Running) throw new InvalidOperationException("Para el PLC (STOP) antes de ir paso a paso.");
        Abandon();
        _instance.PrepareSteppedCycle();
        _steps = _instance.Program.ScanSteps(_instance.CycleMs).GetEnumerator();
        Steps = 0;
        Advance();
    }

    /// <summary>Recoge el ciclo que la CPU dejó a medias al parar en RUN (<see cref="SoftInstance.Paused"/>).</summary>
    public void Attach(IEnumerator<StepPoint> steps, StepPoint at)
    {
        Abandon();
        _steps = steps;
        Current = at;
        Steps = 0;
    }

    /// <summary>Deja el ciclo a medias sin terminarlo (la memoria queda como está).</summary>
    public void Abandon()
    {
        _steps?.Dispose();
        _steps = null;
        Current = null;
    }

    /// <summary>«Entrar»: ejecuta la sentencia actual; si es una llamada a un bloque, entra en él.</summary>
    public void StepInto()
    {
        if (_steps is null) return;
        LastStop = "";
        Steps++;
        Advance();
    }

    /// <summary>«Paso»: ejecuta la sentencia actual entera (una llamada a un bloque, con todo su cuerpo) y para en la siguiente.</summary>
    public void StepOver()
    {
        if (Current is not { } start) return;
        var depth = start.Frame.Depth;
        RunUntil(now => now.Frame.Depth <= depth);
    }

    /// <summary>«Salir del bloque»: ejecuta hasta volver al bloque que llamó a este y espera allí.</summary>
    public void StepOut()
    {
        if (Current is not { } start) return;
        var depth = start.Frame.Depth;
        RunUntil(now => now.Frame.Depth < depth);
    }

    /// <summary>Hasta el siguiente punto de parada o el final del ciclo.</summary>
    public void Continue()
    {
        if (_steps is null) return;
        RunUntil(_ => false);
    }

    /// <summary>
    /// «Ejecutar hasta el cursor»: ciclo tras ciclo hasta llegar a esa línea de ese bloque (en esa instancia, si se da) o a un punto
    /// de parada. True si llegó; si en <paramref name="maxCycles"/> ciclos no llega, se queda al principio del siguiente.
    /// </summary>
    public bool RunToCursor(string pou, int line, string? instance = null, int maxCycles = 200)
    {
        var target = new Breakpoint(pou, line, instance);
        for (var cycle = 0; cycle < maxCycles; cycle++)
        {
            if (_steps is null) Begin();
            if (_steps is null) return false;   // falló al empezar
            if (cycle > 0 && Current is { } first && target.Matches(first)) return true;
            if (RunUntil(target.Matches)) return Current is { } here && target.Matches(here);   // llegó, o un punto de parada antes
            if (_instance.Fault is not null) return false;
        }

        Begin();
        LastStop = $"no se llegó a la línea {line} en {maxCycles} ciclos";
        return false;
    }

    /// <summary>
    /// «Ciclo»: desde donde está el puntero, termina este ciclo, empieza el siguiente y para al volver a la misma sentencia. Si en el
    /// ciclo siguiente esa sentencia no se ejecuta (un IF que ya no se cumple, una etapa que ya no está activa), para en la primera que
    /// se ejecute después de ese punto (autor, 2026-10-20). Un punto de parada por el camino también para.
    /// </summary>
    public void Cycle()
    {
        if (Current is not { } start)
        {
            Begin();
            return;
        }

        var program = _instance.Program;
        var index = program.RunIndexOf(start.Frame.Path);
        var path = start.Frame.Path;

        // lo que queda de este ciclo (un punto de parada o un fallo paran aquí)
        if (RunUntil(_ => false) || _instance.Fault is not null) return;

        // y el siguiente hasta el mismo sitio, o el primero que se ejecute detrás
        bool AtOrAfter(StepPoint now)
        {
            if (index < 0) return now.Frame.Name == start.Frame.Name && now.Line == start.Line;
            var i = program.RunIndexOf(now.Frame.Path);
            if (i != index) return i > index;
            return string.Equals(now.Frame.Path, path, StringComparison.OrdinalIgnoreCase) && now.Frame.Name == start.Frame.Name && now.Line >= start.Line;
        }

        Begin();
        if (_steps is null || Current is { } first && AtOrAfter(first)) return;
        if (RunUntil(AtOrAfter) || _instance.Fault is not null) return;

        // no había nada detrás en este ciclo: al principio del siguiente
        Begin();
        LastStop = "la sentencia no se ejecutó en el ciclo siguiente: parado al principio del otro";
    }

    /// <summary>
    /// Avanza hasta que <paramref name="stop"/> se cumpla en la sentencia siguiente o haya un punto de parada (true), o el ciclo termine
    /// o falle (false). Si tarda más que el watchdog, la CPU pasa a STOP con su foto, como en RUN.
    /// </summary>
    private bool RunUntil(Func<StepPoint, bool> stop)
    {
        LastStop = "";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var n = 1; _steps is not null; n++)
        {
            Steps++;
            if (!Advance()) return false;
            var now = Current!.Value;
            if (stop(now)) return true;
            if (Breakpoints.Any(b => b.Matches(now)))
            {
                LastStop = $"punto de parada en {now.Frame.Name}, línea {now.Line}";
                return true;
            }

            if (n > StepBudget || (n & 1023) == 0 && clock.ElapsedMilliseconds > _instance.WatchdogMs)
            {
                Abandon();
                _instance.ReportFault(new StRuntimeException(
                    $"Watchdog: más de {_instance.WatchdogMs} ms sin terminar el ciclo (¿un WHILE o un REPEAT que no acaba?).", now.Frame.File, now.Line), now);
                LastStop = "watchdog";
                return false;
            }
        }

        return false;
    }

    /// <summary>Un paso del ciclo. False si terminó (salidas hechas, planta, ciclo contado) o falló (CPU en STOP con la foto).</summary>
    private bool Advance()
    {
        bool more;
        try
        {
            lock (_instance.Program.Sync) more = _steps!.MoveNext();
        }
        catch (StRuntimeException e)
        {
            var at = Current;
            Abandon();
            _instance.ReportFault(e, at);
            LastStop = "fallo: " + e.InnerMessage();
            return false;
        }

        if (more)
        {
            Current = _steps!.Current;
            return true;
        }

        Abandon();
        _instance.CompleteSteppedCycle();
        CycleCompleted?.Invoke(this, EventArgs.Empty);
        return false;
    }

    // ---------------------------------------------------------------- Valores

    /// <summary>
    /// Las variables que nombra una línea de código con su valor ahora, en el marco que está parado
    /// (<c>Data.Control.Run_Conditions</c>, <c>tStop.Q</c>, <c>Phase</c>); lo que no se puede leer (un parámetro de una
    /// llamada, una palabra clave) no sale. Es la columna de valores junto al código.
    /// </summary>
    public IReadOnlyList<WatchValue> ValuesOf(string codeLine, Frame frame)
    {
        var values = new List<WatchValue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PathPattern.Matches(Mask(codeLine)))
        {
            var text = Compact(match.Value);
            if (Reserved.Contains(text) || !seen.Add(text)) continue;
            if (Read(text, frame) is { } value) values.Add(value);
        }

        return values;
    }

    /// <summary>
    /// El camino de la variable que hay en la columna <paramref name="column"/> (desde 0) de una línea, hasta el miembro señalado: en
    /// <c>Data.Control.Run</c> con el ratón sobre <c>Control</c>, <c>Data.Control</c>. Null si ahí no hay un nombre.
    /// </summary>
    public static string? PathAt(string codeLine, int column)
    {
        var masked = Mask(codeLine);
        if (column < 0 || column >= masked.Length || !IsNameChar(masked[column])) return null;
        foreach (Match match in PathPattern.Matches(masked))
        {
            if (column < match.Index || column >= match.Index + match.Length) continue;
            var end = column;
            var last = match.Index + match.Length;
            while (end < last && IsNameChar(masked[end])) end++;
            var depth = 0;
            for (var i = match.Index; i < end; i++) depth += masked[i] == '[' ? 1 : masked[i] == ']' ? -1 : 0;
            for (; depth > 0 && end < last; end++) depth += masked[end] == '[' ? 1 : masked[end] == ']' ? -1 : 0;
            var path = Compact(masked[match.Index..end]);
            return Reserved.Contains(path) ? null : path;
        }

        return null;
    }

    /// <summary>
    /// Lo que se enseña al pasar el ratón por una variable: <c>Data.Control.Run = TRUE (BOOL)</c>; una estructura, una instancia o un
    /// array, con sus miembros de primer nivel debajo (como mucho <paramref name="maxMembers"/>). Null si no se puede leer.
    /// </summary>
    public string? Describe(string path, Frame frame, int maxMembers = 40)
    {
        Slot slot;
        try
        {
            if (StParser.ParseExpression(path) is not PathExpr p) return null;
            slot = _instance.Program.Interpreter.ResolveSlot(p, frame, 0);
        }
        catch (Exception e) when (e is StRuntimeException or StSyntaxException)
        {
            return null;
        }

        IEnumerable<(string Name, Slot Slot)> members = slot.Value switch
        {
            StructValue s => s.Members.Select(m => (m.Key, m.Value)),
            FbInstance f => f.Vars.Select(v => (v.Key, v.Value)),
            ArrayValue a => a.Items.Select((item, i) => ($"[{a.Lower + i}]", item)),
            _ => []
        };
        var list = members.ToList();
        if (list.Count == 0 && slot.Value is not (StructValue or FbInstance or ArrayValue)) return $"{path} = {Format(slot)} ({slot.Type})";

        var lines = new List<string> { $"{path} ({slot.Type})" };
        foreach (var (name, member) in list.Take(maxMembers))
            lines.Add($"    {name} = " + (member.Value is StructValue or FbInstance or ArrayValue ? $"({member.Type})" : Format(member)));
        if (list.Count > maxMembers) lines.Add($"    … {list.Count - maxMembers} más");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// La instancia de bloque que nombra un camino (Ctrl+clic: <c>CT01_FB</c>, <c>modes</c>), en el marco dado o, sin él, entre las
    /// globales del programa. Null si no es una instancia.
    /// </summary>
    public FbInstance? InstanceAt(string path, Frame? frame)
    {
        try
        {
            if (frame is null) return _instance.Program.Find(path).Value as FbInstance;
            return StParser.ParseExpression(path) is PathExpr p ? _instance.Program.Interpreter.ResolveSlot(p, frame, 0).Value as FbInstance : null;
        }
        catch (Exception e) when (e is StRuntimeException or StSyntaxException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>Una variable por su camino, en el marco dado (o null si no existe o no es un valor elemental).</summary>
    public WatchValue? Read(string path, Frame frame)
    {
        try
        {
            var expr = StParser.ParseExpression(path);
            if (expr is not PathExpr p) return null;
            var slot = _instance.Program.Interpreter.ResolveSlot(p, frame, 0);
            return slot.Value is StructValue or ArrayValue or FbInstance ? null : new WatchValue(path, slot.Type.ToString(), Format(slot));
        }
        catch (Exception e) when (e is StRuntimeException or StSyntaxException)
        {
            return null;
        }
    }

    public static string Format(Slot slot) => slot.Value switch
    {
        bool b => b ? "TRUE" : "FALSE",
        long n when slot.Type.Kind == StTypeKind.Time => $"T#{n}ms",
        long n when slot.Type.Kind == StTypeKind.Int && slot.Type.Name is "WORD" or "DWORD" or "BYTE" or "LWORD" =>
            $"16#{n.ToString("X" + slot.Type.Bits / 4, CultureInfo.InvariantCulture)}",
        long n => n.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        string s => $"'{s}'",
        var other => other?.ToString() ?? ""
    };

    private static readonly Regex PathPattern = new(@"(?<![A-Za-z0-9_#.])[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*|\s*\[[^\]\r\n]*\])*", RegexOptions.Compiled);

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "IF", "THEN", "ELSIF", "ELSE", "END_IF", "CASE", "OF", "END_CASE", "FOR", "TO", "BY", "DO", "END_FOR", "WHILE", "END_WHILE",
        "REPEAT", "UNTIL", "END_REPEAT", "EXIT", "RETURN", "AND", "OR", "XOR", "NOT", "MOD", "TRUE", "FALSE"
    };

    /// <summary>
    /// La línea con lo que no son nombres en blanco, sin mover las columnas: los comentarios y las cadenas (<c>'TRUE'</c>), y lo que
    /// en SCL de TIA rodea un nombre (<c>"hw_P".CT01</c>, <c>#CT01_FB</c>), que SoftNodes tiene sin comillas ni <c>#</c>.
    /// </summary>
    private static string Mask(string line)
    {
        var text = Regex.Replace(line, @"\(\*.*?\*\)|'[^']*'|//.*$", m => new string(' ', m.Length));
        text = Regex.Replace(text, @"""([A-Za-z_][A-Za-z0-9_]*)""", " $1 ");
        return Regex.Replace(text, @"(?<![A-Za-z0-9_])#(?=[A-Za-z_])", " ");
    }

    /// <summary>El camino sin los blancos que deja <see cref="Mask"/> (<c>hw_P .CT01</c> → <c>hw_P.CT01</c>).</summary>
    private static string Compact(string path) => Regex.Replace(path, @"\s+", "");

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
