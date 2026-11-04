namespace FactoryNodes.Iec61131.Emission;

/// <summary>
/// Cómo se leen desde fuera las variables del programa que genera un emisor: de la ruta neutra de una variable
/// (<c>hw_Prueba</c>, <c>Alveolar_001</c>, <c>Status</c>, <c>Active_Q</c>) al identificador que usa el servidor OPC-UA
/// de esa plataforma. Va junto al emisor porque describe el mismo programa: un PLC nuevo trae su código y su forma de
/// leerlo (docs/design/MONITORING.md). Cada regla, con su prueba en <c>docs/OPCUA_RULES.md</c>.
/// </summary>
public interface IPlcAddressing
{
    /// <summary>La plataforma, como <see cref="IPlcEmitter.Platform"/>.</summary>
    string Platform { get; }

    /// <summary>URI del espacio de nombres OPC-UA de las variables del PLC (el índice se resuelve al conectar).</summary>
    string OpcUaNamespaceUri { get; }

    /// <summary>
    /// ¿Es este el espacio de nombres de las variables? Por defecto, la URI exacta; una plataforma cuya URI cambia (el
    /// simulador y el PLC real de Omron) decide con su propio patrón.
    /// </summary>
    bool IsVariablesNamespace(string uri) => string.Equals(uri, OpcUaNamespaceUri, StringComparison.Ordinal);

    /// <summary>Identificador de texto (<c>s=</c>) de la variable dentro de ese espacio de nombres.</summary>
    string OpcUaIdentifier(IReadOnlyList<string> path);

    /// <summary>
    /// La variable como se escribe en el código del programa de esta plataforma, para pegarla en su editor (Simulación:
    /// botón derecho ▸ «Copiar la variable»). Sysmac <c>Topo_Planta_Rt.Elements[12].Mode</c>; TIA
    /// <c>"hw_P".Topo_Planta_Rt.Elements[12].Mode</c>.
    /// </summary>
    string ProgramText(IReadOnlyList<string> path);
}
