namespace FactoryNodes.SoftNodes;

/// <summary>
/// Un lazo PID que SoftNodes simula (proceso «Lazo PID», docs/design/PID.md §10): lo saca FactoryNodes de cada PID del sinóptico con la
/// medida en un Analog. SoftNodes no sabe qué es un PID: lee lo que mueve el actuador, lo pasa por el modelo del proceso y escribe la
/// medida en la entrada analógica bruta.
/// </summary>
/// <param name="Pid">La variable de datos del PID (<c>PID101</c>), para enseñar su consigna, su medida y su salida.</param>
/// <param name="Input">Expresión ST de lo que mueve de verdad el actuador (<c>M101.SetpointActual</c>, <c>G1.Status.Position_Pct</c>).</param>
/// <param name="Running">Expresión ST de si el actuador está en marcha (<c>M101.Status.Active_Q</c>); parado, la entrada del proceso es 0.</param>
/// <param name="MeasureVariable">La entrada analógica en <c>io_</c> que lee el Analog de la medida (<c>e_TT101</c>).</param>
/// <param name="MeasureElement">La variable de datos de ese Analog (<c>TT101</c>): de ella salen la escala y el bruto.</param>
public sealed record SoftPidLoop(string Pid, string Input, string Running, string MeasureVariable, string MeasureElement);
