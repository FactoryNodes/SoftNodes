using System.Collections.ObjectModel;
using System.Windows.Input;
using FactoryNodes.Iec61131.Emission;
using FactoryNodes.Iec61131.Model;
using FactoryNodes.SoftNodes.Runtime;
using FactoryNodes.StEditing;

namespace FactoryNodes.SoftNodes.Debugging;

/// <summary>Un punto de parada de la lista: dónde está, en qué instancia, y quitarlo.</summary>
/// <param name="line">La línea que se enseña (en una sección, la del código y no el número de sentencia); null = la del punto.</param>
public sealed class BreakpointRowViewModel(Breakpoint point, Action<Breakpoint> remove, int? line = null)
{
    public Breakpoint Point { get; } = point;

    public string Text => $"{Point.Pou}, línea {line ?? Point.Line}" + (Point.Instance is { } instance ? $" · solo {instance}" : " · todas las instancias");

    public ICommand RemoveCommand { get; } = new RelayCommand(() => remove(point));
}

/// <summary>Un bloque de la pila de llamadas de la foto de un fallo.</summary>
/// <param name="line">La línea que se enseña (en una sección, la del código); null = la del marco.</param>
public sealed class FaultFrameRowViewModel(int index, FaultFrame frame, int? line = null)
{
    public int Index { get; } = index;
    public FaultFrame Frame { get; } = frame;
    public string Text => $"{Frame.Pou}" + (Frame.Path is { } path ? $" ({path})" : "") + $", línea {line ?? Frame.Line}";
}

/// <summary>
/// Una fila de la columna «Variables»: una variable del bloque donde está el puntero, con su valor; las estructuras, los arrays y los
/// bloques se despliegan. <see cref="Changed"/>: cambió con el último paso (en dorado).
/// </summary>
public sealed class DebugVariableRowViewModel(string path, string name, int depth, string type, string value, bool canExpand, bool isExpanded,
    bool changed, Action<string> toggle)
{
    public string Path { get; } = path;
    public string Name { get; } = name;
    public int Depth { get; } = depth;

    /// <summary>Sangría por nivel, en píxeles (la vista la usa como margen).</summary>
    public double Indent => Depth * 14;

    public string Type { get; } = type;
    public string Value { get; } = value;
    public bool CanExpand { get; } = canExpand;
    public bool IsExpanded { get; } = isExpanded;
    public string Glyph => !CanExpand ? "" : IsExpanded ? "▾" : "▸";
    public bool Changed { get; } = changed;
    public ICommand ToggleCommand { get; } = new RelayCommand(() => toggle(path));
}

/// <summary>
/// El depurador de solo lectura de una instancia (PENDIENTES 49, 78 y 79). Con la CPU en STOP se va sentencia a sentencia por el
/// código de los bloques; con la CPU en RUN, un punto de parada (o «Pausa») la para a mitad del ciclo y el depurador lo recoge. Muestra
/// el código con la línea que se va a ejecutar, los puntos de parada (cada uno en todas las instancias o en una), el valor de las
/// variables junto a cada línea y, en la columna de la derecha, las variables del bloque donde está el puntero. Si la CPU se paró por
/// un fallo, «Ver el fallo» enseña su foto: la línea en rojo, la pila de llamadas y los valores de entonces. Nunca se edita el código:
/// se cambia en FactoryNodes y se transfiere. Solo lógica; la vista (AvalonEdit) pinta <see cref="CodeText"/> con <see cref="Spans"/>.
/// </summary>
public sealed class DebugViewModel : ObservableObject
{
    private readonly Func<SoftInstance, string, SectionListing?> _listing;
    private readonly SoftInstance _instance;
    private readonly StepDebugger _debugger;
    private readonly Action<Action> _post;
    private string _codeText = "";
    private IReadOnlyList<StSpan> _spans = [];
    private int _currentLine;
    private int _faultLine;
    private string _status = "Sin ciclo en curso. «Paso» empieza uno en la primera sentencia.";
    private string? _shownPou;
    private List<int> _fileLines = [];
    private List<int> _lineNumbers = [];
    private List<string> _codeLines = [];
    private int _caretLine;
    private Frame? _valuesFrame;
    private string? _selectedSection;
    private string? _selectedTarget;
    private readonly Dictionary<string, SectionListing?> _listings = new(StringComparer.OrdinalIgnoreCase);
    private (SoftProgram Program, string Profile)? _listingsOf;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase) { "Data" };
    private Dictionary<string, string> _previousValues = new(StringComparer.OrdinalIgnoreCase);
    private bool _showingFault;
    private FaultFrameRowViewModel? _selectedFaultFrame;

    /// <param name="post">Para pasar al hilo de la ventana lo que avisa la CPU desde el suyo (una parada en RUN).</param>
    /// <param name="listing">El código de cada sección generada (SoftNodes Pro: el del emisor de su PLC); sin él, el ST neutro (<see cref="NeutralSectionListing"/>).</param>
    public DebugViewModel(SoftInstance instance, Action<Action>? post = null, Func<SoftInstance, string, SectionListing?>? listing = null)
    {
        _instance = instance;
        _listing = listing ?? NeutralSectionListing.For;
        _post = post ?? (action => action());
        _debugger = new StepDebugger(instance);
        _debugger.CycleCompleted += (_, _) => OnPropertyChanged(nameof(IsActive));
        instance.Paused += (_, e) => _post(() => OnPaused(e));

        PauseCommand = new RelayCommand(Pause, () => _instance.Mode == SoftMode.Running);
        StopCycleCommand = new RelayCommand(() => { _debugger.Abandon(); Status = "Ciclo dejado a medias: la memoria queda como está."; Refresh(); },
            () => _debugger.IsActive);
        StepCommand = new RelayCommand(() => Do(_debugger.StepOver, nextCycle: true), CanStep);
        StepIntoCommand = new RelayCommand(() => Do(_debugger.StepInto, nextCycle: true), CanStep);
        StepOutCommand = new RelayCommand(() => Do(_debugger.StepOut, nextCycle: true), () => CanStep() && _debugger.IsActive);
        CycleCommand = new RelayCommand(() => Do(_debugger.Cycle, nextCycle: false), CanStep);
        RunToCursorCommand = new RelayCommand(RunToCursor, () => CanStep() && _shownPou is not null && CaretLine > 0);
        ToggleBreakpointCommand = new RelayCommand(() => ToggleBreakpoint(CaretLine), () => _shownPou is not null && CaretLine > 0);
        RemoveAllBreakpointsCommand = new RelayCommand(() =>
        {
            _debugger.Breakpoints.Clear();
            BreakpointsChanged();
        }, () => _debugger.Breakpoints.Count > 0);
        ShowFaultCommand = new RelayCommand(ShowFault, () => _instance.FaultSnapshot is not null);
        GoToLoopCommand = new RelayCommand(() => { if (LoopPlace is { } place) GoToLoop?.Invoke(place); }, () => LoopPlace is not null && GoToLoop is not null);
    }

    public ICommand PauseCommand { get; }
    public ICommand StopCycleCommand { get; }
    public ICommand StepCommand { get; }
    public ICommand StepIntoCommand { get; }
    public ICommand StepOutCommand { get; }
    public ICommand CycleCommand { get; }
    public ICommand RunToCursorCommand { get; }
    public ICommand ToggleBreakpointCommand { get; }
    public ICommand RemoveAllBreakpointsCommand { get; }
    public ICommand ShowFaultCommand { get; }

    /// <summary>Se puede ir paso a paso: la CPU en STOP.</summary>
    private bool CanStep() => _instance.Mode != SoftMode.Running;

    /// <summary>La CPU se paró en RUN (punto de parada o pausa): hay que enseñar el depurador.</summary>
    public event EventHandler? ShowRequested;

    // ---------------------------------------------------------------- Ir al loop

    /// <summary>
    /// «Ir al loop»: lo que hace falta para llevar a FactoryNodes a la etapa. SoftNodes no edita: lo pone quien lo aloja (la
    /// ventana), que sabe a qué proyecto pertenece el programa y cómo hablar con FactoryNodes. Sin él, el botón no se puede usar.
    /// </summary>
    public Action<LoopPlace>? GoToLoop { get; set; }

    public ICommand GoToLoopCommand { get; }

    /// <summary>La línea del cursor en el código que se ve (desde 1; 0 si no hay): la pone la vista al mover el cursor.</summary>
    public int CaretLine
    {
        get => _caretLine;
        set
        {
            if (!SetProperty(ref _caretLine, value)) return;
            RaiseLoopPlace();
            RaiseCommands();
        }
    }

    /// <summary>
    /// Dónde cae la línea que se está mirando en el código de un loop: la que se va a ejecutar si hay un ciclo a medias, o la del
    /// cursor. Null si el bloque que se ve no es de un loop.
    /// </summary>
    public LoopPlace? LoopPlace
    {
        get
        {
            if (_shownPou is null) return null;
            var line = IsActive && CurrentLine > 0 ? CurrentLine : CaretLine;
            return LoopSourceMap.Locate(_shownPou, _codeLines, line);
        }
    }

    /// <summary>«Loop DosifSilo01 · etapa 20, transición de prioridad 10», o vacío si no es un loop.</summary>
    public string LoopText => LoopPlace is { } place
        ? $"Loop {place.Loop}" + (place.Stage is { } stage ? $" · etapa {stage}" : "") + (place.Priority is { } priority ? $", transición de prioridad {priority}" : "")
        : "";

    private void RaiseLoopPlace()
    {
        OnPropertyChanged(nameof(LoopPlace));
        OnPropertyChanged(nameof(LoopText));
        ((RelayCommand)GoToLoopCommand).RaiseCanExecuteChanged();
    }

    // ---------------------------------------------------------------- Estado

    /// <summary>Hay un ciclo a medias.</summary>
    public bool IsActive => _debugger.IsActive;

    /// <summary>El código que se ve, tal cual: los valores van aparte (<see cref="LineValues"/>), así el texto no cambia con cada paso.</summary>
    public string CodeText
    {
        get => _codeText;
        private set => SetProperty(ref _codeText, value);
    }

    /// <summary>Los colores del código (palabras clave, tipos, números, comentarios).</summary>
    public IReadOnlyList<StSpan> Spans
    {
        get => _spans;
        private set => SetProperty(ref _spans, value);
    }

    /// <summary>
    /// El valor de las variables de cada línea (la primera es el índice 0), «Data.Control.Run = TRUE, tStop.Q = FALSE», o vacío. La
    /// vista los pinta al final de la línea, tras una raya «│» y sobre otro fondo: no son código ni forman parte del texto.
    /// </summary>
    public IReadOnlyList<string> LineValues { get; private set; } = [];

    /// <summary>
    /// Lo que se enseña al pasar el ratón por un nombre de la línea <paramref name="line"/> (desde 1), columna <paramref name="column"/>
    /// (desde 0): su valor, o el de los miembros de una estructura. Solo con un ciclo a medias ahí; si no, null.
    /// </summary>
    public string? ValueAt(int line, int column)
    {
        if (_valuesFrame is not { } frame || line < 1 || line > _codeLines.Count) return null;
        return StepDebugger.PathAt(_codeLines[line - 1], column) is { } path ? _debugger.Describe(path, frame) : null;
    }

    /// <summary>
    /// Ctrl+clic en un nombre (línea desde 1, columna desde 0): la instancia de un equipo de una sección (<c>CT01_FB</c>) se elige a la
    /// derecha; otra instancia o el nombre de un bloque enseña el bloque de la librería. False si ahí no hay nada a lo que ir.
    /// </summary>
    public bool GoToDefinition(int line, int column)
    {
        if (line < 1 || line > _codeLines.Count || StepDebugger.PathAt(_codeLines[line - 1], column) is not { } path) return false;
        var program = _instance.Program;
        if (_debugger.InstanceAt(path, _valuesFrame) is { } instance)
        {
            if (program.Runs.FirstOrDefault(r => !r.IsUserSection && !r.IsInsert && ReferenceEquals(program.InstanceOfRun(r.Name), instance)) is { } run)
            {
                SelectedSection = run.Section;
                SelectedTarget = InstanceItem(run);
                return true;
            }

            return ShowLibraryBlock(instance.Type.Name);
        }

        return ShowLibraryBlock(path);
    }

    private bool ShowLibraryBlock(string name)
    {
        if (_instance.Program.Interpreter.Library.Pou(name) is not { } pou) return false;
        SelectedSection = LibraryChoice;
        SelectedTarget = pou.Name;
        return true;
    }

    /// <summary>La línea del texto (desde 1) que se ejecutará en el próximo paso; 0 si no hay.</summary>
    public int CurrentLine
    {
        get => _currentLine;
        private set
        {
            if (SetProperty(ref _currentLine, value)) RaiseLoopPlace();
        }
    }

    /// <summary>La línea del texto (desde 1) donde falló la CPU, al ver la foto de un fallo; 0 si no.</summary>
    public int FaultLine
    {
        get => _faultLine;
        private set => SetProperty(ref _faultLine, value);
    }

    /// <summary>Las líneas del texto (desde 1) con punto de parada.</summary>
    public IReadOnlyList<int> BreakpointLines => _shownPou is null
        ? []
        : _debugger.Breakpoints.Where(b => b.Pou == _shownPou).Select(b => _fileLines.IndexOf(b.Line) + 1).Where(l => l > 0).Distinct().Order().ToList();

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>El bloque que se ve; durante el paso a paso sigue al que se está ejecutando.</summary>
    public string? ShownPou => _shownPou;

    /// <summary>
    /// La clave de cada línea del texto (la primera es el índice 0): en un bloque, su línea del fichero (la que dicen los avisos); en
    /// una sección, su número de sentencia (0 si en esa línea no se ejecuta nada). Con ella van los puntos de parada.
    /// </summary>
    public IReadOnlyList<int> FileLines => _fileLines;

    /// <summary>El número que se pinta junto a cada línea: en un bloque, la del fichero; en una sección, la del código (1, 2, 3…).</summary>
    public IReadOnlyList<int> LineNumbers => _lineNumbers;

    public IReadOnlyList<string> PouNames => _instance.Program.Interpreter.Library.Pous.Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();

    // ---------------------------------------------------------------- Secciones e instancias (PENDIENTES 87)

    /// <summary>La entrada final de <see cref="SectionChoices"/>: ver un bloque de la librería sin pasar por una sección.</summary>
    public const string LibraryChoice = "Bloques de la librería";

    /// <summary>Separa, en <see cref="TargetChoices"/>, la instancia de su bloque («CT01 · Motor_Block»).</summary>
    public const string InstanceSeparator = " · ";

    /// <summary>
    /// El desplegable de la izquierda: las secciones del programa en el orden del ciclo, con los nombres del PLC («fnc001_User_Inicio»,
    /// «fnc010_Topology», «fnc050_Motors»…), y al final <see cref="LibraryChoice"/>.
    /// </summary>
    public IReadOnlyList<string> SectionChoices => [.. _instance.Program.SectionNames, LibraryChoice];

    /// <summary>
    /// El desplegable de la derecha: primero la propia sección («Sección fnc050_Motors») y después sus instancias por orden alfabético
    /// («CT01 · Motor_Block»); con <see cref="LibraryChoice"/>, los bloques de la librería.
    /// </summary>
    public IReadOnlyList<string> TargetChoices
    {
        get
        {
            if (_selectedSection is null) return [];
            if (_selectedSection == LibraryChoice) return PouNames;
            var runs = _instance.Program.Runs.Where(r => !r.IsUserSection && r.Section.Equals(_selectedSection, StringComparison.OrdinalIgnoreCase)).ToList();
            // el código del usuario de la sección (principio y final) detrás de ella; después, las instancias por orden alfabético
            return [SectionItem(_selectedSection), .. runs.Where(r => r.IsInsert).Select(InstanceItem),
                .. runs.Where(r => !r.IsInsert).Select(InstanceItem).Order(StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>«Sección fnc050_Motors»: la primera entrada de <see cref="TargetChoices"/>.</summary>
    public static string SectionItem(string section) => "Sección " + section;

    private static string InstanceItem(SoftRun run) => run.IsInsert
        ? (run.Name.EndsWith("_Start", StringComparison.Ordinal) ? "Código de usuario (principio)" : "Código de usuario (final)")
        : run.Name + InstanceSeparator + run.Block;

    /// <summary>La sección elegida a la izquierda: enseña su código (la primera entrada de la derecha).</summary>
    public string? SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is null || value == _selectedSection) return;
            _selectedSection = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TargetChoices));
            if (value == LibraryChoice)
            {
                // se queda el código que se ve; si es un bloque, sale elegido
                _selectedTarget = _shownPou is not null && !IsSection(_shownPou) ? _shownPou : null;
                OnPropertyChanged(nameof(SelectedTarget));
                return;
            }

            _selectedTarget = null;
            SelectedTarget = SectionItem(value);
        }
    }

    /// <summary>
    /// Lo elegido a la derecha: la sección enseña su código como lo escribe el PLC que se simula; una instancia, el código de su bloque,
    /// y los puntos de parada que se pongan y «Ejecutar hasta el cursor» paran solo en ella; un bloque de la librería, su código.
    /// </summary>
    public string? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (value is null || value == _selectedTarget) return;
            _selectedTarget = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InstanceFilter));
            if (ChosenCode() is { } code) ShowChosen(code);
        }
    }

    /// <summary>En qué instancia paran los puntos de parada que se ponen ahora y «Ejecutar hasta el cursor»: la elegida a la derecha, o en todas.</summary>
    public string? InstanceFilter => ChosenRun() is { IsUserSection: false, IsInsert: false } run && _selectedTarget == InstanceItem(run) ? run.Name : null;

    /// <summary>Lo que ejecuta la entrada de la derecha (una instancia, o la sección de usuario), o null.</summary>
    private SoftRun? ChosenRun()
    {
        if (_selectedTarget is null || _selectedSection is null || _selectedSection == LibraryChoice) return null;
        var runs = _instance.Program.Runs.Where(r => r.Section.Equals(_selectedSection, StringComparison.OrdinalIgnoreCase));
        return _selectedTarget == SectionItem(_selectedSection) ? runs.FirstOrDefault(r => r.IsUserSection) : runs.FirstOrDefault(r => InstanceItem(r) == _selectedTarget);
    }

    /// <summary>El código de lo elegido: la sección, el bloque de la instancia (o de la sección de usuario) o el bloque de la librería.</summary>
    private string? ChosenCode()
    {
        if (_selectedTarget is null || _selectedSection is null) return null;
        if (_selectedSection == LibraryChoice) return _selectedTarget;
        if (ChosenRun() is { } run) return run.Block;
        return _selectedTarget == SectionItem(_selectedSection) ? _selectedSection : null;
    }

    /// <summary>Enseña lo elegido; con un ciclo a medias justo ahí (esa sección o esa instancia), con su línea y sus valores.</summary>
    private void ShowChosen(string code)
    {
        _showingFault = false;
        StepPoint? here = null;
        if (IsActive && _debugger.Current is { } now && now.Frame.Name.Equals(code, StringComparison.OrdinalIgnoreCase))
        {
            var run = ChosenRun();
            var index = _instance.Program.RunIndexOf(now.Frame.Path);
            var runs = _instance.Program.Runs;
            if (run is null || index >= 0 && index < runs.Count && runs[index] == run) here = now;
        }

        Show(code, here, follow: false);
    }

    /// <summary>¿Es el nombre de una sección de elementos del programa (y no un bloque)?</summary>
    private bool IsSection(string name) =>
        _instance.Program.SectionNames.Contains(name, StringComparer.OrdinalIgnoreCase) &&
        !_instance.Program.Runs.Any(r => r.IsUserSection && r.Section.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>El código de una sección como lo escribe el emisor del PLC que simula la instancia (se guarda hasta que cambia el programa o el perfil).</summary>
    private SectionListing? Listing(string section)
    {
        var key = (_instance.Program, _instance.Profile.Name);
        if (_listingsOf != key)
        {
            _listings.Clear();
            _listingsOf = key;
        }

        if (!_listings.TryGetValue(section, out var listing)) _listings[section] = listing = _listing(_instance, section);
        return listing;
    }

    /// <summary>La línea que se enseña de un sitio del código: en una sección, la del código de su número de sentencia.</summary>
    private int DisplayLine(string pou, int line) =>
        IsSection(pou) && Listing(pou) is { } listing && line >= 1 && line <= listing.StatementLines.Count ? listing.StatementLines[line - 1] : line;

    /// <summary>Los desplegables siguen a donde está el ciclo: su sección y, dentro de un bloque, la instancia de arriba.</summary>
    private void Follow(StepPoint now)
    {
        string section, target;
        if (now.Frame.Depth == 0)
        {
            // en la sección (también la de salidas físicas, que no tiene instancias)
            section = now.Frame.Name;
            target = SectionItem(section);
        }
        else
        {
            var runs = _instance.Program.Runs;
            var index = _instance.Program.RunIndexOf(now.Frame.Path);
            if (index < 0 || index >= runs.Count) return;
            var run = runs[index];
            section = run.Section;
            target = run.IsUserSection ? SectionItem(run.Section) : InstanceItem(run);
        }

        if (_selectedSection != section)
        {
            _selectedSection = section;
            OnPropertyChanged(nameof(SelectedSection));
            OnPropertyChanged(nameof(TargetChoices));
        }

        if (_selectedTarget == target) return;
        _selectedTarget = target;
        OnPropertyChanged(nameof(SelectedTarget));
        OnPropertyChanged(nameof(InstanceFilter));
    }

    /// <summary>Se repinta el código o cambió la línea actual o los puntos de parada.</summary>
    public event EventHandler? Changed;

    public ObservableCollection<BreakpointRowViewModel> BreakpointRows { get; } = [];

    /// <summary>Pone o quita un punto de parada en una línea del texto (desde 1), en la instancia elegida.</summary>
    public void ToggleBreakpoint(int line)
    {
        if (_shownPou is null || line < 1 || line > _fileLines.Count) return;
        var fileLine = _fileLines[line - 1];
        if (fileLine <= 0)
        {
            Status = NothingRunsHere;
            return;
        }

        var existing = _debugger.Breakpoints.Where(b => b.Pou == _shownPou && b.Line == fileLine).ToList();
        if (existing.Count > 0) foreach (var b in existing) _debugger.Breakpoints.Remove(b);
        else _debugger.Breakpoints.Add(new Breakpoint(_shownPou, fileLine, InstanceFilter));
        BreakpointsChanged();
    }

    private void BreakpointsChanged()
    {
        _debugger.BreakpointsChanged();
        BreakpointRows.Clear();
        foreach (var point in _debugger.Breakpoints.OrderBy(b => b.Pou, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Line))
            BreakpointRows.Add(new BreakpointRowViewModel(point, p =>
            {
                _debugger.Breakpoints.Remove(p);
                BreakpointsChanged();
            }, DisplayLine(point.Pou, point.Line)));
        OnPropertyChanged(nameof(HasBreakpoints));
        ((RelayCommand)RemoveAllBreakpointsCommand).RaiseCanExecuteChanged();
        Raise();
    }

    public bool HasBreakpoints => _debugger.Breakpoints.Count > 0;

    // ---------------------------------------------------------------- La IA por MCP (PENDIENTES 122): lo mismo que con el ratón

    /// <summary>Cómo va: si hay un ciclo a medias y dónde, los puntos de parada y el fallo (si la CPU paró por uno).</summary>
    public object McpState() => new
    {
        cycle = _debugger.IsActive,
        at = _debugger.Current is { } at ? new { pou = at.Frame.Name, instance = at.Frame.Path, line = at.Line } : null,
        status = Status,
        breakpoints = _debugger.Breakpoints.OrderBy(b => b.Pou, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Line)
            .Select(b => new { pou = b.Pou, line = b.Line, instance = b.Instance }).ToList(),
        fault = _instance.Fault?.Message
    };

    /// <summary>El código de un bloque con el número de línea de cada sentencia (el de los puntos de parada); null si no existe.</summary>
    public string? CodeOf(string pou)
    {
        if (_instance.Program.Interpreter.Library.Pou(pou) is not { } block) return null;
        return string.Join("\n", block.Body.Lines.Select(line => $"{line.Location.Line,5}  " + line switch
        {
            CodeLine c => c.Text,
            RegionStart r => "/// REGION " + r.Title,
            _ => "/// END_REGION"
        }));
    }

    /// <summary>Pone o quita un punto de parada (bloque, línea del bloque y, si se da, la instancia en la que para).</summary>
    public void SetBreakpoint(string pou, int line, string? instance, bool on)
    {
        if (_instance.Program.Interpreter.Library.Pou(pou) is not { } block) throw new InvalidOperationException($"No existe el bloque «{pou}».");
        if (on && block.Body.Lines.All(l => l.Location.Line != line || l is not CodeLine))
            throw new InvalidOperationException($"La línea {line} de {block.Name} no tiene código: mira las líneas con «debug» ▸ code.");
        _debugger.Breakpoints.RemoveWhere(b => b.Pou.Equals(block.Name, StringComparison.OrdinalIgnoreCase) && b.Line == line);
        if (on) _debugger.Breakpoints.Add(new Breakpoint(block.Name, line, string.IsNullOrWhiteSpace(instance) ? null : instance.Trim()));
        BreakpointsChanged();
    }

    public void ClearBreakpoints()
    {
        _debugger.Breakpoints.Clear();
        BreakpointsChanged();
    }

    /// <summary>Una orden del paso a paso, como su botón: step, step_into, step_out, cycle, pause o abandon.</summary>
    public void Execute(string action)
    {
        var command = action switch
        {
            "step" => StepCommand,
            "step_into" => StepIntoCommand,
            "step_out" => StepOutCommand,
            "cycle" => CycleCommand,
            "pause" => PauseCommand,
            "abandon" => StopCycleCommand,
            _ => throw new InvalidOperationException($"«{action}» no es una orden del depurador.")
        };
        if (!command.CanExecute(null))
            throw new InvalidOperationException(action == "pause"
                ? "Pausa solo con la CPU en RUN."
                : action is "abandon" or "step_out" ? "No hay ningún ciclo a medias." : "Para ir paso a paso, pon la CPU en STOP.");
        command.Execute(null);
    }

    // ---------------------------------------------------------------- Órdenes

    /// <summary>«Pausa» con la CPU en RUN: para en la primera sentencia del ciclo siguiente.</summary>
    private void Pause()
    {
        _instance.RequestPause();
        Status = "Pausa pedida: la CPU para al empezar el ciclo siguiente.";
    }

    /// <summary>La CPU se paró en RUN: recoge el ciclo a medias y lo enseña.</summary>
    private void OnPaused(SoftPausedEventArgs e)
    {
        _debugger.Attach(e.Steps, e.At);
        _showingFault = false;
        _previousValues = new(StringComparer.OrdinalIgnoreCase);
        Refresh();
        var breakpoint = _debugger.Breakpoints.FirstOrDefault(b => b.Matches(e.At));
        Status = (breakpoint is null ? "Pausa: " : "Punto de parada: ") + Where(e.At) + ". «RUN» sigue; «Paso» va sentencia a sentencia.";
        ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// «RUN» con un ciclo a medias: termina el ciclo (parando en un punto de parada, si lo hay) y devuelve si la CPU ya puede seguir en
    /// RUN. Lo llama la orden RUN de la instancia antes de arrancar.
    /// </summary>
    public bool FinishCycleBeforeRun()
    {
        if (!_debugger.IsActive) return true;
        _debugger.Continue();
        Refresh();
        if (_debugger.IsActive)
        {
            Status = "Punto de parada antes de terminar el ciclo: " + Where(_debugger.Current!.Value) + ".";
            return false;
        }

        return _instance.Fault is null;
    }

    private void RunToCursor()
    {
        if (_shownPou is not { } pou || CaretLine < 1 || CaretLine > _fileLines.Count) return;
        // en una sección, una línea sin sentencia (REGION, comentario): la primera sentencia que viene detrás
        var index = _fileLines.FindIndex(CaretLine - 1, l => l > 0);
        if (index < 0)
        {
            Status = NothingRunsHere;
            return;
        }

        var line = _fileLines[index];
        var instance = InstanceFilter;
        Do(() =>
        {
            if (!_debugger.RunToCursor(pou, line, instance) && _debugger.LastStop.Length == 0)
                _debugger.Begin();
        }, nextCycle: false, startsItself: true);
    }

    /// <param name="startsItself">La orden empieza ella el ciclo si no hay uno a medias («Ejecutar hasta el cursor»): no se empieza antes.</param>
    private void Do(Action step, bool nextCycle, bool startsItself = false)
    {
        _showingFault = false;
        _previousValues = CurrentValues();
        var faultBefore = _instance.FaultSnapshot;
        bool NewFault() => _instance.FaultSnapshot is { } now && !ReferenceEquals(now, faultBefore);
        try
        {
            // sin ciclo a medias (también tras un fallo: la CPU sigue en STOP), cualquier paso empieza uno
            if (!_debugger.IsActive && !startsItself) _debugger.Begin();
            else step();
            // «Paso» al final del ciclo: el siguiente, parado en su primera sentencia (el puntero siempre se ve)
            if (nextCycle && !_debugger.IsActive && !NewFault()) _debugger.Begin();
        }
        catch (InvalidOperationException e)
        {
            Status = e.Message;
            return;
        }

        Refresh();
        if (NewFault())
        {
            ShowFault();
            return;
        }

        if (_debugger.LastStop.Length > 0) Status = char.ToUpperInvariant(_debugger.LastStop[0]) + _debugger.LastStop[1..] + ". " + Status;
    }

    /// <summary>Al apagar o recargar el programa: se deja el ciclo a medias.</summary>
    public void Close()
    {
        _debugger.Abandon();
        _showingFault = false;
        Refresh();
    }

    /// <summary>Repinta tras un paso (o tras un ciclo hecho desde fuera, o un cambio de estado de la CPU).</summary>
    public void Refresh()
    {
        if (_showingFault && _instance.FaultSnapshot is null) _showingFault = false;
        if (_showingFault)
        {
            RaiseCommands();
            return;
        }

        FaultLine = 0;
        if (_debugger.Current is { } current)
        {
            Show(current.Frame.Name, current);
            Status = $"Parado en {Where(current)}. Sentencias ejecutadas: {_debugger.Steps}.";
        }
        else
        {
            CurrentLine = 0;
            if (_shownPou is not null) Show(_shownPou, null);
            RebuildVariables(null, null);
            Raise();
        }

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(HasFault));
        OnPropertyChanged(nameof(FaultText));
        OnPropertyChanged(nameof(PouNames));
        OnPropertyChanged(nameof(SectionChoices));
        OnPropertyChanged(nameof(TargetChoices));
        RaiseCommands();
    }

    /// <summary>
    /// Lo que cambia con el estado de la CPU (RUN/STOP, un fallo nuevo), sin repintar el código: lo llama el reloj de la ventana, así
    /// que no mueve el código que se está mirando.
    /// </summary>
    public void RefreshState()
    {
        OnPropertyChanged(nameof(HasFault));
        OnPropertyChanged(nameof(FaultText));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { PauseCommand, StopCycleCommand, StepCommand, StepIntoCommand, StepOutCommand, CycleCommand,
                     RunToCursorCommand, ToggleBreakpointCommand, RemoveAllBreakpointsCommand, ShowFaultCommand })
            ((RelayCommand)command).RaiseCanExecuteChanged();
    }

    private string Where(StepPoint point) =>
        $"{point.Frame.Name}" + (point.Frame.Path is { } path ? $" ({path})" : "") + $", línea {DisplayLine(point.Frame.Name, point.Line)}";

    private const string NothingRunsHere = "En esta línea no se ejecuta nada: elige una asignación o la llamada a un bloque.";

    // ---------------------------------------------------------------- Foto del fallo (PENDIENTES 79)

    /// <summary>La CPU está en STOP por un fallo y hay foto.</summary>
    public bool HasFault => _instance.FaultSnapshot is not null;

    /// <summary>«La CPU se paró por un fallo: …» (con el ciclo y la hora del PLC), o vacío.</summary>
    public string FaultText => _instance.FaultSnapshot is { } f
        ? $"La CPU se paró por un fallo en el ciclo {f.Cycle} ({TimeSpan.FromMilliseconds(f.NowMs):hh\\:mm\\:ss\\.f}): {f.Message} ({f.File}, línea {f.Line})."
        : "";

    /// <summary>Se está viendo la foto del fallo (código, pila y valores de entonces), no la memoria de ahora.</summary>
    public bool ShowingFault => _showingFault;

    /// <summary>La pila de llamadas de la foto: el bloque donde falló y quien lo llamó, hasta lo más alto del ciclo.</summary>
    public ObservableCollection<FaultFrameRowViewModel> FaultFrames { get; } = [];

    /// <summary>El bloque de la pila que se ve (al elegir otro, su código y sus valores).</summary>
    public FaultFrameRowViewModel? SelectedFaultFrame
    {
        get => _selectedFaultFrame;
        set
        {
            if (!SetProperty(ref _selectedFaultFrame, value) || value is null || !_showingFault) return;
            ShowFaultFrame(value.Frame);
        }
    }

    private void ShowFault()
    {
        if (_instance.FaultSnapshot is not { } snapshot) return;
        _debugger.Abandon();
        _showingFault = true;
        FaultFrames.Clear();
        for (var i = 0; i < snapshot.Frames.Count; i++)
            FaultFrames.Add(new FaultFrameRowViewModel(i, snapshot.Frames[i], DisplayLine(snapshot.Frames[i].Pou, snapshot.Frames[i].Line)));
        OnPropertyChanged(nameof(ShowingFault));
        Status = "Foto del fallo: lo que había en ese momento. A la derecha, la pila de llamadas (elige un bloque para ver su código y sus valores). «Paso» o «Ciclo» empiezan un ciclo nuevo con la memoria de ahora.";
        if (FaultFrames.Count > 0)
        {
            _selectedFaultFrame = null;
            SelectedFaultFrame = FaultFrames[0];
        }
        else
        {
            RebuildVariables(snapshot.Globals, null);
            Raise();
        }

        RaiseCommands();
    }

    private void ShowFaultFrame(FaultFrame frame)
    {
        CurrentLine = 0;
        Show(frame.Pou, null);
        FaultLine = _fileLines.IndexOf(frame.Line) + 1;
        _previousValues = new(StringComparer.OrdinalIgnoreCase);
        RebuildVariables(frame.Variables, null);
        Raise();
    }

    // ---------------------------------------------------------------- Código

    /// <summary>
    /// Pinta un bloque de la librería o una sección del programa (<paramref name="name"/>), con la línea de <paramref name="current"/> si
    /// se da. <paramref name="follow"/>: los desplegables pasan a donde está <paramref name="current"/>.
    /// </summary>
    private void Show(string name, StepPoint? current, bool follow = true)
    {
        var section = IsSection(name);
        var code = new List<string>();
        _fileLines = [];
        if (section)
        {
            // La sección como la escribe el PLC que se simula; la clave de cada línea es su número de sentencia
            if (Listing(name) is not { } listing) return;
            code.AddRange(listing.Lines);
            _fileLines.AddRange(Enumerable.Repeat(0, code.Count));
            for (var i = 0; i < listing.StatementLines.Count; i++) _fileLines[listing.StatementLines[i] - 1] = i + 1;
            _lineNumbers = Enumerable.Range(1, code.Count).ToList();
        }
        else
        {
            if (_instance.Program.Interpreter.Library.Pou(name) is not { } pou) return;
            foreach (var line in pou.Body.Lines)
            {
                code.Add(line switch
                {
                    CodeLine c => c.Text,
                    RegionStart r => "/// REGION " + r.Title,
                    _ => "/// END_REGION"
                });
                _fileLines.Add(line.Location.Line);
            }

            _lineNumbers = _fileLines;
        }

        var text = string.Join("\n", code);

        // El valor de las variables de cada línea, a su derecha (solo con un ciclo a medias: hay marco en el que leerlas; o monitorizando la
        // caja, en el marco de la instancia elegida, PENDIENTES 170). También en las sentencias de una sección: sin ellos no se puede
        // depurar (autor, 2026-10-21); lo que no lleva la sección es la columna
        var frame = current?.Frame ?? MonitorFrame(name, section);
        _valuesFrame = frame;
        LineValues = code.Select((line, i) => frame is { } f && (!section || _fileLines[i] > 0) ? Values(line, f) : "").ToList();

        _shownPou = name;
        if (follow && current is { } at) Follow(at);

        _codeLines = code;
        if (text != CodeText) Spans = StClassifier.Classify(text);
        CodeText = text;
        CurrentLine = current is { } c2 ? _fileLines.IndexOf(c2.Line) + 1 : 0;
        if (current is { } point) RebuildVariables(point.Frame.Vars, point.Frame);
        else if (frame is { } monitored) RebuildVariables(monitored.Vars, monitored);
        Raise();
    }

    // ---------------------------------------------------------------- Monitorizar la caja (PENDIENTES 170)

    /// <summary>
    /// La memoria de esta instancia (parada) es la copia de otra que corre (la Factory Box): los valores se pintan sin ciclo a medias, en el
    /// marco de la instancia elegida. Lo pone quien trae la memoria (<c>SoftNodeViewModel</c>); no cambia nada del paso a paso.
    /// </summary>
    public bool Monitoring { get; set; }

    /// <summary>Con una memoria nueva: vuelve a pintar los valores de lo que se ve (si no hay ciclo a medias ni fallo que mande).</summary>
    public void RefreshMonitor()
    {
        if (!Monitoring || _showingFault || _debugger.Current is not null || _shownPou is not { } pou) return;
        Show(pou, null, follow: false);
    }

    /// <summary>El marco en el que leer los valores monitorizando: la instancia elegida (sus variables) o la sección (sus globales); null si no hay.</summary>
    private Frame? MonitorFrame(string name, bool section)
    {
        if (!Monitoring) return null;
        if (section) return new Frame(name, name, new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase), 0);   // la sección lee globales
        if (ChosenRun() is { } run && run.Block.Equals(name, StringComparison.OrdinalIgnoreCase) && _instance.Program.InstanceOfRun(run.Name) is { } fb)
            return new Frame(name, _instance.Program.Interpreter.Library.Pou(name)?.Location.File ?? name, fb.Vars, 1, path: run.Name);
        return null;
    }

    private string Values(string codeLine, Frame frame) =>
        string.Join(", ", _debugger.ValuesOf(codeLine, frame).Select(v => $"{v.Path} = {v.Text}"));

    private void Raise()
    {
        OnPropertyChanged(nameof(BreakpointLines));
        OnPropertyChanged(nameof(ShownPou));
        OnPropertyChanged(nameof(ShowingFault));
        RaiseLoopPlace();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- Columna «Variables»

    /// <summary>Las variables del bloque donde está el puntero (o del de la foto del fallo), en árbol plano: desplegar una estructura añade sus miembros debajo.</summary>
    public ObservableCollection<DebugVariableRowViewModel> Variables { get; } = [];

    /// <summary>De qué bloque son las variables de la columna («Motor_Block (M77)»), o vacío.</summary>
    public string VariablesTitle { get; private set; } = "";

    private IReadOnlyDictionary<string, Slot>? _variablesSource;

    private void RebuildVariables(IReadOnlyDictionary<string, Slot>? vars, Frame? frame)
    {
        _variablesSource = vars;
        VariablesTitle = frame is null
            ? _showingFault && _selectedFaultFrame is { } f ? $"{f.Frame.Pou}" + (f.Frame.Path is { } p ? $" ({p})" : "") + " · valores del fallo" : ""
            : frame.Name + (frame.Path is { } path ? $" ({path})" : "");
        OnPropertyChanged(nameof(VariablesTitle));
        FillVariables();
    }

    private void FillVariables()
    {
        Variables.Clear();
        if (_variablesSource is null) return;
        foreach (var (name, slot) in _variablesSource) AddRow(name, name, slot, 0);
    }

    private void AddRow(string path, string name, Slot slot, int depth)
    {
        var children = Children(slot).ToList();
        var expanded = children.Count > 0 && _expanded.Contains(path);
        var value = children.Count > 0 ? Summary(slot) : StepDebugger.Format(slot);
        var changed = children.Count == 0 && _previousValues.TryGetValue(path, out var before) && before != value;
        Variables.Add(new DebugVariableRowViewModel(path, name, depth, slot.Type.ToString(), value, children.Count > 0, expanded, changed, ToggleExpanded));
        if (!expanded) return;
        foreach (var (childName, child) in children.Take(500)) AddRow(path + (childName.StartsWith('[') ? "" : ".") + childName, childName, child, depth + 1);
    }

    private static IEnumerable<(string Name, Slot Slot)> Children(Slot slot) => slot.Value switch
    {
        StructValue s => s.Members.Select(m => (m.Key, m.Value)),
        FbInstance f => f.Vars.Select(v => (v.Key, v.Value)),
        ArrayValue a => a.Items.Select((item, i) => ($"[{a.Lower + i}]", item)),
        _ => []
    };

    private static string Summary(Slot slot) => slot.Value switch
    {
        ArrayValue a => $"{a.Items.Length} elementos",
        FbInstance f => f.Type.Name,
        _ => ""
    };

    private void ToggleExpanded(string path)
    {
        if (!_expanded.Remove(path)) _expanded.Add(path);
        FillVariables();
    }

    /// <summary>Los valores de ahora de lo que se ve en la columna (para marcar en dorado lo que cambia con el paso siguiente).</summary>
    private Dictionary<string, string> CurrentValues() =>
        Variables.Where(v => !v.CanExpand).ToDictionary(v => v.Path, v => v.Value, StringComparer.OrdinalIgnoreCase);
}
