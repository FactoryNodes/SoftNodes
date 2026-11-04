namespace FactoryNodes.Monitoring;

/// <summary>
/// Una conexión que reparte cada variable a su origen por el primer tramo de su ruta (autor, 2026-10-16: variables de otros
/// orígenes en Factory View; hoy <c>modbus.Bascula1.Gross</c>): las de un origen van a su conexión y el resto al PLC. Las
/// pantallas, el histórico y las alarmas no saben de dónde sale cada una. Conectar y el estado son los del PLC.
/// </summary>
public sealed class RoutedPlcConnection : IPlcConnection
{
    private readonly IPlcConnection _plc;
    private readonly Func<PlcPath, IPlcConnection?> _route;

    /// <param name="sources">Por primer tramo de la ruta («modbus»), la conexión de ese origen.</param>
    public RoutedPlcConnection(IPlcConnection plc, IReadOnlyDictionary<string, IPlcConnection> sources)
        : this(plc, path => sources.TryGetValue(path.Segments[0], out var source) ? source : null)
    {
    }

    /// <param name="route">A qué conexión va cada variable; null = al PLC. Para orígenes que no se distinguen por el primer tramo
    /// (el SoftNodes de la Factory Box, con los mismos grupos <c>hw_</c> que el PLC real: va lo que existe en su memoria, PENDIENTES 169).</param>
    public RoutedPlcConnection(IPlcConnection plc, Func<PlcPath, IPlcConnection?> route)
    {
        _plc = plc;
        _route = route;
    }

    /// <summary>La conexión del PLC que hay debajo (para saber si cambió).</summary>
    public IPlcConnection Plc => _plc;

    private IPlcConnection For(PlcPath path) => _route(path) ?? _plc;

    public PlcConnectionState State => _plc.State;
    public PlcServerInfo? Server => _plc.Server;

    public event EventHandler<PlcConnectionState>? StateChanged
    {
        add => _plc.StateChanged += value;
        remove => _plc.StateChanged -= value;
    }

    public Task<PlcServerInfo> ConnectAsync(PlcConnectionSettings settings, CancellationToken cancellation = default) =>
        _plc.ConnectAsync(settings, cancellation);

    public Task DisconnectAsync() => _plc.DisconnectAsync();

    public async Task<IReadOnlyList<PlcValue>> ReadAsync(IReadOnlyList<PlcPath> paths, CancellationToken cancellation = default)
    {
        var result = new PlcValue[paths.Count];
        foreach (var group in paths.Select((p, i) => (p, i)).GroupBy(x => For(x.p)))
        {
            var values = await group.Key.ReadAsync(group.Select(x => x.p).ToList(), cancellation);
            foreach (var (x, value) in group.Zip(values)) result[x.i] = value;
        }

        return result;
    }

    /// <summary>Escribe en el orden dado: lo seguido del mismo origen, junto.</summary>
    public async Task<IReadOnlyList<PlcWriteResult>> WriteAsync(IReadOnlyList<PlcWrite> writes, CancellationToken cancellation = default)
    {
        var result = new List<PlcWriteResult>(writes.Count);
        var i = 0;
        while (i < writes.Count)
        {
            var target = For(writes[i].Path);
            var run = writes.Skip(i).TakeWhile(w => For(w.Path) == target).ToList();
            result.AddRange(await target.WriteAsync(run, cancellation));
            i += run.Count;
        }

        return result;
    }

    public async Task<IPlcSubscription> SubscribeAsync(IReadOnlyList<PlcPath> paths, TimeSpan interval, Action<IReadOnlyList<PlcValue>> onChange,
        CancellationToken cancellation = default)
    {
        var subscriptions = new List<IPlcSubscription>();
        foreach (var group in paths.GroupBy(For))
            subscriptions.Add(await group.Key.SubscribeAsync(group.ToList(), interval, onChange, cancellation));
        return new Subscriptions(paths, subscriptions);
    }

    /// <summary>No libera las conexiones de debajo: son de quien las creó.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Subscriptions(IReadOnlyList<PlcPath> paths, IReadOnlyList<IPlcSubscription> parts) : IPlcSubscription
    {
        public IReadOnlyList<PlcPath> Paths { get; } = paths;

        public async ValueTask DisposeAsync()
        {
            foreach (var part in parts) await part.DisposeAsync();
        }
    }
}
