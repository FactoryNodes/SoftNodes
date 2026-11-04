using FactoryNodes.SoftNodes.Runtime;

namespace FactoryNodes.SoftNodes;

public enum SoftMode
{
    /// <summary>La CPU no ejecuta; la memoria se conserva y se puede leer y escribir (como un PLC en STOP).</summary>
    Stopped,

    Running
}

/// <summary>Qué pasó al cargar un programa nuevo sobre el que ya estaba (<see cref="SoftInstance.Load"/>).</summary>
/// <param name="Kept">Variables que siguen con el mismo nombre y tipo: conservan su valor.</param>
/// <param name="Added">Variables nuevas: arrancan con su valor inicial.</param>
/// <param name="Removed">Variables que ya no están.</param>
/// <param name="Changed">Variables con el mismo nombre y otro tipo: arrancan con su valor inicial.</param>
public sealed record SoftLoadReport(int Kept, int Added, int Removed, int Changed)
{
    /// <summary>Si la estructura cambió, FactoryNodes ofrece el arranque en frío.</summary>
    public bool StructureChanged => Added + Removed + Changed > 0;
}

/// <summary>
/// Un «PLC» de SoftNodes (como una instancia de PLCSIM Advanced): un programa cargado, su memoria, el modo
/// (<see cref="SoftMode"/>) y el reloj del ciclo. En marcha ejecuta un ciclo cada <see cref="CycleMs"/> de tiempo del PLC;
/// con <see cref="TimeScale"/> mayor que 1 el reloj va acelerado. Un fallo de ejecución lo deja en STOP con el motivo
/// (<see cref="Fault"/>), como una CPU. No toca disco ni red: quien lo aloja (el exe, la Simulación de FactoryNodes) guarda y
/// publica.
/// </summary>
public sealed class SoftInstance : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;
    private Task? _loop;
    private bool _hookPrimed;

    public SoftInstance(string name, SoftPackage package, SoftProfile? profile = null)
    {
        Name = name;
        Package = package;
        Profile = profile ?? SoftProfile.Tia;
        Program = package.CreateProgram(Profile);
    }

    /// <summary>El PLC que simula: las funciones <c>/// COMPAT</c> que ejecuta son las suyas (<see cref="SoftProfile"/>).</summary>
    public SoftProfile Profile { get; private set; }

    /// <summary>
    /// Cambia de perfil: se carga otra vez el mismo programa con las funciones del nuevo PLC, como una descarga que conserva lo que
    /// sigue igual (nombre y tipo). Quien publica la memoria por OPC UA debe volver a tomar <see cref="Program"/>.
    /// </summary>
    public SoftLoadReport SetProfile(SoftProfile profile)
    {
        if (profile.Name == Profile.Name) return new SoftLoadReport(Kept: 0, Added: 0, Removed: 0, Changed: 0);
        var previous = Profile;
        Profile = profile;
        try
        {
            return Load(Package);
        }
        catch
        {
            Profile = previous;   // el programa no se pudo cargar con el perfil nuevo: el PLC sigue como estaba
            throw;
        }
    }

    public string Name { get; }

    public SoftPackage Package { get; private set; }

    /// <summary>La memoria y el programa en curso. Cambia al <see cref="Load"/>: quien lo publica (OPC UA) debe volver a tomarlo.</summary>
    public SoftProgram Program { get; private set; }

    public SoftMode Mode { get; private set; }

    /// <summary>Lo que avanza el reloj del PLC en cada ciclo, en milisegundos.</summary>
    public int CycleMs { get; set; } = 100;

    /// <summary>Velocidad del reloj respecto al real (1 = tiempo real, 10 = diez veces más deprisa).</summary>
    public double TimeScale { get; set; } = 1;

    /// <summary>Ciclos ejecutados desde que se creó o se arrancó en frío.</summary>
    public long Cycles { get; private set; }

    /// <summary>El fallo que dejó la CPU en STOP, o null.</summary>
    public StRuntimeException? Fault { get; private set; }

    /// <summary>
    /// Lo que pasa fuera del programa tras cada ciclo, con la memoria tomada (la planta simulada escribe las entradas
    /// físicas). El programa lo ve en el ciclo siguiente, como una entrada que cambió entre dos ciclos.
    /// </summary>
    public Action<SoftProgram>? AfterCycle { get; set; }

    /// <summary>Cambió el modo o hubo un fallo.</summary>
    public event EventHandler? StateChanged;

    // ---------------------------------------------------------------- Modo

    public void Run()
    {
        lock (_gate)
        {
            if (Mode == SoftMode.Running) return;
            Fault = null;
            FaultSnapshot = null;
            Mode = SoftMode.Running;
            _cancel = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cancel.Token));
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        Task? loop;
        lock (_gate)
        {
            if (Mode == SoftMode.Stopped) return;
            Mode = SoftMode.Stopped;
            _cancel?.Cancel();
            loop = _loop;
        }

        loop?.Wait();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Un solo ciclo con la CPU en STOP (paso a paso por ciclo).</summary>
    public void StepCycle()
    {
        if (Mode == SoftMode.Running) throw new InvalidOperationException("Para el PLC antes de ir ciclo a ciclo.");
        ScanOnce();
    }

    private async Task LoopAsync(CancellationToken cancel)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, CycleMs / Math.Max(0.001, TimeScale)));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancel))
            {
                if (!ScanOnce()) return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Un ciclo; si falla, la CPU pasa a STOP con la foto del fallo y devuelve false. Se recorre sentencia a sentencia (lo mismo que
    /// <see cref="SoftProgram.Scan"/>) para poder parar a mitad: en un punto de parada (<see cref="BreakAt"/>), por una pausa
    /// (<see cref="RequestPause"/>) o por el watchdog (<see cref="WatchdogMs"/>). Al parar a mitad, la CPU queda en STOP con el ciclo
    /// a medias y lo recoge quien escucha <see cref="Paused"/> (el depurador).
    /// </summary>
    private bool ScanOnce()
    {
        var program = Program;
        StepPoint? last = null;
        IEnumerator<StepPoint>? steps = null;
        try
        {
            PrimeHook(program);
            var paused = false;
            lock (program.Sync)
            {
                steps = program.ScanSteps(CycleMs).GetEnumerator();
                var pause = Interlocked.Exchange(ref _pauseRequested, 0) == 1;
                var breakAt = BreakAt;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var count = 0;
                while (steps.MoveNext())
                {
                    last = steps.Current;
                    if (pause || breakAt?.Invoke(steps.Current) == true)
                    {
                        paused = true;
                        break;
                    }

                    if ((++count & 1023) == 0 && clock.ElapsedMilliseconds > WatchdogMs)
                        throw Watchdog(steps.Current);
                }
            }

            if (paused)
            {
                lock (_gate) Mode = SoftMode.Stopped;
                Paused?.Invoke(this, new SoftPausedEventArgs(steps, last!.Value));
                StateChanged?.Invoke(this, EventArgs.Empty);
                return false;
            }

            steps.Dispose();
            CompleteCycle(program);
            return true;
        }
        catch (StRuntimeException e)
        {
            steps?.Dispose();
            ReportFault(e, last);
            return false;
        }
    }

    private void PrimeHook(SoftProgram program)
    {
        if (AfterCycle is not { } afterCycle || _hookPrimed) return;
        // Con el programa recién cargado las entradas aún están a 0: se ponen antes del primer ciclo, como una
        // instalación que ya estaba ahí (si no, el guardamotor «caído» del primer ciclo dejaría su alarma memorizada).
        lock (program.Sync) afterCycle(program);
        _hookPrimed = true;
    }

    private void CompleteCycle(SoftProgram program)
    {
        if (AfterCycle is { } afterCycle)
            lock (program.Sync) afterCycle(program);
        if (Links is { } links)
            lock (program.Sync) links.Tick(program);
        Cycles++;
    }

    /// <summary>
    /// La tarea de comunicación de la transferencia de datos (PENDIENTES 167): al final de cada ciclo recoge las lecturas de los remotos y lanza
    /// las que pide el bloque <c>EipLink</c>. Null = SoftNodes no ejecuta los enlaces (quedan caídos).
    /// </summary>
    public SoftLinkRunner? Links { get; set; }

    private StRuntimeException Watchdog(StepPoint at) =>
        new($"Watchdog: el ciclo lleva más de {WatchdogMs} ms sin terminar (¿un WHILE o un REPEAT que no acaba?).", at.Frame.File, at.Line);

    // ---------------------------------------------------------------- Depuración (PENDIENTES 78 y 79)

    private int _pauseRequested;

    /// <summary>
    /// Dónde parar el ciclo en RUN (los puntos de parada del depurador): se pregunta antes de cada sentencia. Null = no se para.
    /// Sin puntos de parada debe ser null, para no preguntar en balde.
    /// </summary>
    public Func<StepPoint, bool>? BreakAt { get; set; }

    /// <summary>«Pausa» con la CPU en RUN: el ciclo siguiente se para en su primera sentencia (un ciclo entero dura una fracción de milisegundo).</summary>
    public void RequestPause() => Interlocked.Exchange(ref _pauseRequested, 1);

    /// <summary>
    /// La CPU paró a mitad de un ciclo (punto de parada o pausa): se queda en STOP y el ciclo a medias va en el aviso para que el
    /// depurador lo siga. Se lanza desde el hilo del ciclo.
    /// </summary>
    public event EventHandler<SoftPausedEventArgs>? Paused;

    /// <summary>Lo más que puede durar un ciclo (tiempo real, en ms) antes de que la CPU pase a STOP por el watchdog.</summary>
    public int WatchdogMs { get; set; } = 2000;

    /// <summary>La foto del fallo que dejó la CPU en STOP (PENDIENTES 79), o null.</summary>
    public FaultSnapshot? FaultSnapshot { get; private set; }

    /// <summary>
    /// La CPU pasa a STOP por un fallo, con su foto (también cuando falla un ciclo que se recorre paso a paso). <paramref name="at"/>:
    /// la sentencia en la que estaba.
    /// </summary>
    public void ReportFault(StRuntimeException fault, StepPoint? at)
    {
        var program = Program;
        FaultSnapshot? snapshot;
        lock (program.Sync) snapshot = FaultSnapshot.Capture(fault, at, program, Cycles + 1);
        lock (_gate)
        {
            Fault = fault;
            FaultSnapshot = snapshot;
            Mode = SoftMode.Stopped;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Un ciclo recorrido paso a paso terminó (sus salidas físicas ya están hechas): lo que pasa fuera del programa tras cada ciclo
    /// (la planta) y la cuenta de ciclos, como en uno normal.
    /// </summary>
    public void CompleteSteppedCycle() => CompleteCycle(Program);

    /// <summary>Antes del primer ciclo recorrido paso a paso con un programa recién cargado: las entradas de la planta, como en RUN.</summary>
    public void PrepareSteppedCycle() => PrimeHook(Program);

    // ---------------------------------------------------------------- Cargar

    /// <summary>
    /// Carga un programa nuevo como una descarga de PLCSIM: se para la CPU, se sustituye el programa, las variables que
    /// siguen con el mismo nombre y tipo conservan su valor (salvo en <paramref name="coldStart"/>) y, si estaba en marcha,
    /// vuelve a marchar. Quien publica la memoria por OPC UA debe volver a tomar <see cref="Program"/>.
    /// </summary>
    public SoftLoadReport Load(SoftPackage package, bool coldStart = false)
    {
        var next = package.CreateProgram(Profile);   // si el paquete no vale, falla aquí y el PLC sigue como estaba
        var wasRunning = Mode == SoftMode.Running;
        Stop();

        SoftLoadReport report;
        lock (Program.Sync)
        {
            var image = MemoryImage.Capture(Program);
            report = coldStart ? new SoftLoadReport(0, 0, 0, 0) : image.RestoreInto(next);
            Package = package;
            Program = next;
            Fault = null;
            FaultSnapshot = null;
            if (coldStart) Cycles = 0;
            _hookPrimed = false;
        }

        if (wasRunning) Run();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return report;
    }

    // ---------------------------------------------------------------- Retentiva

    /// <summary>La memoria de datos (<c>hw_P</c> y las señales) como pares ruta/valor, para guardarla al apagar.</summary>
    public IReadOnlyDictionary<string, string> CaptureRetentive()
    {
        lock (Program.Sync) return MemoryImage.CaptureRetentive(Program).ToText();
    }

    /// <summary>Devuelve a la memoria lo que se guardó al apagar; lo que ya no existe se ignora.</summary>
    public SoftLoadReport RestoreRetentive(IReadOnlyDictionary<string, string> saved)
    {
        lock (Program.Sync) return MemoryImage.FromText(saved).RestoreInto(Program);
    }

    // ---------------------------------------------------------------- Monitorizar desde fuera (PENDIENTES 170)

    /// <summary>
    /// Toda la memoria (datos, señales y las internas de cada instancia de bloque) como pares ruta/valor: lo que la Factory Box da por su API
    /// para que el depurador de SoftNodes Pro pinte los valores de la caja sin pararla.
    /// </summary>
    public IReadOnlyDictionary<string, string> CaptureMemory()
    {
        lock (Program.Sync) return MemoryImage.CaptureAll(Program).ToText();
    }

    /// <summary>Pone en esta instancia (parada) la memoria de otra, para pintarla con el depurador; lo que no existe aquí se ignora.</summary>
    public SoftLoadReport RestoreMemory(IReadOnlyDictionary<string, string> memory)
    {
        lock (Program.Sync) return MemoryImage.FromText(memory).RestoreInto(Program);
    }

    public void Dispose() => Stop();
}

/// <summary>La CPU paró a mitad de un ciclo: el ciclo a medias (<see cref="Steps"/>) y la sentencia que va a ejecutar (<see cref="At"/>).</summary>
public sealed class SoftPausedEventArgs(IEnumerator<StepPoint> steps, StepPoint at) : EventArgs
{
    public IEnumerator<StepPoint> Steps { get; } = steps;
    public StepPoint At { get; } = at;
}
