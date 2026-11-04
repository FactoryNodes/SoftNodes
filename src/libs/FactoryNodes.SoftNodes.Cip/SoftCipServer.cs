using System.Buffers.Binary;
using System.Text;
using System.Net;
using System.Net.Sockets;
using FactoryNodes.Monitoring;

namespace FactoryNodes.SoftNodes.Cip;

/// <summary>
/// Servidor EtherNet/IP de una instancia de SoftNodes (PENDIENTES 168): escucha en TCP 44818 (o el puerto de la instancia), registra
/// sesiones y contesta a los mensajes explícitos sin conexión (SendRRData) con <see cref="CipRequestHandler"/> sobre la memoria del
/// programa, además de ListIdentity. Con él, el cliente de FactoryNodes (<c>CipPlcConnection</c>), otro SoftNodes o un PLC real le leen y
/// escriben variables por nombre como a un Omron NJ/NX. Una tarea por cliente; varias sesiones a la vez.
/// </summary>
public sealed class SoftCipServer : IAsyncDisposable
{
    public const int DefaultPort = 44818;
    private const ushort ListServices = 0x04, ListIdentity = 0x63, RegisterSession = 0x65, UnregisterSession = 0x66, SendRRData = 0x6F,
        SendUnitData = 0x70;
    private const uint UnsupportedCommand = 0x0001, IncorrectData = 0x0003, InvalidSession = 0x0064;

    /// <summary>
    /// Lo que conviene ver en el registro de la instancia (PENDIENTES 167, la prueba con el NJ101 real): quién se conecta y se va, cada comando
    /// que no se entiende, cada petición CIP contestada con error (con sus bytes) y cualquier fallo al atender a un cliente. Las lecturas que
    /// van bien no se apuntan.
    /// </summary>
    public event Action<string>? Logged;

    /// <summary>Cuántas peticiones de cada cliente se apuntan enteras (bytes de ida y vuelta) en el registro.</summary>
    public const int TracedRequests = 12;

    private readonly CipRequestHandler _handler;
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private Task? _accepting;
    private int _nextSession;

    /// <param name="port">Puerto TCP; 0 = elige uno libre (ver <see cref="Port"/>).</param>
    public SoftCipServer(SoftProgram program, string name, int port = DefaultPort)
    {
        _handler = new CipRequestHandler(program, CipServerIdentity.SoftNodes(name));
        Port = port;
    }

    public int Port { get; private set; }

    /// <summary>La dirección a la que se conecta un cliente de esta máquina (<c>eip://localhost</c>, con el puerto si no es el 44818).</summary>
    public string Endpoint => PlcDrivers.EtherNetIp.Endpoint("localhost", Port);

    public bool IsRunning => _listener is not null;

    /// <summary>Cuántos clientes hay conectados ahora.</summary>
    public int Clients => _clients;

    private int _clients;

    /// <summary>Empieza a escuchar. Falla (<see cref="SocketException"/>) si el puerto está ocupado.</summary>
    public Task StartAsync()
    {
        if (_listener is not null) return Task.CompletedTask;
        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listener = listener;
        _stop = new CancellationTokenSource();
        _accepting = AcceptAsync(listener, _stop.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        if (listener is null) return;
        _listener = null;
        _stop?.Cancel();
        listener.Stop();
        if (_accepting is { } accepting)
            try { await accepting; } catch (OperationCanceledException) { }
        _stop?.Dispose();
        _stop = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task AcceptAsync(TcpListener listener, CancellationToken stop)
    {
        var clients = new List<Task>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    break;
                }

                clients.RemoveAll(t => t.IsCompleted);
                clients.Add(ServeAsync(client, stop));
            }
        }
        finally
        {
            await Task.WhenAll(clients.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        Interlocked.Increment(ref _clients);
        using var _ = client;
        client.NoDelay = true;
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Logged?.Invoke($"EtherNet/IP: cliente {remote} conectado.");
        var stream = client.GetStream();
        var header = new byte[24];
        uint session = 0;
        var connections = new CipConnections();   // las de clase 3 de este cliente: mueren con él
        var served = 0;
        var why = "cerró la conexión";
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, stop);
                var command = BinaryPrimitives.ReadUInt16LittleEndian(header);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
                var requestSession = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, stop);

                byte[] reply;
                uint status = 0;
                switch (command)
                {
                    case RegisterSession:
                        session = (uint)Interlocked.Increment(ref _nextSession);
                        reply = [1, 0, 0, 0];   // versión 1, opciones 0
                        break;
                    case UnregisterSession:
                        why = "cerró la sesión";
                        return;
                    case ListIdentity:
                        reply = IdentityItem(((IPEndPoint)client.Client.LocalEndPoint!).Address);
                        break;
                    case ListServices:
                        reply = ServicesItem();
                        break;
                    case SendRRData when session != 0 && requestSession == session:
                        reply = RRData(body, remote, connections);
                        break;
                    case SendUnitData when session != 0 && requestSession == session:
                        (status, reply) = UnitData(body, remote, connections);
                        break;
                    case SendRRData or SendUnitData:
                        (status, reply) = (InvalidSession, []);
                        Logged?.Invoke($"EtherNet/IP: {remote} manda 0x{command:X4} con la sesión {requestSession:X8} (la suya es {session:X8}): 0x0064.");
                        break;
                    default:
                        (status, reply) = (UnsupportedCommand, []);
                        Logged?.Invoke($"EtherNet/IP: {remote} manda el comando 0x{command:X4}, que no se atiende: 0x0001. Bytes: {Hex(header)} {Hex(body)}");
                        break;
                }

                var frame = new byte[24 + reply.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(frame, command);
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)reply.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), command == RegisterSession ? session : requestSession);
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), status);
                header.AsSpan(12, 8).CopyTo(frame.AsSpan(12));   // el contexto del que pregunta vuelve tal cual
                reply.CopyTo(frame, 24);
                await stream.WriteAsync(frame, stop);
                served++;
                // Las primeras peticiones de cada cliente, enteras, y luego un resumen cada mil: para ver qué manda un PLC real (NJ101, 2026-10-30)
                if (served <= TracedRequests)
                    Logged?.Invoke($"EtherNet/IP: {remote} #{served} comando 0x{command:X4} ({Hex(header)} {Hex(body)}) → {Hex(frame)}");
                else if (served % 1000 == 0)
                    Logged?.Invoke($"EtherNet/IP: {remote}: {served} peticiones atendidas.");
            }
        }
        catch (Exception e) when (e is IOException or SocketException or EndOfStreamException or OperationCanceledException or ObjectDisposedException)
        {
            // el cliente se fue o el servidor se para
            why = e is OperationCanceledException ? "el servidor se para" : $"se fue ({e.GetType().Name})";
        }
        catch (Exception e)
        {
            why = $"fallo al atenderle: {e.GetType().Name}: {e.Message}";
        }
        finally
        {
            Interlocked.Decrement(ref _clients);
            Logged?.Invoke($"EtherNet/IP: cliente {remote} desconectado ({why}).");
        }
    }

    /// <summary>SendRRData: asa de interfaz, tiempo, y dos elementos (dirección nula, datos sin conexión 0xB2 con la petición CIP).</summary>
    private byte[] RRData(byte[] body, string remote, CipConnections connections)
    {
        var items = Items(body);
        var request = items.FirstOrDefault(i => i.Type == 0xB2).Data;
        byte[] cip;
        if (request is null)
        {
            cip = [0x80, 0, 0x13, 0];
            Logged?.Invoke($"EtherNet/IP: {remote} manda SendRRData sin elemento de datos 0xB2: {Hex(body)}");
        }
        else if (CipConnections.IsConnectionManager(request))
        {
            cip = connections.Handle(request);
            Logged?.Invoke(cip[2] == 0 && request[0] != CipConnections.ForwardClose && connections.LastOpened is { } opened
                ? $"EtherNet/IP: {remote} abre una conexión de mensajes (clase 3): id 0x{opened.OtoTId:X8}, respuestas a 0x{opened.TtoOId:X8}, hasta {opened.TtoOSize} bytes."
                : $"EtherNet/IP: {remote}, servicio 0x{request[0]:X2} del Connection Manager contestado con estado 0x{cip[2]:X2}. Petición: {Hex(request)}");
        }
        else
            cip = Answer(request, remote);

        var reply = new byte[16 + cip.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(12), 0xB2);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(14), (ushort)cip.Length);
        cip.CopyTo(reply, 16);
        return reply;
    }

    /// <summary>
    /// SendUnitData (0x70): una petición por una conexión de clase 3 abierta con Forward_Open. Dos elementos: dirección con conexión 0xA1
    /// (el id O→T que le dimos) y datos con conexión 0xB1 (contador de secuencia + la petición CIP). Se contesta con el id T→O del cliente y
    /// el mismo contador. Un id que no es de una conexión abierta: estado 0x0003 en la cabecera.
    /// </summary>
    private (uint Status, byte[] Reply) UnitData(byte[] body, string remote, CipConnections connections)
    {
        var items = Items(body);
        var address = items.FirstOrDefault(i => i.Type == 0xA1).Data;
        var data = items.FirstOrDefault(i => i.Type == 0xB1).Data;
        var connection = address is { Length: 4 } ? connections.Find(BinaryPrimitives.ReadUInt32LittleEndian(address)) : null;
        if (connection is null || data is not { Length: >= 2 })
        {
            Logged?.Invoke($"EtherNet/IP: {remote} manda SendUnitData por una conexión que no está abierta: 0x0003. Bytes: {Hex(body)}");
            return (IncorrectData, []);
        }

        var cip = Answer(data[2..], remote);
        if (cip.Length > connection.TtoOSize)
            Logged?.Invoke($"EtherNet/IP: a {remote}, la respuesta de 0x{data[2]:X2} ({cip.Length} bytes) no cabe en su conexión ({connection.TtoOSize}).");

        var reply = new byte[8 + 4 + 4 + 4 + 2 + cip.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(6), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(8), 0xA1);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(10), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(12), connection.TtoOId);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(16), 0xB1);
        BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(18), (ushort)(2 + cip.Length));
        data.AsSpan(0, 2).CopyTo(reply.AsSpan(20));   // el mismo contador de secuencia
        cip.CopyTo(reply, 22);
        return (0, reply);
    }

    /// <summary>La petición CIP contestada por la memoria; si va mal, al registro con sus bytes.</summary>
    private byte[] Answer(byte[] request, string remote)
    {
        var cip = _handler.Handle(request);
        if (cip.Length > 2 && cip[2] != 0)
            Logged?.Invoke($"EtherNet/IP: a {remote}, servicio 0x{request[0]:X2} contestado con estado 0x{cip[2]:X2}. Petición: {Hex(request)}. Respuesta: {Hex(cip)}");
        return cip;
    }

    /// <summary>Los elementos del CPF (Common Packet Format) tras el asa de interfaz y el tiempo: tipo y datos.</summary>
    private static List<(ushort Type, byte[] Data)> Items(byte[] body)
    {
        var items = new List<(ushort, byte[])>();
        if (body.Length < 8) return items;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6));
        var offset = 8;
        for (var i = 0; i < count && offset + 4 <= body.Length; i++)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset + 2));
            if (offset + 4 + length > body.Length) break;
            items.Add((type, body[(offset + 4)..(offset + 4 + length)]));
            offset += 4 + length;
        }

        return items;
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => bytes.Length == 0 ? "(nada)" : Convert.ToHexString(bytes[..Math.Min(bytes.Length, 96)]) + (bytes.Length > 96 ? "…" : "");

    /// <summary>
    /// ListServices (0x04): un elemento 0x0100 «Communications» (versión 1, capacidad 0x0020: mensajes CIP por TCP). Algunos clientes lo piden
    /// antes de registrar la sesión.
    /// </summary>
    private static byte[] ServicesItem()
    {
        var name = Encoding.ASCII.GetBytes("Communications");
        var item = new byte[4 + 16];
        BinaryPrimitives.WriteUInt16LittleEndian(item, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(2), 0x0020);
        name.CopyTo(item, 4);
        var data = new byte[6 + item.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 0x0100);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)item.Length);
        item.CopyTo(data, 6);
        return data;
    }

    /// <summary>
    /// El elemento 0x0C de ListIdentity: versión, sockaddr (big endian: familia 2, puerto, IP, ceros), fabricante, tipo, producto, revisión,
    /// estado, serie, nombre (SHORT_STRING) y estado del equipo (3 = en marcha).
    /// </summary>
    private byte[] IdentityItem(IPAddress local)
    {
        var id = _handler.Identity;
        var name = CipRequestHandler.ShortString(id.ProductName);
        var item = new byte[33 + name.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(item, 1);
        BinaryPrimitives.WriteUInt16BigEndian(item.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(item.AsSpan(4), (ushort)Port);
        (local.AddressFamily == AddressFamily.InterNetwork ? local : IPAddress.Any).GetAddressBytes().CopyTo(item, 6);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(18), id.Vendor);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(20), id.DeviceType);
        BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(22), id.ProductCode);
        item[24] = id.Major;
        item[25] = id.Minor;
        BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(28), id.Serial);
        name.CopyTo(item, 32);
        item[^1] = 3;

        var data = new byte[6 + item.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 0x0C);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)item.Length);
        item.CopyTo(data, 6);
        return data;
    }
}
