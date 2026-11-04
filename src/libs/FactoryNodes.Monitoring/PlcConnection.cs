namespace FactoryNodes.Monitoring;

public enum PlcConnectionState
{
    Disconnected,
    Connecting,
    Connected,

    /// <summary>Se perdió la comunicación (el servidor no responde); hay que volver a conectar.</summary>
    Faulted
}

/// <summary>Seguridad del canal: sin seguridad, firmado, o firmado y cifrado.</summary>
public enum PlcSecurity
{
    None,
    Sign,
    SignAndEncrypt
}

/// <summary>Cómo conectar. Sin usuario, anónimo.</summary>
/// <param name="Endpoint">Dirección del servidor (OPC UA: <c>opc.tcp://192.168.1.200:4840</c>; EtherNet/IP: <c>eip://192.168.1.200</c> o la IP sola).</param>
/// <param name="Protocol">
/// El driver elegido por el usuario (PENDIENTES 165); null = se deduce de la dirección (<see cref="EndpointPlcConnection.ProtocolOf"/>).
/// Seguridad, usuario y contraseña solo cuentan en OPC UA.
/// </param>
public sealed record PlcConnectionSettings(
    string Endpoint,
    PlcSecurity Security = PlcSecurity.None,
    string? UserName = null,
    string? Password = null,
    PlcProtocol? Protocol = null);

/// <summary>Quién es el servidor: <c>SIEMENS AG · SIMATIC S7-1500 OPC UA V03.00.00</c>.</summary>
public sealed record PlcServerInfo(string Manufacturer, string Product, string Version)
{
    public override string ToString() => $"{Manufacturer} · {Product} {Version}".Trim();
}

public enum PlcValueStatus
{
    Good,
    Uncertain,
    Bad,

    /// <summary>La variable no existe en el PLC (programa distinto del proyecto o modificado a mano).</summary>
    NotFound,

    /// <summary>Existe pero el servidor no deja leerla o escribirla (permisos, o variable no publicada).</summary>
    AccessDenied
}

/// <summary>Valor de una variable con su calidad y la hora del servidor. <see cref="Detail"/> explica un fallo.</summary>
public sealed record PlcValue(PlcPath Path, object? Value, PlcValueStatus Status, DateTimeOffset Timestamp, string? Detail = null)
{
    public bool IsGood => Status == PlcValueStatus.Good;
}

/// <summary>Una escritura: qué variable y qué valor (se convierte al tipo de la variable en el PLC).</summary>
public sealed record PlcWrite(PlcPath Path, object Value);

public sealed record PlcWriteResult(PlcPath Path, PlcValueStatus Status, string? Detail = null)
{
    public bool Ok => Status == PlcValueStatus.Good;
}

/// <summary>
/// Conexión con un PLC, independiente del protocolo (docs/design/MONITORING.md). La implementa
/// <c>FactoryNodes.Monitoring.OpcUa</c>; el resto de FactoryNodes solo ve esta interfaz. Las variables se nombran con
/// rutas neutras (<see cref="PlcPath"/>): una que no exista en el PLC devuelve <see cref="PlcValueStatus.NotFound"/>, no
/// hace fallar la operación entera.
/// </summary>
public interface IPlcConnection : IAsyncDisposable
{
    PlcConnectionState State { get; }

    /// <summary>El servidor al que está conectado, o null.</summary>
    PlcServerInfo? Server { get; }

    event EventHandler<PlcConnectionState>? StateChanged;

    Task<PlcServerInfo> ConnectAsync(PlcConnectionSettings settings, CancellationToken cancellation = default);

    Task DisconnectAsync();

    Task<IReadOnlyList<PlcValue>> ReadAsync(IReadOnlyList<PlcPath> paths, CancellationToken cancellation = default);

    /// <summary>Escribe; cada valor se convierte al tipo que tiene la variable en el PLC.</summary>
    Task<IReadOnlyList<PlcWriteResult>> WriteAsync(IReadOnlyList<PlcWrite> writes, CancellationToken cancellation = default);

    /// <summary>
    /// Avisa de los cambios de esas variables, como mucho cada <paramref name="interval"/>. El aviso llega en un hilo
    /// del cliente: quien pinta lo pasa a su hilo. La primera notificación trae el valor actual de todas.
    /// </summary>
    Task<IPlcSubscription> SubscribeAsync(IReadOnlyList<PlcPath> paths, TimeSpan interval, Action<IReadOnlyList<PlcValue>> onChange,
        CancellationToken cancellation = default);

    // ---------- Todo lo que publica el PLC (copia de parámetros, docs/design/MONITORING.md §7f) ----------

    /// <summary>
    /// Recorre el servidor y devuelve todas las variables de valor simple (o array de valores simples) que publica,
    /// sean o no del proyecto. <paramref name="progress"/> recibe cuántas lleva.
    /// </summary>
    Task<IReadOnlyList<PlcNode>> BrowseVariablesAsync(Action<int>? progress = null, CancellationToken cancellation = default) =>
        throw new NotSupportedException("Esta conexión no sabe explorar el PLC.");

    /// <summary>Lee variables por su identificador (<see cref="PlcNode.Id"/>).</summary>
    Task<IReadOnlyList<PlcNodeValue>> ReadNodesAsync(IReadOnlyList<string> ids, CancellationToken cancellation = default) =>
        throw new NotSupportedException("Esta conexión no sabe leer por identificador.");

    /// <summary>Escribe variables por su identificador; el texto se convierte al tipo con el que se leyeron.</summary>
    Task<IReadOnlyList<PlcNodeWriteResult>> WriteNodesAsync(IReadOnlyList<PlcNodeWrite> writes, CancellationToken cancellation = default) =>
        throw new NotSupportedException("Esta conexión no sabe escribir por identificador.");
}

/// <summary>Una suscripción activa; se cancela al liberarla.</summary>
public interface IPlcSubscription : IAsyncDisposable
{
    IReadOnlyList<PlcPath> Paths { get; }
}
