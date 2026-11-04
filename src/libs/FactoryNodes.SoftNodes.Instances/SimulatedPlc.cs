using System.Text.Json;
using System.Text.Json.Nodes;
using FactoryNodes.Iec61131.Emission;
using FactoryNodes.SoftNodes.Cip;
using FactoryNodes.SoftNodes.Modbus;
using FactoryNodes.SoftNodes.OpcUa;

namespace FactoryNodes.SoftNodes.Instances;

/// <summary>Lo único que una instancia necesita del disco: una carpeta por instancia, con ficheros por ruta relativa.</summary>
public interface IInstanceStore
{
    /// <summary>Nombres de las instancias (las carpetas con <see cref="SimulatedPlc.SettingsFile"/>).</summary>
    IReadOnlyList<string> ListInstances();

    string? Read(string instance, string path);

    void Write(string instance, string path, string text);

    byte[]? ReadBytes(string instance, string path);

    void WriteBytes(string instance, string path, byte[] content);

    /// <summary>Cuándo se escribió por última vez un fichero de la instancia (para notar que llegó otro programa), o null.</summary>
    DateTime? LastWrite(string instance, string path);

    void DeleteInstance(string instance);

    /// <summary>Carpeta de certificados del servidor OPC UA de la instancia.</summary>
    string PkiFolder(string instance);
}

/// <summary>
/// Lo que se configura de una instancia. Se guarda en <c>softnode.json</c>, el mismo fichero que SoftNodes Pro: lo que este no conoce (la planta,
/// el banco de pruebas…) se conserva tal cual al guardar, así que las mismas carpetas valen para los dos.
/// </summary>
/// <param name="Profile">«TIA» o «Sysmac»: el direccionamiento OPC UA y las funciones de compatibilidad.</param>
/// <param name="Port">Puerto OPC UA (cada instancia el suyo).</param>
/// <param name="EipPort">Puerto EtherNet/IP; 0 = sin servidor.</param>
/// <param name="ModbusPort">Puerto Modbus TCP; 0 = sin servidor (solo arranca si el programa trae su mapa).</param>
public sealed record PlcSettings(string Name, int Port = 4840, string Profile = "TIA", int CycleMs = 100, double TimeScale = 1, bool AutoRun = true,
    bool AutoReload = true, int EipPort = SoftCipServer.DefaultPort, int ModbusPort = SoftModbusServer.DefaultPort)
{
    public static readonly IReadOnlyList<string> Profiles = ["TIA", "Sysmac"];

    public IPlcAddressing Addressing() => Profile.Equals("Sysmac", StringComparison.OrdinalIgnoreCase) ? new SysmacAddressing() : new TiaAddressing();
}

/// <summary>Cómo está el fichero del programa respecto al cargado.</summary>
public enum ProgramFileState { Unchanged, Changing, Changed }

/// <summary>
/// Un PLC simulado del SoftNodes abierto (PENDIENTES 159): una carpeta con su programa (<c>programa.softnodes</c>, el que transfiere FactoryNodes),
/// su configuración y su memoria retentiva. Encendido, ejecuta el programa y lo sirve por OPC UA (direccionamiento de TIA o de Sysmac), EtherNet/IP y
/// Modbus TCP, así que FactoryNodes, una pantalla u otro PLC se conectan como a uno real. El programa no se edita aquí: se cambia en FactoryNodes y
/// se vuelve a transferir.
/// </summary>
public sealed class SimulatedPlc : IAsyncDisposable, ILinkNode
{
    public const string SettingsFile = "softnode.json";
    public const string ProgramFile = "programa.softnodes";
    public const string RetentiveFile = "retentiva.json";
    public const string LogFile = "registro.log";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IInstanceStore _store;
    private SoftOpcUaServer? _opcUa;
    private SoftCipServer? _eip;
    private SoftModbusServer? _modbus;
    private DateTime? _loadedWrite, _seenWrite;

    private SimulatedPlc(IInstanceStore store, PlcSettings settings, SoftInstance instance)
    {
        _store = store;
        Settings = settings;
        Instance = instance;
        _loadedWrite = ProgramFileWrite;
        // La transferencia de datos entre autómatas (PENDIENTES 167): la tarea de comunicación lee a los remotos con quien diga la aplicación
        Links = new SoftLinkRunner(link => LinkReaders?.Invoke(link));
        Links.ProblemChanged += (remote, problem) => Log(problem.Length == 0 ? $"Enlace con {remote}: lee bien." : $"Enlace con {remote}: {problem}.");
        Instance.Links = Links;
    }

    /// <summary>
    /// Quién lee los remotos de los enlaces de esta instancia (<see cref="SoftLinkRouter.ReaderFor"/>: otra instancia abierta, en memoria, o un PLC
    /// por su IP). Lo pone la aplicación; sin él, los enlaces quedan caídos.
    /// </summary>
    public Func<Iec61131.Emission.ProgramLink, ISoftLinkReader?>? LinkReaders { get; set; }

    /// <summary>Los enlaces con otros autómatas: su estado por remoto y «cortar enlace».</summary>
    public SoftLinkRunner Links { get; }

    public PlcSettings Settings { get; private set; }

    public SoftInstance Instance { get; }

    public string Name => Settings.Name;

    public bool IsOn => _opcUa is not null;

    /// <summary>Las direcciones a las que se conecta un cliente de esta máquina (null las que no están encendidas).</summary>
    public string? OpcUaEndpoint => _opcUa?.Endpoint;

    public string? EipEndpoint => _eip?.Endpoint;

    public string? ModbusEndpoint => _modbus is { } m ? $"modbus://localhost:{m.Port}" : null;

    /// <summary>Lo que se anota en <c>registro.log</c> (encendido, cargas, paros, fallos, clientes).</summary>
    public event EventHandler<string>? Logged;

    // ---------------------------------------------------------------- Crear y abrir

    /// <summary>Crea la carpeta de una instancia nueva con un programa (falla si ya existe).</summary>
    public static void Create(IInstanceStore store, PlcSettings settings, SoftPackage package)
    {
        if (store.ListInstances().Contains(settings.Name, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Ya existe la instancia «{settings.Name}».");
        store.Write(settings.Name, SettingsFile, Merge(null, settings));
        store.WriteBytes(settings.Name, ProgramFile, package.ToZip());
    }

    /// <summary>Abre una instancia (sin encenderla): su configuración y su programa.</summary>
    public static SimulatedPlc Open(IInstanceStore store, string name)
    {
        var settings = ReadSettings(store, name);
        var package = ReadProgram(store, name);
        var instance = new SoftInstance(settings.Name, package, SoftProfile.ByName(settings.Profile))
            { CycleMs = settings.CycleMs, TimeScale = settings.TimeScale };
        return new SimulatedPlc(store, settings, instance);
    }

    public static PlcSettings ReadSettings(IInstanceStore store, string name)
    {
        var text = store.Read(name, SettingsFile) ?? throw new SoftPackageException($"La instancia «{name}» no tiene {SettingsFile}.");
        var node = JsonNode.Parse(text) as JsonObject ?? throw new SoftPackageException($"{SettingsFile} de «{name}» está vacío.");
        var defaults = new PlcSettings(name);
        T Get<T>(string key, T fallback) => node.TryGetPropertyValue(key, out var v) && v is not null ? v.Deserialize<T>() ?? fallback : fallback;
        return new PlcSettings(name, Get("Port", defaults.Port), Get("Profile", defaults.Profile), Get("CycleMs", defaults.CycleMs),
            Get("TimeScale", defaults.TimeScale), Get("AutoRun", defaults.AutoRun), Get("AutoReload", defaults.AutoReload),
            Get("EipPort", defaults.EipPort), Get("ModbusPort", defaults.ModbusPort));
    }

    /// <summary>Lo de <paramref name="settings"/> sobre el <c>softnode.json</c> que había (lo que no es de aquí se queda como estaba).</summary>
    private static string Merge(string? existing, PlcSettings settings)
    {
        var node = (existing is null ? null : JsonNode.Parse(existing) as JsonObject) ?? new JsonObject();
        node["Name"] = settings.Name;
        node["Port"] = settings.Port;
        node["Profile"] = settings.Profile;
        node["CycleMs"] = settings.CycleMs;
        node["TimeScale"] = settings.TimeScale;
        node["AutoRun"] = settings.AutoRun;
        node["AutoReload"] = settings.AutoReload;
        node["EipPort"] = settings.EipPort;
        node["ModbusPort"] = settings.ModbusPort;
        return node.ToJsonString(Json);
    }

    private static SoftPackage ReadProgram(IInstanceStore store, string name) =>
        SoftPackage.FromZip(store.ReadBytes(name, ProgramFile) ?? throw new SoftPackageException($"La instancia «{name}» no tiene {ProgramFile}."));

    // ---------------------------------------------------------------- Configuración

    /// <summary>Cambia la configuración y la guarda. El perfil y los puertos, solo apagada; el ciclo y la velocidad valen en el próximo RUN.</summary>
    public void UpdateSettings(PlcSettings settings)
    {
        settings = settings with { Name = Settings.Name };
        if (IsOn && (settings.Port != Settings.Port || settings.EipPort != Settings.EipPort || settings.ModbusPort != Settings.ModbusPort ||
                     !settings.Profile.Equals(Settings.Profile, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("El perfil y los puertos solo se cambian con la instancia apagada.");
        var profile = SoftProfile.ByName(settings.Profile);
        if (profile.Name != Instance.Profile.Name)
        {
            Instance.SetProfile(profile);
            Log($"Perfil {profile.Name}.");
        }

        Settings = settings with { Profile = profile.Name };
        Instance.CycleMs = Settings.CycleMs;
        Instance.TimeScale = Settings.TimeScale;
        _store.Write(Name, SettingsFile, Merge(_store.Read(Name, SettingsFile), Settings));
    }

    // ---------------------------------------------------------------- Encender y apagar

    /// <summary>Enciende: la retentiva de la última vez, los servidores y, si <see cref="PlcSettings.AutoRun"/>, RUN.</summary>
    public async Task PowerOnAsync()
    {
        if (IsOn) return;
        if (_store.Read(Name, RetentiveFile) is { } saved && JsonSerializer.Deserialize<Dictionary<string, string>>(saved) is { } image)
        {
            var report = Instance.RestoreRetentive(image);
            Log($"Retentiva recuperada: {report.Kept} variables.");
        }

        await StartServersAsync();
        Log($"Encendida: {OpcUaEndpoint}" + (EipEndpoint is { } eip ? $", {eip}" : "") + (ModbusEndpoint is { } mb ? $", {mb}" : "") + $" ({Settings.Profile}).");
        if (Settings.AutoRun) Run();
    }

    public async Task PowerOffAsync()
    {
        if (!IsOn) return;
        Instance.Stop();
        SaveRetentive();
        await StopServersAsync();
        Log("Apagada.");
    }

    public void Run()
    {
        Instance.Run();
        Log("RUN.");
    }

    public void Stop()
    {
        Instance.Stop();
        Log(Instance.Fault is { } fault ? $"STOP por fallo: {fault.Message}" : "STOP.");
    }

    public void SaveRetentive() => _store.Write(Name, RetentiveFile, JsonSerializer.Serialize(Instance.CaptureRetentive(), Json));

    // ---------------------------------------------------------------- El programa

    /// <summary>Cuándo se escribió el programa de la carpeta (null si no hay fichero).</summary>
    public DateTime? ProgramFileWrite => _store.LastWrite(Name, ProgramFile);

    /// <summary>¿Es otro el programa de la carpeta que el cargado? Entonces FactoryNodes transfirió uno nuevo.</summary>
    public bool ProgramChangedOnDisk => ProgramFileWrite is { } written && written != _loadedWrite;

    /// <summary>
    /// Mira el fichero del programa (lo llama el reloj de la ventana). Hasta que no se repite la misma fecha en dos miradas seguidas está
    /// <see cref="ProgramFileState.Changing"/> (puede estar a medio copiar) y no se carga.
    /// </summary>
    public ProgramFileState PollProgramFile()
    {
        if (ProgramFileWrite is not { } written || written == _loadedWrite)
        {
            _seenWrite = null;
            return ProgramFileState.Unchanged;
        }

        if (written != _seenWrite)
        {
            _seenWrite = written;
            return ProgramFileState.Changing;
        }

        return ProgramFileState.Changed;
    }

    /// <summary>
    /// Vuelve a leer el programa de la carpeta y lo carga como una descarga (para, carga, conserva lo que sigue igual y vuelve a RUN si estaba). Los
    /// servidores se rehacen con la memoria nueva: los clientes se reconectan, como tras una descarga.
    /// </summary>
    public async Task<SoftLoadReport> ReloadAsync(bool coldStart = false)
    {
        var written = ProgramFileWrite;
        var package = ReadProgram(_store, Name);
        var wasOn = IsOn;
        if (wasOn) await StopServersAsync();
        var report = Instance.Load(package, coldStart);
        (_loadedWrite, _seenWrite) = (written, null);
        Log(coldStart ? "Programa cargado: arranque en frío."
            : report.StructureChanged ? $"Programa cargado: {report.Kept} variables conservadas, {report.Added} nuevas, {report.Removed} eliminadas, {report.Changed} con otro tipo."
            : $"Programa cargado: {report.Kept} variables conservadas.");
        if (wasOn) await StartServersAsync();
        return report;
    }

    /// <summary>Pone otro programa en la carpeta (la ventana: «Cargar programa…») y lo carga.</summary>
    public Task<SoftLoadReport> LoadAsync(SoftPackage package)
    {
        _store.WriteBytes(Name, ProgramFile, package.ToZip());
        return ReloadAsync();
    }

    // ---------------------------------------------------------------- Servidores

    private async Task StartServersAsync()
    {
        _opcUa = new SoftOpcUaServer(Instance.Program, Settings.Addressing(), Name, Settings.Port, _store.PkiFolder(Name));
        await _opcUa.StartAsync();
        if (Settings.EipPort > 0)
        {
            var eip = new SoftCipServer(Instance.Program, Name, Settings.EipPort);
            eip.Logged += Log;
            try
            {
                await eip.StartAsync();
                _eip = eip;
            }
            catch (System.Net.Sockets.SocketException e)
            {
                Log($"Sin servidor EtherNet/IP: el puerto {Settings.EipPort} está ocupado ({e.Message}).");
            }
        }

        if (Settings.ModbusPort > 0 && Instance.Program.Model.Modbus is { Entries.Count: > 0 } map)
        {
            var modbus = new SoftModbusServer(Instance.Program, map, Settings.ModbusPort);
            modbus.Logged += Log;
            foreach (var problem in modbus.Problems) Log("Modbus TCP: " + problem);
            try
            {
                await modbus.StartAsync();
                _modbus = modbus;
                Log($"Modbus TCP en el puerto {modbus.Port}: {modbus.Served} variables.");
            }
            catch (System.Net.Sockets.SocketException e)
            {
                Log($"Sin servidor Modbus TCP: el puerto {Settings.ModbusPort} está ocupado ({e.Message}).");
            }
        }
    }

    private async Task StopServersAsync()
    {
        if (_modbus is { } modbus) await modbus.StopAsync();
        _modbus = null;
        if (_eip is { } eip) await eip.StopAsync();
        _eip = null;
        if (_opcUa is { } opcUa) await opcUa.StopAsync();
        _opcUa = null;
    }

    // ---------------------------------------------------------------- Registro

    /// <summary>Las últimas líneas del registro de la instancia.</summary>
    public IReadOnlyList<string> RecentLog(int lines = 200) =>
        (_store.Read(Name, LogFile) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(lines).Select(l => l.TrimEnd('\r')).ToList();

    /// <summary>Una línea en el registro de la instancia (con la fecha) y en <see cref="Logged"/>; SoftNodes Pro anota aquí lo suyo.</summary>
    public void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        _store.Write(Name, LogFile, (_store.Read(Name, LogFile) ?? "") + line + Environment.NewLine);
        Logged?.Invoke(this, line);
    }

    public async ValueTask DisposeAsync()
    {
        await PowerOffAsync();
        Instance.Dispose();
    }
}
