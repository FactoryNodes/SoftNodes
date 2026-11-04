using System.Text.Json.Serialization;

namespace FactoryNodes.Devices;

/// <summary>Sentido de una pasarela.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceGatewayDirection
{
    /// <summary>Del equipo al PLC (peso de un visor, velocidad o consumo de un variador).</summary>
    ToPlc,

    /// <summary>Del PLC al equipo: previsto, aún sin hacer (autor, 2026-10-13: «lo definiremos con calma en otro momento»).</summary>
    ToDevice
}

/// <summary>Por dónde habla la caja con un dispositivo (PENDIENTES 45).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceTransport
{
    /// <summary>Modbus TCP (puerto 502; por RS-485, con una pasarela TCP/RTU).</summary>
    ModbusTcp,

    /// <summary>EtherNet/IP, mensajes explícitos (puerto 44818): los parámetros con su dirección CIP del catálogo.</summary>
    EtherNetIp
}

/// <summary>
/// Una pasarela (autor, 2026-10-13): cada <see cref="PeriodMs"/>, el valor de un parámetro del equipo (con su escala) se
/// escribe en una variable del PLC (<c>hw_P.M101.Status.Speed</c>, o la que se diga), por la conexión OPC UA de la caja.
/// </summary>
public sealed class DeviceGateway
{
    public string Parameter { get; set; } = "";
    public string PlcVariable { get; set; } = "";
    public int PeriodMs { get; set; } = 1000;
    public DeviceGatewayDirection Direction { get; set; } = DeviceGatewayDirection.ToPlc;
}

/// <summary>
/// Un dispositivo Modbus TCP de la planta (autor, 2026-10-13): nombre, a qué modelo del catálogo corresponde y cómo se llega
/// (IP, puerto, número de esclavo: el de la pasarela Modbus RTU si el equipo va por RS-485). Se define en FactoryNodes y
/// los habilitados viajan a la caja con las pantallas.
/// </summary>
public sealed class ProjectDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary><see cref="DeviceModel.Key"/> («Omron MX2»).</summary>
    public string ModelKey { get; set; } = "";

    /// <summary>Modbus TCP (lo de siempre) o EtherNet/IP. Con EtherNet/IP, <see cref="Unit"/> no cuenta.</summary>
    public DeviceTransport Transport { get; set; } = DeviceTransport.ModbusTcp;

    public string Host { get; set; } = "";
    public int Port { get; set; } = 502;

    /// <summary>El puerto de cada transporte: 502 Modbus TCP, 44818 EtherNet/IP.</summary>
    public static int DefaultPort(DeviceTransport transport) => transport == DeviceTransport.EtherNetIp ? 44818 : 502;
    public int Unit { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Las pasarelas de antes (2026-10-13). Desde el 2026-10-16 son <c>FvGateway</c> de Factory View ▸ Conexiones ▸ Pasarelas,
    /// de cualquier origen a cualquier destino: al abrir un proyecto se pasan solas y esta lista queda vacía. Solo para leer
    /// ficheros viejos.
    /// </summary>
    public List<DeviceGateway> Gateways { get; set; } = [];

    /// <summary>Por qué no vale como nombre de dispositivo (va en sus variables: <c>modbus.&lt;nombre&gt;.Gross</c>), o vacío.</summary>
    public static string NameProblem(string name) =>
        name.Trim().Length == 0 ? "Falta el nombre."
        : name.Contains('.') ? "El nombre no puede llevar puntos: va en sus variables (modbus.Nombre.Parámetro)."
        : name != name.Trim() ? "El nombre no puede empezar ni acabar en espacio."
        : "";

    /// <summary>Por qué no vale, o vacío.</summary>
    public string Problem(Func<string, DeviceModel?> modelOf)
    {
        if (NameProblem(Name) is { Length: > 0 } nameProblem) return nameProblem;
        if (modelOf(ModelKey) is not { } model) return $"«{ModelKey}» no está en el catálogo de dispositivos.";
        if (Host.Trim().Length == 0) return "Falta la IP.";
        if (Port is < 1 or > 65535) return "El puerto, entre 1 y 65535.";
        if (Transport == DeviceTransport.ModbusTcp && Unit is < 0 or > 255) return "El número de esclavo, entre 0 y 255.";
        if (Transport == DeviceTransport.EtherNetIp && !model.Parameters.Any(p => p.CipAddress is not null))
            return $"El {model.Key} no tiene direcciones EtherNet/IP en el catálogo: va por Modbus TCP.";
        foreach (var g in Gateways)
        {
            if (model.Parameter(g.Parameter) is null) return $"Pasarela: el {model.Key} no tiene el parámetro «{g.Parameter}».";
            if (g.PlcVariable.Trim().Length == 0) return $"Pasarela de {g.Parameter}: falta la variable del PLC.";
            if (g.PeriodMs < 100) return $"Pasarela de {g.Parameter}: cada 100 ms como poco.";
        }

        return "";
    }
}
