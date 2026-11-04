namespace FactoryNodes.Monitoring;

/// <summary>Qué protocolo pide una dirección de PLC.</summary>
public enum PlcProtocol
{
    /// <summary><c>opc.tcp://192.168.0.1:4840</c>.</summary>
    OpcUa,

    /// <summary>EtherNet/IP (CIP) de Omron: <c>eip://192.168.0.1</c> o la IP sola (PENDIENTES 135).</summary>
    EtherNetIp
}

/// <summary>
/// Una conexión que elige el protocolo al conectar: el que pide <see cref="PlcConnectionSettings.Protocol"/> (el driver elegido en
/// pantalla, PENDIENTES 165) o, si no viene, el de la dirección (<see cref="ProtocolOf"/>): el mismo campo «dirección del PLC» vale
/// para OPC UA y para EtherNet/IP. Quien la crea da una conexión por protocolo; el resto solo ve <see cref="IPlcConnection"/>.
/// </summary>
public sealed class EndpointPlcConnection : IPlcConnection
{
    private readonly Func<PlcProtocol, IPlcConnection> _create;
    private IPlcConnection? _inner;
    private PlcProtocol? _protocol;

    public EndpointPlcConnection(Func<PlcProtocol, IPlcConnection> create) => _create = create;

    /// <summary>La dirección con <c>opc.tcp://</c> es OPC UA; <c>eip://</c> o sin esquema, EtherNet/IP.</summary>
    public static PlcProtocol ProtocolOf(string endpoint) =>
        endpoint.TrimStart().StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase) ? PlcProtocol.OpcUa : PlcProtocol.EtherNetIp;

    public PlcConnectionState State => _inner?.State ?? PlcConnectionState.Disconnected;

    public PlcServerInfo? Server => _inner?.Server;

    public event EventHandler<PlcConnectionState>? StateChanged;

    public async Task<PlcServerInfo> ConnectAsync(PlcConnectionSettings settings, CancellationToken cancellation = default)
    {
        var protocol = settings.Protocol ?? ProtocolOf(settings.Endpoint);
        if (_inner is null || _protocol != protocol)
        {
            await ReleaseAsync();
            _inner = _create(protocol);
            _protocol = protocol;
            _inner.StateChanged += Forward;
        }

        return await _inner.ConnectAsync(settings, cancellation);
    }

    public Task DisconnectAsync() => _inner?.DisconnectAsync() ?? Task.CompletedTask;

    public Task<IReadOnlyList<PlcValue>> ReadAsync(IReadOnlyList<PlcPath> paths, CancellationToken cancellation = default) =>
        Inner.ReadAsync(paths, cancellation);

    public Task<IReadOnlyList<PlcWriteResult>> WriteAsync(IReadOnlyList<PlcWrite> writes, CancellationToken cancellation = default) =>
        Inner.WriteAsync(writes, cancellation);

    public Task<IPlcSubscription> SubscribeAsync(IReadOnlyList<PlcPath> paths, TimeSpan interval, Action<IReadOnlyList<PlcValue>> onChange,
        CancellationToken cancellation = default) => Inner.SubscribeAsync(paths, interval, onChange, cancellation);

    public Task<IReadOnlyList<PlcNode>> BrowseVariablesAsync(Action<int>? progress = null, CancellationToken cancellation = default) =>
        Inner.BrowseVariablesAsync(progress, cancellation);

    public Task<IReadOnlyList<PlcNodeValue>> ReadNodesAsync(IReadOnlyList<string> ids, CancellationToken cancellation = default) =>
        Inner.ReadNodesAsync(ids, cancellation);

    public Task<IReadOnlyList<PlcNodeWriteResult>> WriteNodesAsync(IReadOnlyList<PlcNodeWrite> writes, CancellationToken cancellation = default) =>
        Inner.WriteNodesAsync(writes, cancellation);

    public async ValueTask DisposeAsync() => await ReleaseAsync();

    private IPlcConnection Inner => _inner ?? throw new InvalidOperationException("No hay conexión con el PLC.");

    private void Forward(object? sender, PlcConnectionState state) => StateChanged?.Invoke(this, state);

    private async Task ReleaseAsync()
    {
        if (_inner is null) return;
        _inner.StateChanged -= Forward;
        await _inner.DisposeAsync();
        _inner = null;
        _protocol = null;
    }
}
