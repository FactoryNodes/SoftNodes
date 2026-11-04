using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using FactoryNodes.Iec61131.Emission;

namespace FactoryNodes.SoftNodes.Modbus;

/// <summary>
/// Servidor Modbus TCP de una instancia de SoftNodes (PENDIENTES 199): escucha en TCP 502 (o el puerto de la instancia) y contesta con
/// <see cref="ModbusRequestHandler"/> sobre la memoria del programa y su mapa. Una tarea por cliente; varios a la vez. El número de esclavo
/// (unit id) no se mira: se contesta a todos con el mismo.
/// </summary>
public sealed class SoftModbusServer : IAsyncDisposable
{
    public const int DefaultPort = 502;

    /// <summary>Quién se conecta y se va, y cada petición contestada con una excepción (con sus bytes).</summary>
    public event Action<string>? Logged;

    private readonly ModbusRequestHandler _handler;
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private Task? _accepting;
    private int _clients;

    /// <param name="port">Puerto TCP; 0 = elige uno libre (ver <see cref="Port"/>).</param>
    public SoftModbusServer(SoftProgram program, ProgramModbus? map, int port = DefaultPort)
    {
        _handler = new ModbusRequestHandler(program, map);
        Port = port;
    }

    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    public int Clients => _clients;

    /// <summary>Cuántas variables del mapa se sirven y lo que no se puede servir.</summary>
    public int Served => _handler.Served;

    public IReadOnlyList<string> Problems => _handler.Problems;

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

    /// <summary>
    /// La trama: cabecera MBAP de 7 bytes (transacción, protocolo 0, longitud de lo que sigue, unit id) y la PDU. La respuesta lleva la misma
    /// transacción y el mismo unit id.
    /// </summary>
    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        Interlocked.Increment(ref _clients);
        using var _ = client;
        client.NoDelay = true;
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Logged?.Invoke($"Modbus TCP: cliente {remote} conectado.");
        var stream = client.GetStream();
        var header = new byte[7];
        var why = "cerró la conexión";
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, stop);
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) != 0 || length is < 2 or > 254)
                {
                    why = $"mandó una cabecera que no es de Modbus TCP ({Convert.ToHexString(header)})";
                    return;
                }

                var pdu = new byte[length - 1];
                await stream.ReadExactlyAsync(pdu, stop);
                var answer = _handler.Handle(pdu);
                if (answer.Length == 2 && (answer[0] & 0x80) != 0)
                    Logged?.Invoke($"Modbus TCP: a {remote}, función 0x{pdu[0]:X2} contestada con la excepción {answer[1]:D2}. Petición: {Convert.ToHexString(pdu[..Math.Min(pdu.Length, 32)])}");

                var frame = new byte[7 + answer.Length];
                header.AsSpan(0, 4).CopyTo(frame);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(answer.Length + 1));
                frame[6] = header[6];
                answer.CopyTo(frame, 7);
                await stream.WriteAsync(frame, stop);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or EndOfStreamException or OperationCanceledException or ObjectDisposedException)
        {
            why = e is OperationCanceledException ? "el servidor se para" : $"se fue ({e.GetType().Name})";
        }
        catch (Exception e)
        {
            why = $"fallo al atenderle: {e.GetType().Name}: {e.Message}";
        }
        finally
        {
            Interlocked.Decrement(ref _clients);
            Logged?.Invoke($"Modbus TCP: cliente {remote} desconectado ({why}).");
        }
    }
}
