namespace FactoryNodes.Monitoring;

/// <summary>
/// Un driver de comunicación con el PLC, tal como se elige en pantalla (PENDIENTES 165): su nombre, el protocolo que monta
/// <see cref="EndpointPlcConnection"/>, el esquema con el que se guarda la dirección y lo que pide al usuario.
/// </summary>
/// <param name="Id">Lo que se guarda en el proyecto («OpcUa», «EtherNetIp»).</param>
/// <param name="Name">Lo que se ve («OPC UA», «EtherNet/IP»; nombres propios, no se traducen).</param>
/// <param name="Scheme">Esquema de la dirección (<c>opc.tcp</c>, <c>eip</c>).</param>
/// <param name="DefaultPort">Puerto si no se escribe otro.</param>
/// <param name="HasSecurity">Si pide seguridad, usuario y contraseña (solo OPC UA).</param>
/// <param name="ShowsPort">Si el puerto se enseña: en EtherNet/IP es siempre el 44818 y no se pregunta (autor).</param>
public sealed record PlcDriver(string Id, string Name, PlcProtocol Protocol, string Scheme, int DefaultPort, bool HasSecurity, bool ShowsPort)
{
    /// <summary>La dirección que se guarda para este driver: <c>opc.tcp://host:puerto</c>; <c>eip://host</c> (con puerto solo si no es el de serie).</summary>
    public string Endpoint(string host, int? port = null)
    {
        host = host.Trim();
        if (host.Length == 0) return "";
        var p = port is > 0 and <= 65535 ? port.Value : DefaultPort;
        return ShowsPort || p != DefaultPort ? $"{Scheme}://{host}:{p}" : $"{Scheme}://{host}";
    }
}

/// <summary>Los drivers que existen. Añadir uno = una entrada aquí, su <see cref="PlcProtocol"/> y su <see cref="IPlcConnection"/>.</summary>
public static class PlcDrivers
{
    public static readonly PlcDriver OpcUa = new("OpcUa", "OPC UA", PlcProtocol.OpcUa, "opc.tcp", 4840, HasSecurity: true, ShowsPort: true);

    public static readonly PlcDriver EtherNetIp = new("EtherNetIp", "EtherNet/IP", PlcProtocol.EtherNetIp, "eip", 44818, HasSecurity: false, ShowsPort: false);

    public static readonly IReadOnlyList<PlcDriver> All = [OpcUa, EtherNetIp];

    /// <summary>Por su id guardado; null si no es ninguno (vacío o desconocido).</summary>
    public static PlcDriver? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(d => d.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));

    public static PlcDriver For(PlcProtocol protocol) => All.First(d => d.Protocol == protocol);

    /// <summary>
    /// El driver que manda: el guardado; si no hay, el que dice la dirección (<see cref="EndpointPlcConnection.ProtocolOf"/>); sin
    /// dirección, OPC UA (lo de siempre).
    /// </summary>
    public static PlcDriver Resolve(string? id, string endpoint) =>
        Find(id) ?? (string.IsNullOrWhiteSpace(endpoint) ? OpcUa : For(EndpointPlcConnection.ProtocolOf(endpoint)));

    /// <summary>
    /// Separa una dirección: <c>opc.tcp://10.1.2.3:4840/</c> → («10.1.2.3», 4840); <c>eip://10.1.2.3</c> o <c>10.1.2.3</c> → («10.1.2.3», null).
    /// El puerto es null si no está escrito (cada driver pone el suyo).
    /// </summary>
    public static (string Host, int? Port) Split(string endpoint)
    {
        var text = (endpoint ?? "").Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) text = text[(scheme + 3)..];
        text = text.TrimEnd('/');
        var slash = text.IndexOf('/');
        if (slash >= 0) text = text[..slash];
        var colon = text.LastIndexOf(':');
        return colon > 0 && int.TryParse(text[(colon + 1)..], out var port) ? (text[..colon], port) : (text, null);
    }
}
