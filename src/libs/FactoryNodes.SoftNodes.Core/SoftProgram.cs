using FactoryNodes.Iec61131.Emission;
using FactoryNodes.Iec61131.Model;
using FactoryNodes.SoftNodes.Runtime;
using FactoryNodes.SoftNodes.Syntax;

namespace FactoryNodes.SoftNodes;

/// <summary>
/// El programa de un proyecto cargado en el intérprete: los datos <c>hw_&lt;P&gt;</c> y las señales <c>io_&lt;P&gt;</c>, y en
/// cada ciclo lo mismo que escriben los emisores en sus secciones: por cada elemento su bloqueo, su orden, el mapeo de
/// señales y la llamada a su bloque; al final, las salidas físicas. Se construye del <see cref="ProgramModel"/> (la forma
/// neutra que ya usan TIA y Sysmac), no de su texto.
/// </summary>
/// <summary>
/// Una cosa del ciclo: su sección del PLC (<c>fnc010_Topology</c>), su nombre (<c>Topo_CargaZ2</c>) y el bloque que ejecuta.
/// <paramref name="IsInsert"/>: el código del usuario al principio o al final de una sección generada (<c>User_Motors_Start</c>, PENDIENTES 88).
/// </summary>
public sealed record SoftRun(string Section, string Name, string Block, bool IsUserSection, bool IsInsert = false);

public sealed class SoftProgram
{
    private readonly StInterpreter _interpreter;
    private readonly ProgramModel _program;
    private readonly List<ElementRun> _elements = [];

    /// <summary>
    /// Lo que se ejecuta en un ciclo, en orden: elementos (<see cref="ElementRun"/>), secciones de usuario (su instancia) y el código del
    /// usuario de las secciones generadas (<see cref="InsertRun"/>).
    /// </summary>
    private readonly List<object> _order = [];

    /// <summary>Las secciones en el orden del ciclo, por su nombre completo, con la de salidas físicas al final.</summary>
    private readonly List<string> _sectionNames = [];

    private readonly Dictionary<string, FbInstance> _userSections = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>El nombre de cada cosa de <see cref="_order"/>: el equipo o loop (<c>M77</c>) o la sección (<c>User_Inicio</c>).</summary>
    private readonly List<string> _runNames = [];
    private readonly List<SoftRun> _runs = [];
    private readonly List<Action> _outputs = [];
    private readonly Frame _outputFrame;
    private int _outputFirstLine = 1;
    private InsertRun? _outputStart, _outputEnd;
    private readonly Frame _frame;
    private readonly Slot _debugSim;

    /// <summary>El reloj de 1 s para las expresiones del programa (<see cref="ProgramValue.Clock"/>: el parpadeo del piloto de alarma).</summary>
    private readonly Slot _clock = new(StType.Bool, false);

    /// <summary>Su nombre en SoftNodes (en el PLC, <c>"Clock_1Hz"</c> o <c>Get1sClk()</c>).</summary>
    public const string ClockName = "SoftClock_1s";

    /// <summary>Una asignación de la sección; <paramref name="Line"/>: su número de sentencia en la sección (<see cref="SectionListing"/>).</summary>
    /// <param name="When">Solo se escribe mientras da TRUE (una orden de loop de escribir un valor en su tramo); null = siempre.</param>
    private sealed record Assignment(Slot Target, Func<object> Value, int Line, Func<object>? When = null);

    private sealed class ElementRun(ProgramElement element, FbInstance instance, Slot data, List<Assignment> assignments,
        List<(string Parameter, Slot Slot)> companions, Frame section, int callLine)
    {
        public ProgramElement Element { get; } = element;
        public FbInstance Instance { get; } = instance;
        public Slot Data { get; } = data;
        public List<Assignment> Assignments { get; } = assignments;
        public List<(string Parameter, Slot Slot)> Companions { get; } = companions;

        /// <summary>El marco de la sección mientras ejecuta este elemento (nombre = la sección, camino = el elemento, profundidad 0).</summary>
        public Frame Section { get; } = section;

        /// <summary>El número de sentencia de la llamada al bloque en la sección.</summary>
        public int CallLine { get; } = callLine;
    }

    /// <summary>
    /// El código del usuario al principio o al final de una sección generada (PENDIENTES 88): la instancia de su bloque
    /// (<c>FB_User_Motors_Start</c>), que se llama desde una sentencia de la sección (<paramref name="line"/>), como un elemento.
    /// </summary>
    private sealed class InsertRun(string name, FbInstance instance, Frame section, int line)
    {
        public string Name { get; } = name;
        public FbInstance Instance { get; } = instance;
        public Frame Section { get; } = section;
        public int Line { get; } = line;
    }

    public SoftProgram(MasterLibrary library, ProgramModel program)
    {
        _program = program;
        _interpreter = new StInterpreter(library);
        _frame = new Frame("Program", program.ProjectName, new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase), 0);

        Io = new StructValue();
        Data = new StructValue();
        var structType = new StType { Kind = StTypeKind.Struct, Name = "Program" };
        Globals[program.IoName] = new Slot(structType, Io);
        Globals[program.DataName] = new Slot(structType, Data);

        foreach (var variable in program.IoVariables) Add(Io, variable.Name, _interpreter.TypeOf(variable.DataType));
        _debugSim = Add(Data, "DebugSim", StType.Bool);
        Globals[ClockName] = _clock;

        foreach (var variable in program.DataVariables)
            Add(Data, variable.Name, _interpreter.TypeOf(variable.DataType));

        // Los enlaces con otros autómatas (PENDIENTES 167): lo que devuelve la tarea de comunicación en el PLC. SoftNodes todavía no ejecuta el
        // enlace, así que quedan a 0 y el bloque EipLink da el enlace por caído (en Debug, por conectado).
        foreach (var (name, type) in program.Links.SelectMany(l => l.Variables).Where(v => !Globals.ContainsKey(v.Name)))
            Globals[name] = new Slot(_interpreter.TypeOf(type), _interpreter.NewValue(_interpreter.TypeOf(type)));

        // Las constantes de etapa son globales en el PLC (Sysmac): el código de las secciones de usuario las nombra
        foreach (var constant in program.Constants.Where(c => !Globals.ContainsKey(c.Name)))
        {
            var type = _interpreter.TypeOf(constant.DataType);
            Globals[constant.Name] = new Slot(type, StInterpreter.Coerce(Constant(constant.Value), type));
        }

        foreach (var section in program.Sections)
        {
            if (section.User is { } user)
            {
                // Sección de usuario (módulo IEC): su bloque FB_User_<Nombre> viene en la librería del paquete
                var instance = _interpreter.NewInstance(user.FunctionBlock);
                _sectionNames.Add(section.FullName);
                _userSections[section.Name] = instance;
                _order.Add(instance);
                _runNames.Add(section.Name);
                _runs.Add(new SoftRun(section.FullName, section.Name, user.FunctionBlock, IsUserSection: true));
                continue;
            }

            _sectionNames.Add(section.FullName);
            var statement = 0;
            if (section.Prologue is { } prologue) AddInsert(section, prologue, ++statement);
            foreach (var element in section.Elements)
            {
                var run = Build(section, element, ref statement);
                _elements.Add(run);
                _order.Add(run);
                _runNames.Add(element.Name);
                _runs.Add(new SoftRun(section.FullName, element.Name, run.Instance.Type.Name, IsUserSection: false));
                // El código del usuario de las secciones nombra sus instancias (CT01_FB): SoftNodes las busca entre sus globales; sus
                // nombres no se repiten en todo el programa
                Globals.TryAdd(element.Instance, new Slot(run.Instance.Type, run.Instance));
            }

            if (section.Epilogue is { } epilogue) AddInsert(section, epilogue, ++statement);
        }

        // Las salidas físicas: también una sección del paso a paso (cada salida es una sentencia), con su código del usuario
        var outputs = program.OutputSection;
        _sectionNames.Add(outputs.FullName);
        _outputFrame = new Frame(outputs.FullName, outputs.FullName, new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase), 0);
        if (outputs.Prologue is { } outputStart)
        {
            _outputStart = NewInsert(outputs, outputStart, 1);
            _outputFirstLine = 2;
        }

        foreach (var output in program.Outputs) _outputs.Add(BuildOutput(output));
        if (outputs.Epilogue is { } outputEnd) _outputEnd = NewInsert(outputs, outputEnd, _outputFirstLine + _outputs.Count);
    }

    /// <summary>El código del usuario al principio o al final de una sección (<c>User_Motors_Start</c>): su instancia y su sentencia.</summary>
    private InsertRun NewInsert(ProgramSection section, ProgramUserCode user, int line)
    {
        var name = user.FunctionBlock.StartsWith("FB_", StringComparison.Ordinal) ? user.FunctionBlock[3..] : user.FunctionBlock;
        var frame = new Frame(section.FullName, section.FullName, new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase), 0, path: name);
        var run = new InsertRun(name, _interpreter.NewInstance(user.FunctionBlock), frame, line);
        _runNames.Add(name);
        _runs.Add(new SoftRun(section.FullName, name, user.FunctionBlock, IsUserSection: false, IsInsert: true));
        return run;
    }

    private void AddInsert(ProgramSection section, ProgramUserCode user, int line) => _order.Add(NewInsert(section, user, line));

    /// <summary>Las secciones en el orden del ciclo, por su nombre completo (<c>fnc050_Motors</c>), con la de salidas físicas al final.</summary>
    public IReadOnlyList<string> SectionNames => _sectionNames;

    /// <summary>
    /// Lo que se ejecuta en un ciclo, en orden, por el nombre con que empieza <see cref="Frame.Path"/>: cada equipo o loop
    /// (<c>M77</c>, <c>Loop_Carro</c>) y cada sección de usuario (<c>User_Inicio</c>).
    /// </summary>
    public IReadOnlyList<string> RunNames => _runNames;

    /// <summary>
    /// Lo mismo que <see cref="RunNames"/> con su sección del PLC y su bloque (<c>fnc050_Motors</c> · <c>CCS1_105</c> · <c>Motor_Block</c>), en
    /// el orden del ciclo: para que el depurador enseñe el programa como en TIA o en Sysmac.
    /// </summary>
    public IReadOnlyList<SoftRun> Runs => _runs;

    /// <summary>La posición en el ciclo de un camino de instancia (<c>M77.alarms</c> → la de <c>M77</c>), o -1.</summary>
    public int RunIndexOf(string? path)
    {
        if (path is null) return -1;
        var dot = path.IndexOf('.');
        var root = dot < 0 ? path : path[..dot];
        return _runNames.FindIndex(n => n.Equals(root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Las instancias de un bloque que se ejecutan en el ciclo (las de lo más alto: un equipo, un loop o una sección), por su camino:
    /// para elegir en cuál para un punto de parada. Las de dentro de otro bloque (el AlarmManager de cada motor) no salen.
    /// </summary>
    public IReadOnlyList<string> InstancesOfBlock(string block)
    {
        var list = new List<string>();
        for (var i = 0; i < _order.Count; i++)
        {
            var type = _order[i] switch
            {
                FbInstance user => user.Type.Name,
                InsertRun insert => insert.Instance.Type.Name,
                _ => ((ElementRun)_order[i]).Instance.Type.Name
            };
            if (type.Equals(block, StringComparison.OrdinalIgnoreCase)) list.Add(_runNames[i]);
        }

        return list;
    }

    /// <summary>
    /// La instancia del bloque de algo del ciclo por su nombre (<see cref="RunNames"/>): un equipo o loop, una sección de usuario o el código
    /// del usuario de una sección (<c>User_Motors_End</c>); null si no hay.
    /// </summary>
    public FbInstance? InstanceOfRun(string name)
    {
        var index = _runNames.FindIndex(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index >= _order.Count) return new[] { _outputStart, _outputEnd }.FirstOrDefault(r => r?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true)?.Instance;
        return _order[index] switch
        {
            FbInstance user => user,
            InsertRun insert => insert.Instance,
            ElementRun element => element.Instance,
            _ => null
        };
    }

    /// <summary>La instancia del bloque de una sección de usuario (<c>User_Inicio</c>), o null.</summary>
    public FbInstance? UserSectionInstance(string sectionName) => _userSections.GetValueOrDefault(sectionName);

    /// <summary>Las secciones de usuario por su nombre (<c>User_Inicio</c>) con la instancia de su bloque.</summary>
    public IReadOnlyDictionary<string, FbInstance> UserSections => _userSections;

    /// <summary>El programa neutro del que se construyó (para enseñar el código de una sección como lo escribe cada emisor).</summary>
    public ProgramModel Model => _program;

    /// <summary>Nombre de la variable de datos (<c>hw_P</c>).</summary>
    public string DataName => _program.DataName;

    /// <summary>Nombre de las señales físicas (<c>io_P</c>).</summary>
    public string IoName => _program.IoName;

    /// <summary>Datos del proyecto (<c>hw_P</c>): un miembro por elemento.</summary>
    public StructValue Data { get; }

    /// <summary>Señales físicas (<c>io_P</c>).</summary>
    public StructValue Io { get; }

    public StInterpreter Interpreter => _interpreter;

    /// <summary>
    /// Candado de la memoria: el ciclo (<see cref="Scan"/>) y quien lee o escribe desde fuera (el servidor OPC UA, otro
    /// hilo) lo toman, así que nadie ve una escritura a medias. El paso a paso (<see cref="ScanSteps"/>) lo maneja quien lo usa.
    /// </summary>
    public object Sync { get; } = new();

    public Dictionary<string, Slot> Globals => _interpreter.Globals;

    /// <summary>Instante del reloj del PLC, en milisegundos.</summary>
    public long NowMs => _interpreter.NowMs;

    /// <summary>La casilla de una variable global por su camino (<c>Topo_Planta_T1.Ctrl.Request.Sources[1]</c>, <c>io_P.Q_Lamp</c>, <c>hw_P.Lamp1.Status.Active_Q</c>).</summary>
    public Slot Find(string path) => _interpreter.ResolveSlot((PathExpr)StParser.ParseExpression(path, _program.ProjectName), _frame, 0);

    /// <summary>
    /// Una expresión ST sobre las variables del programa (<c>Topo_Planta_T1.Status.State = 50</c>) lista para evaluar tantas
    /// veces como se quiera; quien la usa debe tener tomado <see cref="Sync"/>. Falla ahora si no se entiende, no al evaluarla.
    /// </summary>
    public Func<object> Compile(string expression)
    {
        var parsed = StParser.ParseExpression(expression, _program.ProjectName);
        return () => _interpreter.Evaluate(parsed, _frame);
    }

    /// <summary>La instancia del bloque de un elemento (para ver su estado interno), o null.</summary>
    public FbInstance? InstanceOf(string elementName) =>
        _elements.FirstOrDefault(e => e.Element.Name.Equals(elementName, StringComparison.OrdinalIgnoreCase))?.Instance;

    private Slot Add(StructValue owner, string name, StType type)
    {
        var slot = new Slot(type, _interpreter.NewValue(type));
        owner.Members[name] = slot;
        Globals[name] = slot;
        return slot;
    }

    /// <param name="statement">El último número de sentencia de la sección (<see cref="SectionListing"/>): avanza con cada una de este elemento.</param>
    private ElementRun Build(ProgramSection section, ProgramElement element, ref int statement)
    {
        var data = Data.Members[element.Name];
        foreach (var initial in element.InitialValues)
        {
            var target = Resolve(data, initial.Path);
            target.Value = StInterpreter.Coerce(Constant(initial.Literal), target.Type);
        }

        var assignments = new List<Assignment>();
        foreach (var assignment in new[] { element.Lock, element.Run }.Concat(element.Mapping))
        {
            statement++;   // las comentadas no se ejecutan, pero cuentan (salen en el código de la sección)
            if (assignment.Commented) continue;
            var owner = assignment.Variable is { } companion ? Data.Members[companion] : data;
            assignments.Add(new Assignment(Resolve(owner, assignment.Target), ValueOf(assignment.Value, data), statement,
                assignment.When is { } when ? ValueOf(when, data) : null));
        }

        var instance = _interpreter.NewInstance(element.FunctionBlock);
        var companions = element.Companions.Select(c => (c.Parameter, Data.Members[c.Name])).ToList();
        var frame = new Frame(section.FullName, section.FullName, new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase), 0, path: element.Name);
        return new ElementRun(element, instance, data, assignments, companions, frame, ++statement);
    }

    private Func<object> ValueOf(ProgramValue value, Slot owner)
    {
        Func<object> read = value.Kind switch
        {
            ProgramValueKind.IoVariable => ReadSlot(Io.Members[value.Text]),
            ProgramValueKind.OwnMember => ReadSlot(Resolve(owner, value.Text)),
            ProgramValueKind.Expression => Expression(value.Resolve(name => name, (_, literal) => literal, ClockName)),
            _ => Expression(value.Text)
        };

        return value.Not ? () => !(bool)read() : read;
    }

    private static Func<object> ReadSlot(Slot slot) => () => slot.Value;

    private Func<object> Expression(string text)
    {
        var expr = StParser.ParseExpression(text, _program.ProjectName);
        return () => _interpreter.Evaluate(expr, _frame);
    }

    private object Constant(string literal) => _interpreter.Evaluate(StParser.ParseExpression(literal), _frame);

    /// <summary>La casilla de un camino relativo a una variable (<c>Status.Active_Q</c>, <c>Elements[1].Run</c>).</summary>
    private Slot Resolve(Slot owner, string path)
    {
        var expr = (PathExpr)StParser.ParseExpression("Owner__." + path, _program.ProjectName);
        var frame = new Frame("Program", _program.ProjectName, new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase) { ["Owner__"] = owner }, 0);
        return _interpreter.ResolveSlot(expr, frame, 0);
    }

    private Action BuildOutput(ProgramOutput output)
    {
        var target = Io.Members[output.Physical];
        var source = Resolve(Data.Members[output.Element], output.Member);
        return () => target.Value = output.Not ? !(bool)source.Value : source.Value;
    }

    // ---------------------------------------------------------------- Ciclo

    /// <summary>
    /// Avanza el reloj sin ejecutar el programa (con el reloj de 1 Hz): para la planta de un PLC externo (banco de pruebas, PENDIENTES 110),
    /// donde esta memoria es un espejo del PLC real y solo corre la planta. Quien llama tiene tomado <see cref="Sync"/>.
    /// </summary>
    public void AdvanceClock(long deltaMs)
    {
        _interpreter.NowMs += deltaMs;
        _clock.Value = _interpreter.NowMs / 500 % 2 == 1;
    }

    /// <summary>Un ciclo de programa de golpe; <paramref name="deltaMs"/> es lo que avanza el reloj antes de ejecutarlo.</summary>
    public void Scan(long deltaMs)
    {
        lock (Sync)
        {
            foreach (var _ in ScanSteps(deltaMs)) { }
        }
    }

    /// <summary>
    /// Un ciclo, deteniéndose antes de cada sentencia: las de las secciones del programa (bloqueo, orden, cada mapeo y la llamada de
    /// cada elemento; marco de la sección, profundidad 0, línea = su número de sentencia, <see cref="SectionListing"/>) y las de los
    /// bloques (profundidad 1 y más). Las salidas físicas del final se hacen sin parar. PENDIENTES 87.
    /// </summary>
    public IEnumerable<StepPoint> ScanSteps(long deltaMs)
    {
        _interpreter.NowMs += deltaMs;
        var pulse = _interpreter.NowMs / 500 % 2 == 1;   // reloj de 1 Hz (medio segundo a 1, medio a 0), como la marca de reloj del PLC
        _clock.Value = pulse;

        for (var i = 0; i < _order.Count; i++)
        {
            if (_order[i] is FbInstance user)
            {
                foreach (var point in _interpreter.Execute(user, _runNames[i])) yield return point;
                continue;
            }

            if (_order[i] is InsertRun insert)
            {
                foreach (var point in Run(insert)) yield return point;
                continue;
            }

            var run = (ElementRun)_order[i];
            var section = run.Section;
            foreach (var assignment in run.Assignments)
            {
                section.Line = assignment.Line;
                yield return new StepPoint(section, assignment.Line);
                if (assignment.When is { } when && !(bool)when()) continue;
                assignment.Target.Value = StInterpreter.Coerce(assignment.Value(), assignment.Target.Type);
            }

            section.Line = run.CallLine;
            yield return new StepPoint(section, run.CallLine);
            var vars = run.Instance.Vars;
            if (vars.TryGetValue("Pul_1Sec", out var clock)) clock.Value = pulse;
            if (vars.TryGetValue("Debug", out var debug)) debug.Value = _debugSim.Value;
            vars["Data"] = run.Data;
            foreach (var (parameter, slot) in run.Companions) vars[parameter] = slot;

            foreach (var point in _interpreter.Execute(run.Instance, _runNames[i], section)) yield return point;
        }

        if (_outputStart is { } start)
            foreach (var point in Run(start)) yield return point;
        for (var i = 0; i < _outputs.Count; i++)
        {
            var line = _outputFirstLine + i;
            _outputFrame.Line = line;
            yield return new StepPoint(_outputFrame, line);
            _outputs[i]();
        }

        if (_outputEnd is { } end)
            foreach (var point in Run(end)) yield return point;
    }

    /// <summary>El código del usuario de una sección: para en su sentencia de la sección y ejecuta su bloque desde ella.</summary>
    private IEnumerable<StepPoint> Run(InsertRun insert)
    {
        insert.Section.Line = insert.Line;
        yield return new StepPoint(insert.Section, insert.Line);
        foreach (var point in _interpreter.Execute(insert.Instance, insert.Name, insert.Section)) yield return point;
    }
}
