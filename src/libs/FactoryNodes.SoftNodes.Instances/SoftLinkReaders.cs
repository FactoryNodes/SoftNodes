using System.Collections.Concurrent;
using FactoryNodes.Iec61131.Emission;
using FactoryNodes.Monitoring;

namespace FactoryNodes.SoftNodes.Instances;

/// <summary>
/// El lector de un enlace (PENDIENTES 167) sobre cualquier conexión de PLC: lee <c>EIP_OUT_&lt;yo&gt;.Campo</c> por campo con
/// <see cref="IPlcConnection.ReadAsync"/>. Vale para la memoria de otra instancia del mismo proceso (<see cref="SoftMemoryConnection"/>), para
/// un Omron o un SoftNodes por EtherNet/IP y para un Siemens por OPC UA. Si la conexión está caída y hay <paramref name="settings"/>, conecta
/// antes; sin ellos (la memoria), la conexión es la vida del programa.
/// </summary>
public sealed class PlcLinkReader(IPlcConnection connection, PlcConnectionSettings? settings = null) : ISoftLinkReader
{
    private readonly SemaphoreSlim _one = new(1, 1);

    public IPlcConnection Connection => connection;

    public async Task<SoftLinkReading> ReadAsync(ProgramLink link, IReadOnlyList<string> fields, CancellationToken cancellation)
    {
        await _one.WaitAsync(cancellation);
        try
        {
            if (connection.State != PlcConnectionState.Connected)
            {
                if (settings is null) return SoftLinkReading.Failed("la instancia remota está apagada", SoftLinkRunner.NoReader);
                try
                {
                    await connection.ConnectAsync(settings, cancellation);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return SoftLinkReading.Failed($"sin conexión con {settings.Endpoint}: {e.Message}", SoftLinkRunner.NoReader);
                }
            }

            var paths = fields.Select(f => new PlcPath([link.RemoteOut, f])).ToList();
            var values = await connection.ReadAsync(paths, cancellation);
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                if (value.Status != PlcValueStatus.Good || value.Value is null)
                    return SoftLinkReading.Failed($"{value.Path}: {(string.IsNullOrEmpty(value.Detail) ? value.Status.ToString() : value.Detail)}", SoftLinkRunner.BadRead);
                result[value.Path.Segments[^1]] = Normalise(value.Value);
            }

            return SoftLinkReading.Ok(result);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Con un autómata externo (PENDIENTES 180): primero las escrituras (el valor con el tipo que tiene la etiqueta en el PLC, que el driver lee
    /// una vez y recuerda) y luego las lecturas, las dos agrupadas como hace el driver. Las etiquetas van tal cual (<c>Var1_out</c>,
    /// <c>Programa.Var</c>: el punto separa tramos).
    /// </summary>
    public async Task<SoftLinkReading> ExchangeAsync(ProgramLink link, IReadOnlyList<string> reads, IReadOnlyList<(string Tag, object Value)> writes,
        CancellationToken cancellation)
    {
        await _one.WaitAsync(cancellation);
        try
        {
            if (connection.State != PlcConnectionState.Connected)
            {
                if (settings is null) return SoftLinkReading.Failed("el autómata externo no tiene conexión", SoftLinkRunner.NoReader);
                try
                {
                    await connection.ConnectAsync(settings, cancellation);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    return SoftLinkReading.Failed($"sin conexión con {settings.Endpoint}: {e.Message}", SoftLinkRunner.NoReader);
                }
            }

            if (writes.Count > 0)
            {
                var written = await connection.WriteAsync([.. writes.Select(w => new PlcWrite(PathOf(w.Tag), w.Value))], cancellation);
                if (written.FirstOrDefault(w => !w.Ok) is { } bad)
                    return SoftLinkReading.Failed($"no se pudo escribir {bad.Path}: {(string.IsNullOrEmpty(bad.Detail) ? bad.Status.ToString() : bad.Detail)}", SoftLinkRunner.BadRead);
            }

            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (reads.Count > 0)
            {
                var values = await connection.ReadAsync([.. reads.Select(PathOf)], cancellation);
                for (var i = 0; i < values.Count && i < reads.Count; i++)
                {
                    var value = values[i];
                    if (value.Status != PlcValueStatus.Good || value.Value is null)
                        return SoftLinkReading.Failed($"{reads[i]}: {(string.IsNullOrEmpty(value.Detail) ? value.Status.ToString() : value.Detail)}", SoftLinkRunner.BadRead);
                    result[reads[i]] = Normalise(value.Value);
                }
            }

            return SoftLinkReading.Ok(result);
        }
        finally
        {
            _one.Release();
        }
    }

    private static PlcPath PathOf(string tag) => new(tag.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>Lo que llega del driver (cualquier entero, float, bool, texto) en lo que coacciona el intérprete: bool, long, double o string.</summary>
    private static object Normalise(object value) => value switch
    {
        bool or long or double or string => value,
        sbyte or byte or short or ushort or int or uint => Convert.ToInt64(value),
        ulong u => unchecked((long)u),
        float f => (double)f,
        decimal d => (double)d,
        _ => value.ToString() ?? ""
    };
}

/// <summary>
/// Una instancia que puede leer o ser leída por un enlace: la de SoftNodes abierto (<see cref="SimulatedPlc"/>) y la de SoftNodes Pro
/// (<c>SoftNode</c>). Lo que el <see cref="SoftLinkRouter"/> necesita saber de ella.
/// </summary>
public interface ILinkNode
{
    string Name { get; }

    bool IsOn { get; }

    SoftInstance Instance { get; }
}

/// <summary>
/// Quién lee cada remoto de las instancias de esta aplicación (SoftNodes o SoftNodes Pro): si otra instancia abierta tiene el programa del remoto
/// (su memoria tiene <c>EIP_OUT_&lt;yo&gt;</c>), se le lee en memoria, sin red; si no, por su IP con el cliente que da la aplicación
/// (EtherNet/IP para Omron y SoftNodes, OPC UA para Siemens). Las conexiones por red se guardan por instancia y remoto.
/// </summary>
public sealed class SoftLinkRouter
{
    private readonly Func<IEnumerable<ILinkNode>> _nodes;
    private readonly Func<IPlcAddressing, IPlcConnection>? _clients;
    private readonly ConcurrentDictionary<(string Node, string Remote), PlcLinkReader> _remote = new();

    /// <param name="nodes">Las instancias abiertas ahora (cambian).</param>
    /// <param name="clients">El cliente de PLC de la aplicación (null = solo en memoria).</param>
    public SoftLinkRouter(Func<IEnumerable<ILinkNode>> nodes, Func<IPlcAddressing, IPlcConnection>? clients)
    {
        _nodes = nodes;
        _clients = clients;
    }

    /// <summary>El lector del enlace <paramref name="link"/> de la instancia <paramref name="self"/>; null si no hay camino.</summary>
    public ISoftLinkReader? ReaderFor(ILinkNode self, ProgramLink link)
    {
        // Un externo (PENDIENTES 180) no tiene nuestro programa: siempre por la red, a su IP
        var peer = link.External ? null : _nodes().FirstOrDefault(n => !ReferenceEquals(n, self) && Has(n, link.RemoteOut));
        if (peer is not null) return new PlcLinkReader(new SoftMemoryConnection(() => peer.IsOn ? peer.Instance.Program : null));
        if (link.RemoteIp.Length == 0 || _clients is null) return null;
        return _remote.GetOrAdd((self.Name, link.Remote), _ =>
            new PlcLinkReader(_clients(AddressingOf(link.RemotePlatform)), new PlcConnectionSettings(EndpointOf(link))));
    }

    /// <summary>
    /// La dirección del remoto: EtherNet/IP al 44818 para todos. Un Siemens también: su programa lleva el servidor CIP de etiquetas
    /// (<c>EipTagServer</c>, PENDIENTES 165); por OPC UA no se le leería <c>EIP_OUT_x</c> con este nombre (iría dentro del DB <c>hw_…</c>).
    /// </summary>
    public static string EndpointOf(ProgramLink link) => PlcDrivers.EtherNetIp.Endpoint(link.RemoteIp, PlcDrivers.EtherNetIp.DefaultPort);

    private static IPlcAddressing AddressingOf(string platform) => IsTia(platform) ? new TiaAddressing() : new SysmacAddressing();

    private static bool IsTia(string platform) => platform.Equals("TIA", StringComparison.OrdinalIgnoreCase);

    private static bool Has(ILinkNode node, string variable)
    {
        var program = node.Instance.Program;
        lock (program.Sync) return program.Globals.ContainsKey(variable) || program.Data.Members.ContainsKey(variable);
    }
}
