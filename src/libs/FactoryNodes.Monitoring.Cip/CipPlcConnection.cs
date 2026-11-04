using System.Collections.Concurrent;
using FactoryNodes.Iec61131.Emission;

namespace FactoryNodes.Monitoring.Cip;

/// <summary>Opciones del cliente CIP.</summary>
/// <param name="Route">
/// Ruta hasta la CPU para un Unconnected Send (p. ej. <c>[1, 0]</c>: puerto de la placa, ranura 0), o null para hablar
/// directamente con el Message Router del equipo al que se conecta (el puerto propio de un NJ/NX). Por comprobar cuál pide
/// cada CPU (FORMAT.md).
/// </param>
/// <param name="Members">Cómo van los miembros de una estructura en la ruta (<see cref="CipMemberSegments"/>).</param>
/// <param name="Batch">Cuántas lecturas van juntas en un Multiple Service Packet (1 = una a una).</param>
public sealed record CipClientOptions(
    byte[]? Route = null,
    CipMemberSegments Members = CipMemberSegments.Separate,
    int Batch = 20,
    TimeSpan? Timeout = null);

/// <summary>
/// <see cref="IPlcConnection"/> por EtherNet/IP (CIP, mensajes explícitos sin conexión) para los Omron NJ/NX que no tienen
/// OPC UA (PENDIENTES 135). Lee y escribe las globales publicadas en red por su nombre (el de <see cref="IPlcAddressing"/>,
/// como en OPC UA: <c>Cinta211.Status.Active_Q</c>). EtherNet/IP no avisa de los cambios: las suscripciones sondean.
/// </summary>
public sealed class CipPlcConnection : IPlcConnection
{
    private readonly IPlcAddressing _addressing;
    private readonly CipClientOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<PlcPath, CipRaw> _types = new();
    private CipSession? _session;
    private PlcConnectionState _state = PlcConnectionState.Disconnected;

    public CipPlcConnection(IPlcAddressing addressing, CipClientOptions? options = null)
    {
        _addressing = addressing;
        _options = options ?? new CipClientOptions();
    }

    public PlcConnectionState State => _state;

    public PlcServerInfo? Server { get; private set; }

    /// <summary>La identidad que dio el equipo al conectar (modelo, revisión, serie).</summary>
    public CipIdentity? Identity { get; private set; }

    public event EventHandler<PlcConnectionState>? StateChanged;

    private TimeSpan Timeout => _options.Timeout ?? TimeSpan.FromSeconds(5);

    /// <summary><c>192.168.250.1</c>, <c>192.168.250.1:44818</c> o <c>eip://192.168.250.1</c>.</summary>
    public async Task<PlcServerInfo> ConnectAsync(PlcConnectionSettings settings, CancellationToken cancellation = default)
    {
        await DisconnectAsync();
        SetState(PlcConnectionState.Connecting);
        try
        {
            var (host, port) = ParseEndpoint(settings.Endpoint);
            _session = await CipSession.OpenAsync(host, port, Timeout, cancellation);
            Identity = await _session.IdentityAsync(cancellation);
            Server = new PlcServerInfo(Identity?.VendorName ?? "", Identity?.ProductName ?? "EtherNet/IP", Identity is null ? "" : $"rev. {Identity.Revision}");
            SetState(PlcConnectionState.Connected);
            return Server;
        }
        catch
        {
            await DisconnectAsync();
            SetState(PlcConnectionState.Faulted);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        var session = _session;
        _session = null;
        Server = null;
        Identity = null;
        _types.Clear();
        if (session is not null) await session.DisposeAsync();
        SetState(PlcConnectionState.Disconnected);
    }

    public async Task<IReadOnlyList<PlcValue>> ReadAsync(IReadOnlyList<PlcPath> paths, CancellationToken cancellation = default)
    {
        var results = new List<PlcValue>(paths.Count);
        foreach (var chunk in paths.Chunk(Math.Max(1, _options.Batch)))
            results.AddRange(await ReadChunkAsync(chunk, cancellation));
        return results;
    }

    public async Task<IReadOnlyList<PlcWriteResult>> WriteAsync(IReadOnlyList<PlcWrite> writes, CancellationToken cancellation = default)
    {
        // El valor se escribe con el tipo y el tamaño que tiene la variable en el PLC: se lee una vez y se recuerda.
        var unknown = writes.Select(w => w.Path).Where(p => !_types.ContainsKey(p)).Distinct().ToList();
        var failed = new Dictionary<PlcPath, PlcValue>();
        if (unknown.Count > 0)
            foreach (var value in await ReadAsync(unknown, cancellation))
                if (!value.IsGood) failed[value.Path] = value;

        var results = new PlcWriteResult?[writes.Count];
        var requests = new List<(int Index, byte[] Request)>();
        for (var i = 0; i < writes.Count; i++)
        {
            var write = writes[i];
            if (failed.TryGetValue(write.Path, out var bad) || !_types.TryGetValue(write.Path, out var read))
            {
                results[i] = new PlcWriteResult(write.Path, bad?.Status ?? PlcValueStatus.Bad, bad?.Detail);
                continue;
            }

            try
            {
                requests.Add((i, CipMessages.WriteRequest(TagPath(write.Path), CipMessages.Encode(read, write.Value))));
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                results[i] = new PlcWriteResult(write.Path, PlcValueStatus.Bad, $"«{write.Value}» no vale para una variable {CipTypes.Name(read.Type)}: {ex.Message}");
            }
        }

        // Agrupadas en Multiple Service Packet como las lecturas (2026-10-27: una a una, las 407 SimUse_ sueltas de Natucer18 tardaban 14 s en el NJ101)
        foreach (var chunk in requests.Chunk(Math.Max(1, _options.Batch)))
            foreach (var (index, reply) in await WriteChunkAsync(chunk, cancellation))
                results[index] = new PlcWriteResult(writes[index].Path, StatusOf(reply.GeneralStatus),
                    reply.Ok ? null : CipMessages.StatusText(reply.GeneralStatus, reply.ExtendedStatus));

        return results.Select(r => r!).ToList();
    }

    private async Task<IReadOnlyList<(int Index, CipReply Reply)>> WriteChunkAsync(IReadOnlyList<(int Index, byte[] Request)> chunk,
        CancellationToken cancellation)
    {
        if (chunk.Count == 1) return [(chunk[0].Index, await SendAsync(chunk[0].Request, cancellation))];
        var reply = await SendAsync(CipMessages.MultipleRequest(chunk.Select(c => c.Request).ToList()), cancellation);
        if (reply.GeneralStatus is 0 or 0x1E)
        {
            var replies = CipMessages.ParseMultiple(reply.Data);
            return chunk.Select((c, i) => (c.Index, replies[i])).ToList();
        }

        // La petición no cabe en un mensaje (o el PLC no agrupa): por mitades.
        var half = chunk.Count / 2;
        return [.. await WriteChunkAsync(chunk.Take(half).ToList(), cancellation), .. await WriteChunkAsync(chunk.Skip(half).ToList(), cancellation)];
    }

    public async Task<IPlcSubscription> SubscribeAsync(IReadOnlyList<PlcPath> paths, TimeSpan interval, Action<IReadOnlyList<PlcValue>> onChange,
        CancellationToken cancellation = default)
    {
        RequireSession();
        var subscription = new PollingSubscription(this, paths, interval < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : interval, onChange);
        subscription.Start();
        return await Task.FromResult<IPlcSubscription>(subscription);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    // ---------- Para la herramienta de prueba (Tools/CipProbe) ----------

    /// <summary>Lee una variable por su nombre en el PLC y devuelve también los bytes tal cual llegaron (tipo y datos en hex).</summary>
    public async Task<(PlcValue Value, string Raw)> ReadRawAsync(string name, CancellationToken cancellation = default)
    {
        var path = PlcPath.Parse(name);
        var reply = await SendAsync(CipMessages.ReadRequest(CipMessages.TagPath(name, _options.Members)), cancellation);
        var value = ToValue(path, reply);
        return (value, $"estado {CipMessages.StatusText(reply.GeneralStatus, reply.ExtendedStatus)}, datos {CipMessages.Hex(reply.Data)}");
    }

    // ---------- Internos ----------

    private async Task<IReadOnlyList<PlcValue>> ReadChunkAsync(IReadOnlyList<PlcPath> paths, CancellationToken cancellation)
    {
        if (paths.Count == 1)
            return [ToValue(paths[0], await SendAsync(CipMessages.ReadRequest(TagPath(paths[0])), cancellation))];

        var reply = await SendAsync(CipMessages.MultipleRequest(paths.Select(p => CipMessages.ReadRequest(TagPath(p))).ToList()), cancellation);
        if (reply.GeneralStatus is 0 or 0x1E)
        {
            var replies = CipMessages.ParseMultiple(reply.Data);
            return paths.Select((p, i) => ToValue(p, replies[i])).ToList();
        }

        // La respuesta no cabe en un mensaje (o el PLC no agrupa): por mitades.
        var half = paths.Count / 2;
        return [.. await ReadChunkAsync(paths.Take(half).ToList(), cancellation), .. await ReadChunkAsync(paths.Skip(half).ToList(), cancellation)];
    }

    private PlcValue ToValue(PlcPath path, CipReply reply)
    {
        var now = DateTimeOffset.UtcNow;
        if (!reply.Ok)
            return new PlcValue(path, null, StatusOf(reply.GeneralStatus), now, CipMessages.StatusText(reply.GeneralStatus, reply.ExtendedStatus));
        try
        {
            var raw = CipMessages.ParseReadData(reply.Data);
            var value = CipMessages.Decode(raw);
            if (value is null)
                return new PlcValue(path, null, PlcValueStatus.Bad, now, $"Es de tipo {CipTypes.Name(raw.Type)}: se leen sus miembros, no la variable entera.");
            _types[path] = raw;
            return new PlcValue(path, value, PlcValueStatus.Good, now);
        }
        catch (CipException ex)
        {
            return new PlcValue(path, null, PlcValueStatus.Bad, now, ex.Message);
        }
    }

    private async Task<CipReply> SendAsync(byte[] request, CancellationToken cancellation)
    {
        var session = RequireSession();
        await _lock.WaitAsync(cancellation);
        try
        {
            var message = _options.Route is { Length: > 0 } route ? CipMessages.Routed(request, route) : request;
            return CipMessages.ParseReply(await session.SendAsync(message, cancellation));
        }
        catch (Exception ex) when (ex is CipException or IOException or System.Net.Sockets.SocketException)
        {
            SetState(PlcConnectionState.Faulted);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private byte[] TagPath(PlcPath path) => CipMessages.TagPath(_addressing.OpcUaIdentifier(path.Segments), _options.Members);

    private CipSession RequireSession() => _session ?? throw new InvalidOperationException("No hay conexión con el PLC.");

    private static PlcValueStatus StatusOf(byte status) => status switch
    {
        0 => PlcValueStatus.Good,
        0x04 or 0x05 => PlcValueStatus.NotFound,
        0x0F => PlcValueStatus.AccessDenied,
        _ => PlcValueStatus.Bad
    };

    internal static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        var text = endpoint.Trim();
        if (text.Contains("://")) text = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..];
        text = text.TrimEnd('/');
        var colon = text.LastIndexOf(':');
        return colon > 0 && int.TryParse(text[(colon + 1)..], out var port) ? (text[..colon], port) : (text, CipSession.Port);
    }

    private void SetState(PlcConnectionState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    /// <summary>
    /// Sondeo: lee todas cada <c>interval</c> y avisa de las que cambian (la primera vez, de todas). Si se cae la conexión,
    /// para y deja la conexión en <see cref="PlcConnectionState.Faulted"/>.
    /// </summary>
    private sealed class PollingSubscription(CipPlcConnection owner, IReadOnlyList<PlcPath> paths, TimeSpan interval,
        Action<IReadOnlyList<PlcValue>> onChange) : IPlcSubscription
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
            catch (Exception ex) when (ex is CipException or IOException or System.Net.Sockets.SocketException or InvalidOperationException)
            {
                // conexión caída: el estado ya está en Faulted
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            if (_loop is not null)
                try { await _loop; } catch { /* ya informado */ }
            _stop.Dispose();
        }
    }
}
