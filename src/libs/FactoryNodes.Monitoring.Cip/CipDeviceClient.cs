using System.Globalization;

namespace FactoryNodes.Monitoring.Cip;

/// <summary>
/// Un atributo de un objeto CIP: clase, instancia y atributo (lo que en Modbus es un registro). Como lo escriben los manuales:
/// <c>0x64/1/0x08</c> (cada parte en hexadecimal con 0x o en decimal).
/// </summary>
public sealed record CipAttribute(int Class, int Instance, int Attribute)
{
    public override string ToString() => $"0x{Class:X2}/{Instance}/0x{Attribute:X2}";

    /// <summary>«clase/instancia/atributo»; null si no se entiende o algo pasa de 16 bits.</summary>
    public static CipAttribute? Parse(string? text)
    {
        var parts = (text ?? "").Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return null;
        var values = parts.Select(Number).ToArray();
        return values.All(v => v is >= 0 and <= 0xFFFF) ? new CipAttribute(values[0]!.Value, values[1]!.Value, values[2]!.Value) : null;
    }

    private static int? Number(string part) =>
        part.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(part[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : null
            : int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : null;
}

/// <summary>Lo leído de un atributo: sus bytes (little-endian, como los manda el equipo) o el error de esa lectura.</summary>
public sealed record CipAttributeValue(CipAttribute Attribute, byte[]? Bytes, string Problem = "")
{
    public bool Ok => Bytes is not null;
}

/// <summary>
/// Cliente EtherNet/IP de **equipos** (variadores, indicadores de pesaje, tarjetas de E/S; PENDIENTES 45): mensajes explícitos sin conexión
/// con Get_Attribute_Single (0x0E) y Set_Attribute_Single (0x10) por clase/instancia/atributo, lo equivalente a leer y escribir registros
/// Modbus. Varias lecturas van juntas en un Multiple Service Packet; si el equipo no lo admite, una a una. Comparte el encapsulado con
/// <see cref="CipPlcConnection"/> (la de los PLC, por nombre de variable). Sin conexiones implícitas (I/O cíclico, UDP 2222): el dueño de
/// esas es el PLC.
/// </summary>
public sealed class CipDeviceClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeSpan _timeout;
    private readonly int _batch;
    private CipSession? _session;
    private bool _multipleRefused;

    /// <param name="batch">Cuántas lecturas van juntas (1 = una a una).</param>
    public CipDeviceClient(TimeSpan? timeout = null, int batch = 20)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        _batch = Math.Max(1, batch);
    }

    public bool Connected => _session is not null;

    /// <summary>Lo que dijo el equipo de sí mismo al conectar (fabricante, modelo, revisión).</summary>
    public CipIdentity? Identity { get; private set; }

    /// <summary><c>192.168.0.20</c>, <c>192.168.0.20:44818</c> o <c>eip://192.168.0.20</c>.</summary>
    public async Task ConnectAsync(string endpoint, CancellationToken cancellation = default)
    {
        await DisconnectAsync();
        var (host, port) = CipPlcConnection.ParseEndpoint(endpoint);
        _session = await CipSession.OpenAsync(host, port, _timeout, cancellation);
        try
        {
            Identity = await _session.IdentityAsync(cancellation);
        }
        catch (Exception e) when (e is CipException or IOException or TimeoutException or OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            Identity = null;   // hay equipos que no contestan ListIdentity por TCP: se sigue sin ella
        }
    }

    public async Task DisconnectAsync()
    {
        var session = _session;
        _session = null;
        Identity = null;
        if (session is not null) await session.DisposeAsync();
    }

    /// <summary>Lee los atributos, en el orden pedido. Un atributo que el equipo no tiene no hace fallar a los demás.</summary>
    public async Task<IReadOnlyList<CipAttributeValue>> ReadAsync(IReadOnlyList<CipAttribute> attributes, CancellationToken cancellation = default)
    {
        var result = new List<CipAttributeValue>(attributes.Count);
        foreach (var chunk in attributes.Chunk(_multipleRefused ? 1 : _batch))
        {
            if (chunk.Length == 1)
            {
                result.Add(Value(chunk[0], await SendAsync(CipMessages.GetAttributeRequest(chunk[0]), cancellation)));
                continue;
            }

            var reply = await SendAsync(CipMessages.MultipleRequest(chunk.Select(CipMessages.GetAttributeRequest).ToList()), cancellation);
            if (reply.GeneralStatus is 0x00 or 0x1E)
            {
                result.AddRange(chunk.Zip(CipMessages.ParseMultiple(reply.Data), Value));
                continue;
            }

            // Sin Multiple Service Packet (0x08 servicio no admitido, 0x05 sin Message Router…): desde ahora, una a una
            _multipleRefused = true;
            foreach (var attribute in chunk) result.Add(Value(attribute, await SendAsync(CipMessages.GetAttributeRequest(attribute), cancellation)));
        }

        return result;
    }

    /// <summary>Escribe un atributo con sus bytes (little-endian, del tamaño que tenga en el equipo).</summary>
    public async Task WriteAsync(CipAttribute attribute, byte[] value, CancellationToken cancellation = default)
    {
        var reply = await SendAsync(CipMessages.SetAttributeRequest(attribute, value), cancellation);
        if (reply.GeneralStatus != 0)
            throw new CipException($"{attribute}: {CipMessages.StatusText(reply.GeneralStatus, reply.ExtendedStatus)}");
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private static CipAttributeValue Value(CipAttribute attribute, CipReply reply) => reply.GeneralStatus == 0
        ? new CipAttributeValue(attribute, reply.Data)
        : new CipAttributeValue(attribute, null, CipMessages.StatusText(reply.GeneralStatus, reply.ExtendedStatus));

    private async Task<CipReply> SendAsync(byte[] request, CancellationToken cancellation)
    {
        var session = _session ?? throw new InvalidOperationException("No hay conexión con el equipo.");
        await _lock.WaitAsync(cancellation);
        try
        {
            return CipMessages.ParseReply(await session.SendAsync(request, cancellation));
        }
        finally
        {
            _lock.Release();
        }
    }
}
