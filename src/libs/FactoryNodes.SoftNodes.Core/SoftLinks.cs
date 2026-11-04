using System.Collections.Concurrent;
using FactoryNodes.Iec61131.Emission;
using FactoryNodes.SoftNodes.Runtime;

namespace FactoryNodes.SoftNodes;

/// <summary>
/// Lo que trae una lectura del remoto de un enlace (PENDIENTES 167): los valores de los campos de su <c>EIP_OUT_&lt;yo&gt;</c> por nombre
/// (bool, long, double o string, como los guarda el intérprete) o, si no se pudo, el problema y su código (<see cref="SoftLinkRunner"/>).
/// </summary>
public sealed record SoftLinkReading(IReadOnlyDictionary<string, object>? Values, string? Problem, ushort ErrorCode)
{
    public static SoftLinkReading Ok(IReadOnlyDictionary<string, object> values) => new(values, null, 0);

    public static SoftLinkReading Failed(string problem, ushort code) => new(null, problem, code);

    public bool IsOk => Values is not null;
}

/// <summary>
/// Quien lee el remoto de un enlace para <see cref="SoftLinkRunner"/>: por EtherNet/IP u OPC UA a un PLC, o en memoria a otra instancia del
/// mismo proceso. <c>Core</c> no tiene red: las implementaciones están en <c>SoftNodes.Hosting</c>.
/// </summary>
public interface ISoftLinkReader
{
    /// <param name="link">El enlace: el remoto, su IP y plataforma y la variable que hay que leerle (<see cref="ProgramLink.RemoteOut"/>).</param>
    /// <param name="fields">Los campos de la estructura, en el orden del tipo (la cabecera <c>Layout</c> y <c>Heartbeat</c> incluida).</param>
    Task<SoftLinkReading> ReadAsync(ProgramLink link, IReadOnlyList<string> fields, CancellationToken cancellation);

    /// <summary>
    /// Con un autómata externo (PENDIENTES 180): escribe <paramref name="writes"/> (etiqueta y valor) y lee <paramref name="reads"/> (etiquetas
    /// tal cual); los valores vuelven por etiqueta. Si falla una escritura, la ronda falla.
    /// </summary>
    Task<SoftLinkReading> ExchangeAsync(ProgramLink link, IReadOnlyList<string> reads, IReadOnlyList<(string Tag, object Value)> writes,
        CancellationToken cancellation) =>
        Task.FromResult(SoftLinkReading.Failed("este lector no habla con autómatas externos", SoftLinkRunner.NoReader));
}

/// <summary>
/// La tarea de comunicación de la transferencia de datos (PENDIENTES 167 y 169) en SoftNodes: lo que en un Omron hace <c>CIPUCMMRead</c>.
/// Al final de cada ciclo mira, por cada enlace del programa (<see cref="ProgramModel.Links"/>), el pulso <c>EIP_Status_R.Status.Trigger</c>
/// que da el bloque <c>EipLink</c>; con él, pide al lector la estructura <c>EIP_OUT_&lt;yo&gt;</c> del remoto y pone <c>Link_R_Busy</c>. Cuando la
/// lectura vuelve (en un ciclo posterior), copia los campos a <c>EIP_IN_R</c> y levanta <c>Link_R_Done</c>, o <c>Link_R_Error</c> con
/// <c>Link_R_ErrorID</c>; el bloque hace el resto (latido, plazo, <c>Connected</c>). Sin lector para un remoto, el enlace queda caído con el
/// código <see cref="NoReader"/>. <see cref="Cut"/> corta un enlace a sabiendas (banco de pruebas: «cortar enlace»).
/// Se llama con <see cref="SoftProgram.Sync"/> tomado; las lecturas corren aparte y sus resultados se recogen en el ciclo siguiente.
/// </summary>
public sealed class SoftLinkRunner
{
    /// <summary>Códigos de <c>Link_R_ErrorID</c> (WORD) que pone SoftNodes; los de un PLC real son los suyos.</summary>
    public const ushort NoReader = 0x0001;
    public const ushort Timeout = 0x0002;
    public const ushort BadRead = 0x0003;
    public const ushort CutByBench = 0x0004;

    private readonly Func<ProgramLink, ISoftLinkReader?> _readers;
    private readonly Dictionary<string, Task<SoftLinkReading>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _problems = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _cut = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="readers">El lector de cada enlace (null = no hay camino hasta ese remoto). Se pregunta en cada lectura: puede cambiar.</param>
    public SoftLinkRunner(Func<ProgramLink, ISoftLinkReader?> readers) => _readers = readers;

    /// <summary>Cuánto se espera una lectura antes de darla por fallida: cinco períodos, y al menos un segundo.</summary>
    public static TimeSpan TimeoutFor(ProgramLink link) => TimeSpan.FromMilliseconds(Math.Max(1000, 5 * Math.Max(1, link.PeriodMs)));

    /// <summary>Un problema nuevo o resuelto de un remoto («PLC_A: sin conexión…»; vacío = resuelto).</summary>
    public event Action<string, string>? ProblemChanged;

    /// <summary>El último problema de cada remoto (solo los que lo tienen).</summary>
    public IReadOnlyDictionary<string, string> Problems => _problems;

    /// <summary>Si un enlace está cortado a sabiendas (banco de pruebas).</summary>
    public bool IsCut(string remote)
    {
        lock (_cut) return _cut.Contains(remote);
    }

    /// <summary>Corta (o reanuda) el enlace con un remoto: las lecturas fallan con <see cref="CutByBench"/> y el bloque da el enlace por caído.</summary>
    public void Cut(string remote, bool cut)
    {
        lock (_cut)
        {
            if (cut) _cut.Add(remote);
            else _cut.Remove(remote);
        }
    }

    /// <summary>Un ciclo terminado: recoge las lecturas que han vuelto y lanza las que pide el pulso. Con <see cref="SoftProgram.Sync"/> tomado.</summary>
    public void Tick(SoftProgram program)
    {
        foreach (var link in program.Model.Links)
        {
            // Sin tarea de comunicación (BND075/BND076: sin IP, plataforma que no se lee): el enlace queda caído, como en un PLC
            if (!link.Pulls) continue;
            if (!program.Globals.TryGetValue(link.In, out var inSlot) || inSlot.Value is not StructValue input) continue;
            if (_pending.TryGetValue(link.Remote, out var pending))
            {
                if (!pending.IsCompleted) continue;
                _pending.Remove(link.Remote);
                Apply(program, link, input, pending.IsCompletedSuccessfully ? pending.Result : SoftLinkReading.Failed(pending.Exception?.GetBaseException().Message ?? "lectura fallida", BadRead));
                continue;
            }

            if (!Trigger(program, link)) continue;
            Set(program, link.Done, false);
            Set(program, link.Error, false);
            Set(program, link.Busy, true);
            _pending[link.Remote] = Start(link, input.Names.ToList(), link.External ? Writes(program, link) : []);
        }
    }

    /// <summary>Con un externo: lo que se le escribe en esta ronda, con los valores de <c>EIP_OUT_R</c> de ahora (con <c>Sync</c> tomado).</summary>
    private static List<(string Tag, object Value)> Writes(SoftProgram program, ProgramLink link)
    {
        var writes = new List<(string, object)>();
        if (!program.Globals.TryGetValue(link.Out, out var outSlot) || outSlot.Value is not StructValue output) return writes;
        foreach (var w in link.Writes)
            if (output.Members.TryGetValue(w.Field, out var slot) && slot.Value is { } value)
                writes.Add((w.Tag, value));
        return writes;
    }

    private Task<SoftLinkReading> Start(ProgramLink link, IReadOnlyList<string> fields, IReadOnlyList<(string Tag, object Value)> writes)
    {
        if (IsCut(link.Remote)) return Task.FromResult(SoftLinkReading.Failed("enlace cortado desde el banco de pruebas", CutByBench));
        if (_readers(link) is not { } reader)
            return Task.FromResult(SoftLinkReading.Failed(link.RemoteIp.Length == 0
                ? "el autómata remoto no tiene IP en su ficha y no hay otra instancia con su programa"
                : $"sin camino hasta {link.RemoteIp}: esta aplicación no lee ese autómata", NoReader));
        return Task.Run(async () =>
        {
            using var cancel = new CancellationTokenSource(TimeoutFor(link));
            try
            {
                return link.External
                    ? await reader.ExchangeAsync(link, [.. link.Reads.Select(r => r.Tag)], writes, cancel.Token)
                    : await reader.ReadAsync(link, fields, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                return SoftLinkReading.Failed($"sin respuesta en {TimeoutFor(link).TotalSeconds:0.#} s", Timeout);
            }
            catch (Exception e)
            {
                return SoftLinkReading.Failed(e.Message, BadRead);
            }
        });
    }

    private void Apply(SoftProgram program, ProgramLink link, StructValue input, SoftLinkReading reading)
    {
        Set(program, link.Busy, false);
        if (reading.IsOk && link.External)
        {
            if (ApplyExternal(link, input, reading.Values!) is not { } problem)
            {
                Set(program, link.Done, true);
                Problem(link.Remote, "");
                return;
            }

            reading = SoftLinkReading.Failed(problem, BadRead);
        }
        else if (reading.IsOk)
        {
            var missing = new List<string>();
            foreach (var (name, slot) in input.Members)
            {
                if (reading.Values!.TryGetValue(name, out var value))
                {
                    try
                    {
                        slot.Value = StInterpreter.Coerce(value, slot.Type);
                    }
                    catch (Exception e) when (e is InvalidCastException or OverflowException)
                    {
                        missing.Add($"{name} ({value})");
                    }
                }
                else
                {
                    missing.Add(name);
                }
            }

            if (missing.Count == 0)
            {
                Set(program, link.Done, true);
                Problem(link.Remote, "");
                return;
            }

            reading = SoftLinkReading.Failed($"el remoto no tiene estos campos de {link.RemoteOut}: {string.Join(", ", missing)}", BadRead);
        }

        Set(program, link.Error, true);
        if (program.Globals.TryGetValue(link.ErrorId, out var errorId)) errorId.Value = StInterpreter.Coerce((long)reading.ErrorCode, errorId.Type);
        Problem(link.Remote, reading.Problem ?? "lectura fallida");
    }

    /// <summary>
    /// Una ronda buena con un externo (PENDIENTES 180): cada etiqueta leída a su campo de <c>EIP_IN_R</c> y el latido a la cabecera (la copia del
    /// campo marcado o, sin él, uno más que antes). Null si todo fue bien; si no, qué faltó.
    /// </summary>
    private static string? ApplyExternal(ProgramLink link, StructValue input, IReadOnlyDictionary<string, object> values)
    {
        var missing = new List<string>();
        foreach (var r in link.Reads)
        {
            if (!values.TryGetValue(r.Tag, out var value) || !input.Members.TryGetValue(r.Field, out var slot))
            {
                missing.Add(r.Tag);
                continue;
            }

            try
            {
                slot.Value = StInterpreter.Coerce(value, slot.Type);
            }
            catch (Exception e) when (e is InvalidCastException or OverflowException)
            {
                missing.Add($"{r.Tag} ({value})");
            }
        }

        if (missing.Count > 0) return $"el autómata externo no da estas etiquetas: {string.Join(", ", missing)}";
        if (!input.Members.TryGetValue(DataTransferHeartbeat, out var heartbeat)) return null;
        long next;
        if (link.HeartbeatField is { } field && input.Members.TryGetValue(field, out var source))
            next = source.Value switch { bool b => b ? 1 : 0, long l => l, double d => (long)d, _ => 0 };
        else
            next = (heartbeat.Value is long current ? current : 0) + 1;
        heartbeat.Value = StInterpreter.Coerce(next & 0xFFFF, heartbeat.Type);
        return null;
    }

    /// <summary>La cabecera de latido de <c>EIP_IN_R</c> (<c>DataTransferNames.HeartbeatMember</c>; Core no conoce Binding).</summary>
    private const string DataTransferHeartbeat = "Heartbeat";

    private void Problem(string remote, string problem)
    {
        var had = _problems.TryGetValue(remote, out var old) ? old : "";
        if (had == problem) return;
        if (problem.Length == 0) _problems.TryRemove(remote, out _);
        else _problems[remote] = problem;
        ProblemChanged?.Invoke(remote, problem);
    }

    private static bool Trigger(SoftProgram program, ProgramLink link) =>
        program.Globals.TryGetValue(link.Status, out var status) && status.Value is StructValue s &&
        s.Members.TryGetValue("Status", out var st) && st.Value is StructValue ss &&
        ss.Members.TryGetValue("Trigger", out var trigger) && trigger.Value is true;

    private static void Set(SoftProgram program, string name, bool value)
    {
        if (program.Globals.TryGetValue(name, out var slot)) slot.Value = StInterpreter.Coerce(value, slot.Type);
    }
}
