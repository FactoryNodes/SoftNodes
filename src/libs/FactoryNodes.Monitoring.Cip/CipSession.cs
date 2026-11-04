using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FactoryNodes.Monitoring.Cip;

/// <summary>Quién contesta a un ListIdentity: fabricante, producto, revisión y número de serie.</summary>
public sealed record CipIdentity(IPAddress Address, ushort Vendor, ushort DeviceType, ushort ProductCode, byte Major, byte Minor,
    uint Serial, string ProductName)
{
    /// <summary>Omron es el fabricante 47 (ODVA); el 0 (reservado, ningún equipo real lo usa) es el servidor de SoftNodes.</summary>
    public string VendorName => Vendor switch { 47 => "OMRON", 1 => "Rockwell Automation", 0 => "FactoryNodes", _ => $"fabricante {Vendor}" };

    public string Revision => $"{Major}.{Minor:D2}";

    public override string ToString() => $"{Address}  {VendorName} · {ProductName} rev. {Revision} (serie {Serial:X8})";
}

/// <summary>
/// Sesión EtherNet/IP por TCP 44818 (encapsulado, CIP Vol. 2): RegisterSession y peticiones sin conexión (SendRRData).
/// Una petición cada vez; quien la usa la protege con su cerrojo.
/// </summary>
internal sealed class CipSession : IAsyncDisposable
{
    public const int Port = 44818;
    private const ushort ListIdentityCommand = 0x63, RegisterSessionCommand = 0x65, UnregisterSessionCommand = 0x66, SendRRDataCommand = 0x6F;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private uint _session;

    private CipSession(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    public static async Task<CipSession> OpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellation)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var limit = Limit(timeout, cancellation);
            await tcp.ConnectAsync(host, port, limit.Token);
            var session = new CipSession(tcp) { Timeout = timeout };
            var reply = await session.ExchangeAsync(RegisterSessionCommand, [1, 0, 0, 0], cancellation);
            session._session = reply.Session;
            return session;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new CipException($"{host}:{port} no contesta.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Quién es el equipo de esta sesión (ListIdentity por la misma conexión TCP).</summary>
    public async Task<CipIdentity?> IdentityAsync(CancellationToken cancellation)
    {
        var reply = await ExchangeAsync(ListIdentityCommand, [], cancellation);
        return ParseIdentities(reply.Data, ((IPEndPoint)_tcp.Client.RemoteEndPoint!).Address).FirstOrDefault();
    }

    /// <summary>Una petición CIP sin conexión; devuelve la respuesta CIP.</summary>
    public async Task<byte[]> SendAsync(byte[] cip, CancellationToken cancellation)
    {
        var data = new byte[16 + cip.Length];
        // asa de interfaz 0, tiempo 0 (lo pone la petición), 2 elementos: dirección nula y datos sin conexión (0xB2)
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 0xB2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)cip.Length);
        cip.CopyTo(data, 16);
        var reply = await ExchangeAsync(SendRRDataCommand, data, cancellation);

        var d = reply.Data;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6));
        var offset = 8;
        for (var i = 0; i < count; i++)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(offset + 2));
            if (type == 0xB2) return d[(offset + 4)..(offset + 4 + length)];
            offset += 4 + length;
        }

        throw new CipException("La respuesta no trae datos CIP.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_session != 0) await WriteAsync(UnregisterSessionCommand, [], CancellationToken.None);
        }
        catch { /* ya caída */ }

        _tcp.Dispose();
    }

    // ---------- Encapsulado ----------

    private sealed record Encapsulated(uint Session, byte[] Data);

    private async Task<Encapsulated> ExchangeAsync(ushort command, byte[] data, CancellationToken cancellation)
    {
        using var limit = Limit(Timeout, cancellation);
        try
        {
            await WriteAsync(command, data, limit.Token);
            var header = new byte[24];
            await _stream.ReadExactlyAsync(header, limit.Token);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
            var status = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            var body = new byte[length];
            await _stream.ReadExactlyAsync(body, limit.Token);
            if (status != 0) throw new CipException($"El equipo rechaza la petición EtherNet/IP 0x{command:X2}: estado 0x{status:X8}.");
            return new Encapsulated(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)), body);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new CipException("El PLC no contesta (tiempo agotado).");
        }
    }

    private async Task WriteAsync(ushort command, byte[] data, CancellationToken cancellation)
    {
        var frame = Frame(command, _session, data);
        await _stream.WriteAsync(frame, cancellation);
    }

    private static byte[] Frame(ushort command, uint session, byte[] data)
    {
        var frame = new byte[24 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, command);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), session);
        data.CopyTo(frame, 24);
        return frame;
    }

    private static CancellationTokenSource Limit(TimeSpan timeout, CancellationToken cancellation)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        return limit;
    }

    // ---------- Búsqueda en la red ----------

    /// <summary>
    /// ListIdentity por difusión UDP 44818: todos los equipos EtherNet/IP de la red que contestan en <paramref name="wait"/>.
    /// </summary>
    public static async Task<IReadOnlyList<CipIdentity>> DiscoverAsync(TimeSpan wait, IPAddress? broadcast = null, CancellationToken cancellation = default)
    {
        using var udp = new UdpClient(0) { EnableBroadcast = true };
        await udp.SendAsync(Frame(ListIdentityCommand, 0, []), new IPEndPoint(broadcast ?? IPAddress.Broadcast, Port), cancellation);
        var found = new Dictionary<IPAddress, CipIdentity>();
        using var limit = Limit(wait, cancellation);
        try
        {
            while (true)
            {
                var packet = await udp.ReceiveAsync(limit.Token);
                if (packet.Buffer.Length < 24) continue;
                foreach (var identity in ParseIdentities(packet.Buffer[24..], packet.RemoteEndPoint.Address))
                    found[identity.Address] = identity;
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // fin de la espera
        }

        return found.Values.OrderBy(i => i.Address.ToString(), StringComparer.Ordinal).ToList();
    }

    internal static IEnumerable<CipIdentity> ParseIdentities(byte[] d, IPAddress from)
    {
        if (d.Length < 2) yield break;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(d);
        var offset = 2;
        for (var i = 0; i < count && offset + 4 <= d.Length; i++)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(offset + 2));
            var item = d.AsSpan(offset + 4, Math.Min(length, d.Length - offset - 4));
            offset += 4 + length;
            if (type != 0x0C || item.Length < 33) continue;

            // versión (2), sockaddr (16, big endian: familia, puerto, IP, ceros), fabricante, tipo, producto, revisión,
            // estado, serie, nombre (longitud + texto), estado
            var address = new IPAddress(item.Slice(6, 4));
            var nameLength = item[32];
            var name = Encoding.ASCII.GetString(item.Slice(33, Math.Min(nameLength, item.Length - 33)));
            yield return new CipIdentity(address.Equals(IPAddress.Any) ? from : address,
                BinaryPrimitives.ReadUInt16LittleEndian(item[18..]), BinaryPrimitives.ReadUInt16LittleEndian(item[20..]),
                BinaryPrimitives.ReadUInt16LittleEndian(item[22..]), item[24], item[25],
                BinaryPrimitives.ReadUInt32LittleEndian(item[28..]), name);
        }
    }
}
