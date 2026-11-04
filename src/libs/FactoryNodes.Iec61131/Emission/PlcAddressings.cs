namespace FactoryNodes.Iec61131.Emission;

// Los dos direccionamientos OPC UA (2026-11-04, PENDIENTES 159): estaban en las DLL de los emisores; aquí, porque solo dicen cómo nombra cada PLC sus
// variables (docs/OPCUA_RULES.md) y los necesita el SoftNodes abierto, que no lleva emisores.

/// <summary>
/// Variables del programa en el servidor OPC-UA del S7-1500 (validado con PLCSIM Advanced, docs/OPCUA_RULES.md):
/// espacio de nombres <c>http://www.siemens.com/simatic-s7-opcua</c> y cada tramo entre comillas, separados por punto:
/// <c>"hw_Prueba"."Alveolar_001"."Status"."Active_Q"</c>; una marca, <c>"Clock_1Hz"</c>. Un elemento de un array lleva el
/// índice fuera de las comillas: <c>"hw_P"."Topo_Planta_Rt"."Elements"[3]."Run"</c> (por validar en el PLC).
/// </summary>
public sealed class TiaAddressing : IPlcAddressing
{
    public string Platform => "TIA";

    public string OpcUaNamespaceUri => "http://www.siemens.com/simatic-s7-opcua";

    public string OpcUaIdentifier(IReadOnlyList<string> path) => string.Join(".", path.Select(Segment));

    /// <summary>
    /// En SCL: el DB (o la marca) entre comillas y los miembros sin ellas, como los escribe el emisor:
    /// <c>"hw_P".Cinta211.Status.Active_Q</c>, <c>"hw_P".Topo_Planta_Rt.Elements[12].Mode</c>, <c>"Clock_1Hz"</c>.
    /// </summary>
    public string ProgramText(IReadOnlyList<string> path) => string.Join(".", path.Select((s, i) => i == 0 ? $"\"{s}\"" : s));

    /// <summary><c>Status</c> → <c>"Status"</c>; <c>Elements[3]</c> → <c>"Elements"[3]</c>.</summary>
    private static string Segment(string segment)
    {
        var bracket = segment.IndexOf('[');
        return bracket > 0 && segment.EndsWith(']') ? $"\"{segment[..bracket]}\"{segment[bracket..]}" : $"\"{segment}\"";
    }
}

/// <summary>
/// Variables del programa en el servidor OPC-UA de un controlador NX/NJ de Omron (visto en el simulador de Sysmac
/// Studio, «NxSimulatorOpcUaServer», docs/OPCUA_RULES.md): las globales cuelgan de <c>NxController.GlobalVars</c> con
/// su nombre tal cual y los miembros separados por punto: <c>Alveolar_001.Status.HMI</c>, <c>Alveolar_001_Contactor</c>.
/// En Sysmac no hay DB: <c>hw_&lt;P&gt;</c> e <c>io_&lt;P&gt;</c> son solo el grupo de la ruta neutra y no van en el
/// identificador. Solo se ven las globales publicadas (publicación en red).
/// </summary>
public sealed class SysmacAddressing : IPlcAddressing
{
    public string Platform => "Sysmac";

    /// <summary>La del simulador; la del PLC real se acepta por patrón (<see cref="IsVariablesNamespace"/>).</summary>
    public string OpcUaNamespaceUri => "urn:OMRON:NxOpcUaSimulatorServer:FactoryAutomationSimulation";

    /// <summary>
    /// Simulador: <c>urn:OMRON:NxOpcUaSimulatorServer:FactoryAutomationSimulation</c>. PLC real: por confirmar; se aceptan
    /// las de Omron que terminan en <c>FactoryAutomation…</c> (<c>urn:OMRON:NxOpcUaServer:FactoryAutomation</c>).
    /// </summary>
    public bool IsVariablesNamespace(string uri) =>
        uri.StartsWith("urn:OMRON:", StringComparison.OrdinalIgnoreCase) &&
        uri.Contains(":FactoryAutomation", StringComparison.OrdinalIgnoreCase);

    public string OpcUaIdentifier(IReadOnlyList<string> path) =>
        string.Join(".", path.Count > 1 && IsGroup(path[0]) ? path.Skip(1) : path);

    /// <summary>En el código, igual que en el OPC-UA: la global por su nombre, sin el grupo (<c>Cinta211.Status.Active_Q</c>).</summary>
    public string ProgramText(IReadOnlyList<string> path) => OpcUaIdentifier(path);

    /// <summary><c>hw_…</c> e <c>io_…</c>: el grupo de la ruta neutra (DB en TIA), que en Sysmac no existe.</summary>
    private static bool IsGroup(string segment) =>
        segment.StartsWith("hw_", StringComparison.OrdinalIgnoreCase) || segment.StartsWith("io_", StringComparison.OrdinalIgnoreCase);
}
