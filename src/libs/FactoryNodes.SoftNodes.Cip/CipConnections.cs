using System.Buffers.Binary;

namespace FactoryNodes.SoftNodes.Cip;

/// <summary>
/// Conexiones de mensajes explícitos (clase 3) de un cliente (PENDIENTES 195): el Connection Manager (clase 6/1) con Forward_Open (0x54),
/// Large_Forward_Open (0x5B) y Forward_Close (0x4E), y el id de conexión con el que llegan luego las peticiones por SendUnitData. Lo pidió una
/// pantalla con el driver de Omron NJ/NX (2026-11-03): Forward_Close para limpiar y Large_Forward_Open de clase 3 al Message Router, 1996
/// bytes en los dos sentidos. Una por cliente TCP (las conexiones mueren con él). Sin red: recibe la petición CIP y devuelve la respuesta.
/// </summary>
public sealed class CipConnections
{
    public const byte ForwardOpen = 0x54, LargeForwardOpen = 0x5B, ForwardClose = 0x4E;
    private const byte ConnectionFailure = 0x01, NotEnoughData = 0x13, PathSegmentError = 0x04;

    /// <summary>Estado adicional de ConnectionFailure: clase de transporte no admitida (solo la 3, mensajes explícitos).</summary>
    public const ushort TransportNotSupported = 0x0103;

    private static int _nextId = 0x1000;
    private readonly Dictionary<uint, Connection> _byId = [];

    /// <summary>Una conexión abierta: el id con el que nos llega (O→T, lo damos nosotros), el id con el que contestamos (T→O, el del
    /// cliente), su triada (serie, fabricante, serie del originador) y el tamaño máximo de lo que devolvemos.</summary>
    public sealed record Connection(uint OtoTId, uint TtoOId, ushort Serial, ushort Vendor, uint OriginatorSerial, int TtoOSize);

    public IReadOnlyCollection<Connection> Open => _byId.Values;

    /// <summary>La última que se abrió (para el registro).</summary>
    public Connection? LastOpened { get; private set; }

    /// <summary>Si la petición es del Connection Manager (Forward_Open/Close a 6/1) y, entonces, su respuesta.</summary>
    public static bool IsConnectionManager(ReadOnlySpan<byte> request) =>
        request.Length >= 2 && request[0] is ForwardOpen or LargeForwardOpen or ForwardClose &&
        2 + request[1] * 2 <= request.Length && CipPath.TryParse(request.Slice(2, request[1] * 2), out var path) &&
        path.Class == 6 && path.Instance == 1;

    public byte[] Handle(ReadOnlySpan<byte> request)
    {
        var service = request[0];
        var data = request[(2 + request[1] * 2)..];
        return service == ForwardClose ? Close(data) : Opening(service, data);
    }

    /// <summary>La conexión de ese id (el que trae el elemento 0xA1 de SendUnitData), o null.</summary>
    public Connection? Find(uint otoTId) => _byId.GetValueOrDefault(otoTId);

    private byte[] Opening(byte service, ReadOnlySpan<byte> data)
    {
        var large = service == LargeForwardOpen;
        var parameters = large ? 4 : 2;
        var fixedLength = 2 + 4 + 4 + 2 + 2 + 4 + 1 + 3 + (4 + parameters) * 2 + 1 + 1;
        if (data.Length < fixedLength) return Reply(service, NotEnoughData, []);

        var ttoOId = BinaryPrimitives.ReadUInt32LittleEndian(data[6..]);
        var serial = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
        var vendor = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        var originator = BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);
        var at = 22;
        var otoTRpi = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
        at += 4 + parameters;
        var ttoORpi = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
        var ttoOParameters = large ? BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 4)..]);
        at += 4 + parameters;
        var transport = data[at];
        var pathWords = data[at + 1];
        if (data.Length < at + 2 + pathWords * 2) return Reply(service, NotEnoughData, []);

        // Solo mensajes explícitos (clase 3); las E/S cíclicas (clase 0/1, UDP) son otra cosa (PENDIENTES 197)
        if ((transport & 0x0F) != 3)
            return Fail(service, TransportNotSupported, [.. Triad(serial, vendor, originator), 0, 0]);
        if (!CipPath.TryParse(SkipPort(data.Slice(at + 2, pathWords * 2)), out _)) return Reply(service, PathSegmentError, []);

        var size = (int)(ttoOParameters & (large ? 0xFFFFu : 0x01FFu));
        var connection = new Connection((uint)Interlocked.Increment(ref _nextId), ttoOId, serial, vendor, originator, size);
        foreach (var old in _byId.Values.Where(c => SameTriad(c, serial, vendor, originator)).ToList()) _byId.Remove(old.OtoTId);
        _byId[connection.OtoTId] = connection;
        LastOpened = connection;

        var reply = new byte[26];
        BinaryPrimitives.WriteUInt32LittleEndian(reply, connection.OtoTId);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), ttoOId);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(8), serial);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(10), vendor);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(12), originator);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(16), otoTRpi);   // intervalo real = el pedido
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(20), ttoORpi);
        // reply[24] = tamaño de la respuesta de la aplicación (0), reply[25] = reservado
        return Reply(service, 0, reply);
    }

    /// <summary>
    /// Forward_Close: cierra la conexión de esa triada. Si no hay ninguna, también contesta bien: la pantalla manda uno antes de abrir, para
    /// limpiar lo que hubiera quedado, y no necesita saber que no había nada.
    /// </summary>
    private byte[] Close(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10) return Reply(ForwardClose, NotEnoughData, []);
        var serial = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        var vendor = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        var originator = BinaryPrimitives.ReadUInt32LittleEndian(data[6..]);
        foreach (var old in _byId.Values.Where(c => SameTriad(c, serial, vendor, originator)).ToList()) _byId.Remove(old.OtoTId);
        return Reply(ForwardClose, 0, [.. Triad(serial, vendor, originator), 0, 0]);
    }

    /// <summary>La ruta de la conexión empieza por el puerto (<c>01 00</c>: puerto 1, enlace 0) y sigue con el Message Router (<c>20 02 24 01</c>).</summary>
    private static ReadOnlySpan<byte> SkipPort(ReadOnlySpan<byte> path)
    {
        while (path.Length >= 2 && (path[0] & 0xE0) == 0)   // segmento de puerto (tipo 000): puerto + enlace (sin dirección larga)
            path = path[2..];
        return path;
    }

    private static bool SameTriad(Connection c, ushort serial, ushort vendor, uint originator) =>
        c.Serial == serial && c.Vendor == vendor && c.OriginatorSerial == originator;

    private static byte[] Triad(ushort serial, ushort vendor, uint originator)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, serial);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), vendor);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), originator);
        return bytes;
    }

    /// <summary>ConnectionFailure con un estado adicional (una palabra) con el motivo, y detrás la triada y la ruta restante (0).</summary>
    private static byte[] Fail(byte service, ushort extended, byte[] data)
    {
        var failed = new byte[6 + data.Length];
        failed[0] = (byte)(service | 0x80);
        failed[2] = ConnectionFailure;
        failed[3] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(failed.AsSpan(4), extended);
        data.CopyTo(failed, 6);
        return failed;
    }

    private static byte[] Reply(byte service, byte status, byte[] data)
    {
        var reply = new byte[4 + data.Length];
        reply[0] = (byte)(service | 0x80);
        reply[2] = status;
        data.CopyTo(reply, 4);
        return reply;
    }
}
