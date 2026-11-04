using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using FactoryNodes.Localization;
using FactoryNodes.SoftNodes.Instances;
using FactoryNodes.SoftNodes.Runtime;

namespace FactoryNodes.SoftNodes.Open.Presentation;

/// <summary>
/// La ventana del SoftNodes abierto (PENDIENTES 159): a la izquierda las instancias (una carpeta cada una, como en PLCSIM Advanced); a la derecha la
/// elegida, con encender, RUN/STOP, cargar el programa que transfiere FactoryNodes, su configuración, sus variables (verlas y escribirlas a mano) y
/// su registro. Lo que no es abierto (la planta simulada, el banco de pruebas, el operario con IA, el depurador con el código de cada PLC) es de
/// SoftNodes Pro.
/// </summary>
public sealed class SoftNodesOpenViewModel : ObservableObject
{
    public const string ProductName = "SoftNodes";

    /// <summary>Lo que pinta la app por su cuenta (el diálogo de abrir), aquí para que se traduzca con lo demás.</summary>
    public static string ProgramFilter => L.T("Programas de SoftNodes (*.softnodes)");

    public static string CloseQuestion => L.T("Hay instancias encendidas: al cerrar se apagan (y se guarda su retentiva). ¿Cerrar?");

    private readonly IOpenSoftNodesServices _services;
    private InstanceViewModel? _selected;
    private string _status = "";

    public SoftNodesOpenViewModel(IOpenSoftNodesServices services)
    {
        _services = services;
        NewInstanceCommand = new RelayCommand(NewInstance);
        RemoveInstanceCommand = new RelayCommand(() => _ = RemoveSelectedAsync(), () => Selected is { IsOn: false });
        foreach (var name in services.Store.ListInstances().Order(StringComparer.OrdinalIgnoreCase))
            if (TryOpen(name) is { } item) Instances.Add(item);
        Selected = Instances.FirstOrDefault();
        Status = Instances.Count == 0 ? L.T("No hay instancias: crea una con el programa que guarda FactoryNodes («Exportar a PLC» ▸ SoftNodes).") : "";
    }

    public ObservableCollection<InstanceViewModel> Instances { get; } = [];

    public InstanceViewModel? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelected));
            (RemoveInstanceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelected => Selected is not null;

    public string Status
    {
        get => _status;
        internal set
        {
            _status = value;
            OnPropertyChanged();
        }
    }

    public ICommand NewInstanceCommand { get; }
    public ICommand RemoveInstanceCommand { get; }

    internal IOpenSoftNodesServices Services => _services;

    internal void Changed() => (RemoveInstanceCommand as RelayCommand)?.RaiseCanExecuteChanged();

    /// <summary>Lo que hace el reloj de la ventana (dos veces por segundo): estado, valores a la vista y si llegó otro programa.</summary>
    public void Tick()
    {
        foreach (var item in Instances) item.Tick();
    }

    // ---------- Instancias ----------

    private InstanceViewModel? TryOpen(string name)
    {
        try
        {
            return new InstanceViewModel(SimulatedPlc.Open(_services.Store, name), this);
        }
        catch (Exception e) when (e is SoftPackageException or IOException or InvalidOperationException or System.Text.Json.JsonException)
        {
            Status = L.F("No se pudo abrir la instancia «{0}»: {1}", name, e.Message);
            return null;
        }
    }

    private void NewInstance()
    {
        if (_services.ChooseProgram() is not { } chosen) return;
        try
        {
            NewInstance(chosen.Bytes, Path.GetFileNameWithoutExtension(chosen.FileName));
        }
        catch (Exception e) when (e is SoftPackageException or InvalidOperationException or IOException)
        {
            Status = L.F("No se pudo crear la instancia: {0}", e.Message);
        }
    }

    /// <summary>Crea una instancia con ese programa, con un nombre libre y los primeros puertos libres. Devuelve la instancia.</summary>
    public InstanceViewModel NewInstance(byte[] program, string wanted)
    {
        var package = SoftPackage.FromZip(program);
        package.CreateProgram();   // que se pueda cargar, antes de crear la carpeta
        var name = UniqueName(Clean(wanted.Length > 0 ? wanted : package.ProjectName));
        var profile = PlcSettings.Profiles.FirstOrDefault(p => p.Equals(package.Plc?.Platform, StringComparison.OrdinalIgnoreCase)) ?? "TIA";
        var settings = new PlcSettings(name, Free(i => i.Settings.Port, 4840), profile)
        {
            EipPort = Free(i => i.Settings.EipPort, 44818),
            ModbusPort = Instances.All(i => i.Settings.ModbusPort != 502) ? 502 : Free(i => i.Settings.ModbusPort, 1502)
        };
        SimulatedPlc.Create(_services.Store, settings, package);
        var item = new InstanceViewModel(SimulatedPlc.Open(_services.Store, name), this);
        Instances.Add(item);
        Selected = item;
        Status = L.F("Instancia «{0}» creada con el programa «{1}». Enciéndela para publicarla.", name, package.ProjectName);
        return item;
    }

    private async Task RemoveSelectedAsync()
    {
        if (Selected is not { IsOn: false } item) return;
        if (!_services.Confirm(L.F("¿Borrar la instancia «{0}» y su carpeta (programa, retentiva y registro)?", item.Name))) return;
        await item.Plc.DisposeAsync();
        _services.Store.DeleteInstance(item.Name);
        Instances.Remove(item);
        Selected = Instances.FirstOrDefault();
        Status = L.F("Instancia «{0}» borrada.", item.Name);
    }

    private int Free(Func<InstanceViewModel, int> port, int first)
    {
        var used = Instances.Select(port).ToHashSet();
        var p = first;
        while (used.Contains(p)) p++;
        return p;
    }

    private static string Clean(string name)
    {
        var clean = new string(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return clean.Length == 0 ? "PLC_1" : clean;
    }

    private string UniqueName(string wanted)
    {
        var existing = _services.Store.ListInstances();
        var name = wanted;
        for (var n = 2; existing.Contains(name, StringComparer.OrdinalIgnoreCase); n++) name = $"{wanted}_{n}";
        return name;
    }

    /// <summary>Al cerrar la ventana: apaga las encendidas (guarda su retentiva).</summary>
    public async Task ShutdownAsync()
    {
        foreach (var item in Instances) await item.Plc.DisposeAsync();
    }

    // ---------- Idioma ----------

    public IReadOnlyList<LanguageChoice> Languages =>
        [.. AppLanguages.All.Select(l => new LanguageChoice(l, l.Code == CurrentLanguage.Code, ChooseLanguage))];

    public AppLanguage CurrentLanguage => AppLanguages.Resolve(_services.SavedLanguage, _services.SystemCulture);

    private void ChooseLanguage(AppLanguage language)
    {
        if (language.Code == CurrentLanguage.Code) return;
        if (Instances.Any(i => i.IsOn) && !_services.Confirm(L.T("Para cambiar de idioma el programa se vuelve a abrir y se apagan las instancias. ¿Seguir?"))) return;
        _services.SavedLanguage = language.Code;
        _services.Restart();
    }
}

/// <summary>Una instancia en la ventana: su estado, sus botones, su configuración, sus variables y su registro.</summary>
public sealed class InstanceViewModel : ObservableObject
{
    private readonly SoftNodesOpenViewModel _owner;
    private string _filter = "";
    private bool _busy;

    public InstanceViewModel(SimulatedPlc plc, SoftNodesOpenViewModel owner)
    {
        Plc = plc;
        _owner = owner;
        PowerCommand = new RelayCommand(() => _ = Do(() => IsOn ? Plc.PowerOffAsync() : Plc.PowerOnAsync()), () => !_busy);
        RunCommand = new RelayCommand(() => Plc.Run(), () => IsOn && !IsRunning);
        StopCommand = new RelayCommand(() => Plc.Stop(), () => IsOn && IsRunning);
        ReloadCommand = new RelayCommand(() => _ = Do(async () => { await Plc.ReloadAsync(); }), () => !_busy);
        ColdStartCommand = new RelayCommand(() => _ = Do(async () => { if (owner.Services.Confirm(ColdStartQuestion)) await Plc.ReloadAsync(coldStart: true); }), () => !_busy);
        LoadProgramCommand = new RelayCommand(() => _ = LoadProgramAsync(), () => !_busy);
        foreach (var line in plc.RecentLog()) Log.Add(line);
        plc.Logged += (_, line) => owner.Services.OnUiThread(() =>
        {
            Log.Add(line);
            while (Log.Count > 500) Log.RemoveAt(0);
        });
        plc.Instance.StateChanged += (_, _) => owner.Services.OnUiThread(Refresh);
        RebuildVariables();
    }

    public SimulatedPlc Plc { get; }

    public string Name => Plc.Name;

    public PlcSettings Settings => Plc.Settings;

    public bool IsOn => Plc.IsOn;

    public bool IsRunning => Plc.Instance.Mode == SoftMode.Running;

    public bool CanEditSettings => !IsOn && !_busy;

    /// <summary>«RUN», «STOP», «Apagada» o «STOP por fallo: …».</summary>
    public string State => !IsOn ? L.T("Apagada") : IsRunning ? "RUN" : Plc.Instance.Fault is { } fault ? L.F("STOP por fallo: {0}", fault.Message) : "STOP";

    public bool HasFault => Plc.Instance.Fault is not null;

    public string Endpoints => !IsOn ? "" : string.Join("   ·   ", new[] { Plc.OpcUaEndpoint, Plc.EipEndpoint, Plc.ModbusEndpoint }.OfType<string>());

    /// <summary>El programa cargado: el proyecto y el autómata del que salió.</summary>
    public string ProgramText => Plc.Instance.Package.Plc is { Name.Length: > 0 } card
        ? L.F("Programa «{0}» (autómata {1}) · {2} ciclos", Plc.Instance.Package.ProjectName, card.Name, Plc.Instance.Cycles)
        : L.F("Programa «{0}» · {1} ciclos", Plc.Instance.Package.ProjectName, Plc.Instance.Cycles);

    public ICommand PowerCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand ColdStartCommand { get; }
    public ICommand LoadProgramCommand { get; }

    public static string ColdStartQuestion => L.T("¿Arranque en frío? Todas las variables vuelven a su valor inicial, también la retentiva.");

    public string PowerText => IsOn ? L.T("Apagar: para el PLC, guarda la memoria retentiva y cierra los servidores.") : L.T("Encender: servidores OPC UA, EtherNet/IP y Modbus, la retentiva de la última vez y RUN si está marcado.");

    // ---------- Configuración ----------

    public IReadOnlyList<string> Profiles => PlcSettings.Profiles;

    public string Profile { get => Settings.Profile; set => Change(Settings with { Profile = value }); }
    public int Port { get => Settings.Port; set => Change(Settings with { Port = Math.Clamp(value, 1, 65535) }); }
    public int EipPort { get => Settings.EipPort; set => Change(Settings with { EipPort = Math.Clamp(value, 0, 65535) }); }
    public int ModbusPort { get => Settings.ModbusPort; set => Change(Settings with { ModbusPort = Math.Clamp(value, 0, 65535) }); }
    public int CycleMs { get => Settings.CycleMs; set => Change(Settings with { CycleMs = Math.Clamp(value, 1, 10_000) }); }
    public double TimeScale { get => Settings.TimeScale; set => Change(Settings with { TimeScale = Math.Clamp(value, 0.1, 1000) }); }
    public bool AutoRun { get => Settings.AutoRun; set => Change(Settings with { AutoRun = value }); }
    public bool AutoReload { get => Settings.AutoReload; set => Change(Settings with { AutoReload = value }); }

    private void Change(PlcSettings settings)
    {
        if (settings == Settings) return;
        try
        {
            Plc.UpdateSettings(settings);
        }
        catch (Exception e) when (e is InvalidOperationException or SoftPackageException)
        {
            _owner.Status = e.Message;
        }

        Refresh();
    }

    // ---------- Variables ----------

    /// <summary>Las variables del programa que casan con el filtro (las primeras <see cref="MaxRows"/>).</summary>
    public ObservableCollection<VariableRow> Variables { get; } = [];

    public const int MaxRows = 1000;

    private List<(string Path, Slot Slot)> _all = [];

    /// <summary>Lo que se busca en el nombre de las variables (sin mayúsculas).</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (value == _filter) return;
            _filter = value ?? "";
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public string VariablesText => _all.Count == 0 ? L.T("El programa no tiene variables.")
        : Variables.Count < Matching ? L.F("{0} de {1} variables (escribe para buscar)", Variables.Count, Matching)
        : L.F("{0} variables", Variables.Count);

    private int Matching => _filter.Length == 0 ? _all.Count : _all.Count(v => v.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase));

    /// <summary>Vuelve a recorrer la memoria (al cargar otro programa: las casillas son otras).</summary>
    private void RebuildVariables()
    {
        var program = Plc.Instance.Program;
        var list = new List<(string, Slot)>();
        lock (program.Sync)
        {
            Walk(program.Model.DataName, program.Data, list);
            Walk(program.Model.IoName, program.Io, list);
        }

        _all = list;
        _program = program;
        ApplyFilter();
    }

    private SoftProgram? _program;

    private static void Walk(string path, StructValue value, List<(string, Slot)> list)
    {
        foreach (var (name, slot) in value.Members) Walk($"{path}.{name}", slot, list);
    }

    private static void Walk(string path, Slot slot, List<(string, Slot)> list)
    {
        switch (slot.Value)
        {
            case StructValue s:
                Walk(path, s, list);
                break;
            case ArrayValue a:
                for (var i = 0; i < a.Items.Length; i++) Walk($"{path}[{a.Lower + i}]", a.Items[i], list);
                break;
            case FbInstance:
                break;   // dentro de un bloque: en el depurador de SoftNodes Pro
            default:
                list.Add((path, slot));
                break;
        }
    }

    private void ApplyFilter()
    {
        Variables.Clear();
        foreach (var (path, slot) in _all.Where(v => _filter.Length == 0 || v.Path.Contains(_filter, StringComparison.OrdinalIgnoreCase)).Take(MaxRows))
            Variables.Add(new VariableRow(path, slot, this));
        RefreshValues();
        OnPropertyChanged(nameof(VariablesText));
    }

    private void RefreshValues()
    {
        if (_program is not { } program) return;
        lock (program.Sync)
            foreach (var row in Variables) row.Read();
    }

    /// <summary>Escribe a mano en una casilla (como forzar una entrada en el PLC). Devuelve el problema, o null.</summary>
    internal string? Write(VariableRow row, string text)
    {
        if (_program is not { } program) return null;
        try
        {
            var value = ParseValue(text, row.Slot.Type);
            lock (program.Sync) row.Slot.Value = value;
            _owner.Status = L.F("{0} := {1}", row.Path, text.Trim());
            return null;
        }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            return L.F("«{0}» no vale para un {1}.", text.Trim(), row.Slot.Type.Name);
        }
    }

    private static object ParseValue(string text, StType type)
    {
        var t = text.Trim();
        object value = type.Kind switch
        {
            StTypeKind.Bool => t.ToUpperInvariant() switch { "TRUE" or "1" => true, "FALSE" or "0" => false, _ => throw new FormatException() },
            StTypeKind.Int => t.StartsWith("16#", StringComparison.Ordinal) ? Convert.ToInt64(t[3..].Replace("_", ""), 16) : long.Parse(t, NumberStyles.Integer, CultureInfo.InvariantCulture),
            StTypeKind.Real => double.Parse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture),
            StTypeKind.Time => long.Parse(t.ToUpperInvariant().Replace("T#", "").Replace("MS", ""), NumberStyles.Integer, CultureInfo.InvariantCulture),
            StTypeKind.String => t,
            _ => throw new InvalidCastException()
        };
        return StInterpreter.Coerce(value, type);
    }

    // ---------- Registro ----------

    public ObservableCollection<string> Log { get; } = [];

    // ---------- Reloj ----------

    internal void Tick()
    {
        if (Settings.AutoReload && !_busy && Plc.PollProgramFile() == ProgramFileState.Changed) _ = Do(async () => { await Plc.ReloadAsync(); });
        RefreshValues();
        OnPropertyChanged(nameof(ProgramText));
    }

    private async Task LoadProgramAsync()
    {
        if (_owner.Services.ChooseProgram() is not { } chosen) return;
        await Do(async () => { await Plc.LoadAsync(SoftPackage.FromZip(chosen.Bytes)); });
    }

    private async Task Do(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        Refresh();
        var program = Plc.Instance.Program;
        try
        {
            await action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _owner.Status = L.F("{0}: {1}", Name, e.Message);
        }
        finally
        {
            _busy = false;
            if (!ReferenceEquals(program, Plc.Instance.Program)) RebuildVariables();
            Refresh();
        }
    }

    private void Refresh()
    {
        foreach (var name in new[] { nameof(IsOn), nameof(IsRunning), nameof(State), nameof(HasFault), nameof(Endpoints), nameof(ProgramText), nameof(PowerText),
                     nameof(CanEditSettings), nameof(Profile), nameof(Port), nameof(EipPort), nameof(ModbusPort), nameof(CycleMs), nameof(TimeScale), nameof(AutoRun),
                     nameof(AutoReload) })
            OnPropertyChanged(name);
        foreach (var command in new[] { PowerCommand, RunCommand, StopCommand, ReloadCommand, ColdStartCommand, LoadProgramCommand })
            (command as RelayCommand)?.RaiseCanExecuteChanged();
        _owner.Changed();
    }
}

/// <summary>Una variable del programa: su ruta, su tipo y su valor (que se puede escribir a mano).</summary>
public sealed class VariableRow(string path, Slot slot, InstanceViewModel owner) : ObservableObject
{
    private string _value = "";
    private string _problem = "";

    public string Path { get; } = path;
    internal Slot Slot { get; } = slot;
    public string Type => Slot.Type.Name;

    public string Value
    {
        get => _value;
        set
        {
            if (value == _value) return;
            Problem = owner.Write(this, value ?? "") ?? "";
            OnPropertyChanged();
        }
    }

    public string Problem
    {
        get => _problem;
        private set
        {
            if (value == _problem) return;
            _problem = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Lee el valor (quien llama tiene tomada la memoria).</summary>
    internal void Read()
    {
        var text = Slot.Value switch
        {
            bool b => b ? "TRUE" : "FALSE",
            double d => d.ToString("0.######", CultureInfo.InvariantCulture),
            long l when Slot.Type.Kind == StTypeKind.Time => $"T#{l}ms",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var v => v?.ToString() ?? ""
        };
        if (text == _value) return;
        _value = text;
        OnPropertyChanged(nameof(Value));
    }
}
