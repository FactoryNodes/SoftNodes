using System.Globalization;
using FactoryNodes.Monitoring;
using FactoryNodes.SoftNodes.Runtime;

namespace FactoryNodes.SoftNodes.Instances;

/// <summary>
/// La memoria de un programa de SoftNodes vista como un PLC, <b>en proceso</b> (PENDIENTES 169): sin red ni servidor, para las pantallas
/// de la Factory Box que corren en el mismo proceso que su SoftNodes. Las rutas son las neutras de siempre (<c>hw_P.Cinta.Status.Active_Q</c>,
/// <c>hw_P.Topo.Elements[3].Mode</c>); lo que no está en la memoria es <see cref="PlcValueStatus.NotFound"/>. Los avisos de cambio se
/// sondean, como por EtherNet/IP. Siempre «conectada»: la conexión es la vida del programa.
/// </summary>
public sealed class SoftMemoryConnection : IPlcConnection
{
    private readonly Func<SoftProgram?> _program;

    /// <param name="program">El programa que corre ahora (cambia al recargar; null = instancia apagada).</param>
    public SoftMemoryConnection(Func<SoftProgram?> program) => _program = program;

    public PlcConnectionState State => _program() is null ? PlcConnectionState.Disconnected : PlcConnectionState.Connected;

    public PlcServerInfo? Server => _program() is null ? null : new PlcServerInfo("FactoryNodes", "SoftNodes (en proceso)", "");

    public event EventHandler<PlcConnectionState>? StateChanged;

    public Task<PlcServerInfo> ConnectAsync(PlcConnectionSettings settings, CancellationToken cancellation = default) =>
        Task.FromResult(Server ?? throw new InvalidOperationException("La instancia de SoftNodes está apagada."));

    public Task DisconnectAsync() => Task.CompletedTask;

    /// <summary>Si la variable existe en la memoria del programa (para repartir entre este PLC y otro con los mismos nombres de grupo).</summary>
    public bool Knows(PlcPath path)
    {
        if (_program() is not { } program) return false;
        lock (program.Sync) return Resolve(program, path) is { Value: not (StructValue or ArrayValue or FbInstance) };
    }

    public Task<IReadOnlyList<PlcValue>> ReadAsync(IReadOnlyList<PlcPath> paths, CancellationToken cancellation = default)
    {
        var now = DateTimeOffset.UtcNow;
        var program = _program();
        var result = new List<PlcValue>(paths.Count);
        if (program is null)
        {
            result.AddRange(paths.Select(p => new PlcValue(p, null, PlcValueStatus.Bad, now, "La instancia de SoftNodes está apagada.")));
            return Task.FromResult<IReadOnlyList<PlcValue>>(result);
        }

        lock (program.Sync)
        {
            foreach (var path in paths)
            {
                var slot = Resolve(program, path);
                result.Add(slot switch
                {
                    null => new PlcValue(path, null, PlcValueStatus.NotFound, now, "No existe en el programa de SoftNodes."),
                    { Value: StructValue or ArrayValue or FbInstance } => new PlcValue(path, null, PlcValueStatus.Bad, now, "Es una estructura: se leen sus miembros."),
                    _ => new PlcValue(path, ToNet(slot), PlcValueStatus.Good, now)
                });
            }
        }

        return Task.FromResult<IReadOnlyList<PlcValue>>(result);
    }

    public Task<IReadOnlyList<PlcWriteResult>> WriteAsync(IReadOnlyList<PlcWrite> writes, CancellationToken cancellation = default)
    {
        var program = _program();
        var result = new List<PlcWriteResult>(writes.Count);
        if (program is null)
        {
            result.AddRange(writes.Select(w => new PlcWriteResult(w.Path, PlcValueStatus.Bad, "La instancia de SoftNodes está apagada.")));
            return Task.FromResult<IReadOnlyList<PlcWriteResult>>(result);
        }

        lock (program.Sync)
        {
            foreach (var write in writes)
            {
                var slot = Resolve(program, write.Path);
                if (slot is null) { result.Add(new PlcWriteResult(write.Path, PlcValueStatus.NotFound, "No existe en el programa de SoftNodes.")); continue; }
                if (slot.Value is StructValue or ArrayValue or FbInstance) { result.Add(new PlcWriteResult(write.Path, PlcValueStatus.Bad, "Es una estructura: se escriben sus miembros.")); continue; }
                try
                {
                    slot.Value = StInterpreter.Coerce(FromNet(write.Value, slot.Type), slot.Type);
                    result.Add(new PlcWriteResult(write.Path, PlcValueStatus.Good, null));
                }
                catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
                {
                    result.Add(new PlcWriteResult(write.Path, PlcValueStatus.Bad, $"«{write.Value}» no vale para una variable {slot.Type}: {e.Message}"));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<PlcWriteResult>>(result);
    }

    public Task<IPlcSubscription> SubscribeAsync(IReadOnlyList<PlcPath> paths, TimeSpan interval, Action<IReadOnlyList<PlcValue>> onChange,
        CancellationToken cancellation = default)
    {
        var subscription = new Polling(this, paths, interval < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : interval, onChange);
        subscription.Start();
        return Task.FromResult<IPlcSubscription>(subscription);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Avisa a quien escucha de que el programa se ha recargado o apagado (lo llama quien lo aloja).</summary>
    public void NotifyStateChanged() => StateChanged?.Invoke(this, State);

    // ---------- Memoria ----------

    /// <summary>
    /// <c>hw_P.Cinta.Status.Active_Q</c>: la global <c>hw_P</c> y sus miembros; <c>Elements[3]</c> entra en el array con su base. Dentro de la
    /// instancia de un bloque no se entra. Como en el servidor OPC UA, también valen los miembros de <c>hw_*</c>/<c>io_*</c> sin el grupo.
    /// </summary>
    internal static Slot? Resolve(SoftProgram program, PlcPath path)
    {
        var segments = path.Segments;
        if (segments.Count == 0) return null;
        var (first, firstIndexes) = SplitIndexes(segments[0]);
        foreach (var start in Starts(program, first))
        {
            var slot = Index(start, firstIndexes);
            for (var i = 1; slot is not null && i < segments.Count; i++)
            {
                var (name, indexes) = SplitIndexes(segments[i]);
                slot = slot.Value is StructValue structure && structure.Members.TryGetValue(name, out var member) ? Index(member, indexes) : null;
            }

            if (slot is not null) return slot;
        }

        return null;
    }

    private static IEnumerable<Slot> Starts(SoftProgram program, string name)
    {
        if (program.Globals.TryGetValue(name, out var global)) yield return global;
        foreach (var root in program.Globals)
            if (root.Value.Value is StructValue structure &&
                (root.Key.StartsWith("hw_", StringComparison.OrdinalIgnoreCase) || root.Key.StartsWith("io_", StringComparison.OrdinalIgnoreCase)) &&
                structure.Members.TryGetValue(name, out var member))
                yield return member;
    }

    private static Slot? Index(Slot? slot, IReadOnlyList<int> indexes)
    {
        foreach (var index in indexes)
        {
            if (slot?.Value is not ArrayValue array) return null;
            var k = index - array.Lower;
            if (k < 0 || k >= array.Items.Length) return null;
            slot = array.Items[k];
        }

        return slot;
    }

    /// <summary><c>Elements[3]</c> → («Elements», [3]); <c>M[1,2]</c> → («M», [1, 2]).</summary>
    private static (string Name, IReadOnlyList<int> Indexes) SplitIndexes(string segment)
    {
        var bracket = segment.IndexOf('[');
        if (bracket < 0 || !segment.EndsWith(']')) return (segment, []);
        var indexes = new List<int>();
        foreach (var part in segment[(bracket + 1)..^1].Split(','))
            if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) indexes.Add(n);
            else return (segment, []);
        return (segment[..bracket], indexes);
    }

    /// <summary>El valor con el tipo .NET que da el PLC real por OPC UA (los enteros con su ancho, TIME en ms como entero, REAL como float).</summary>
    private static object ToNet(Slot slot)
    {
        var type = slot.Type;
        return type.Kind switch
        {
            StTypeKind.Bool => (bool)slot.Value,
            StTypeKind.Real => type.Is32BitReal ? (float)(double)slot.Value : (double)slot.Value,
            StTypeKind.Time => (int)(long)slot.Value,
            StTypeKind.String => (string)slot.Value,
            StTypeKind.Int => (type.Bits, type.Signed) switch
            {
                (8, true) => (sbyte)(long)slot.Value,
                (8, false) => (byte)(long)slot.Value,
                (16, true) => (short)(long)slot.Value,
                (16, false) => (ushort)(long)slot.Value,
                (32, true) => (int)(long)slot.Value,
                (32, false) => (uint)(long)slot.Value,
                (_, true) => (long)slot.Value,
                _ => (ulong)(long)slot.Value
            },
            _ => slot.Value
        };
    }

    /// <summary>Lo que llega de una pantalla (texto, número o bool) a lo que entiende <see cref="StInterpreter.Coerce"/>.</summary>
    private static object FromNet(object value, StType type)
    {
        var c = CultureInfo.InvariantCulture;
        if (value is string text)
        {
            text = text.Trim();
            return type.Kind switch
            {
                StTypeKind.Bool => text is "1" || bool.Parse(text),
                StTypeKind.String => text,
                StTypeKind.Real => double.Parse(text, NumberStyles.Float, c),
                _ => long.Parse(text, NumberStyles.Integer, c)
            };
        }

        return type.Kind switch
        {
            StTypeKind.Bool => Convert.ToBoolean(value, c),
            StTypeKind.String => Convert.ToString(value, c) ?? "",
            StTypeKind.Real => Convert.ToDouble(value, c),
            _ => Convert.ToInt64(value, c)
        };
    }

    private sealed class Polling(SoftMemoryConnection owner, IReadOnlyList<PlcPath> paths, TimeSpan interval, Action<IReadOnlyList<PlcValue>> onChange) : IPlcSubscription
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Dictionary<PlcPath, PlcValue> _last = new();
        private Task? _loop;

        public IReadOnlyList<PlcPath> Paths { get; } = paths;

        public void Start() => _loop = Task.Run(LoopAsync);

        private async Task LoopAsync()
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                do
                {
                    var values = await owner.ReadAsync(Paths, _stop.Token);
                    var changed = values.Where(v => !_last.TryGetValue(v.Path, out var old) || old.Status != v.Status || !Equals(old.Value, v.Value)).ToList();
                    foreach (var value in changed) _last[value.Path] = value;
                    if (changed.Count > 0) onChange(changed);
                } while (await timer.WaitForNextTickAsync(_stop.Token));
            }
            catch (OperationCanceledException)
            {
                // fin de la suscripción
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_loop is not null)
                try { await _loop; } catch { /* ya parado */ }
            _stop.Dispose();
        }
    }
}
