using FactoryNodes.Iec61131.Model;

namespace FactoryNodes.Iec61131.Emission;

/// <summary>
/// Lo que un emisor recibe además de la librería. Todo lo que no sale de la librería ni del proyecto (la hora, por
/// ejemplo) llega aquí, porque las libs no leen el reloj ni el disco.
/// </summary>
/// <param name="CreationTime">Fecha y hora de generación, para las cabeceras que la llevan (Sysmac).</param>
/// <param name="CycleVariablesRetain">
/// Las variables de ciclo de los bloques (<c>VAR_TEMP</c>: se escriben antes de leerse en cada ejecución, así que da igual si son
/// retentivas) se declaran <b>retentivas</b> en el PLC. Opción del autómata del proyecto (autor, 2026-10-29): en Sysmac la memoria y el
/// número de variables no retentivas y retentivas tienen límites separados, y así se reparte. False = como siempre (no retentivas).
/// Solo lo usa Sysmac; TIA las deja en <c>VAR</c>.
/// </param>
/// <param name="EnableVariables">
/// Sysmac: cada FB lleva <c>EN_IN</c>/<c>EN_OUT</c> (la costumbre de Omron, útiles en ladder; regla validada). False = sin ellas
/// (opción «Bloques sin EN_IN/EN_OUT» del autómata, autor, 2026-10-29): dos variables menos por instancia de bloque, para el límite de
/// variables no retentivas. TIA no las tiene nunca.
/// </param>
/// <param name="ConvenienceOutputs">
/// Sysmac: los bloques de elemento (<c>*_Block</c>) llevan las salidas de comodidad de la regla 4 (<c>Active_Q</c>, <c>Alarm</c>, <c>Siren</c>,
/// <c>ALM_Word</c>…: copias de <c>Status</c>). False = sin ellas ni sus asignaciones (opción «Bloques sin salidas de comodidad» del
/// autómata, autor, 2026-10-29): el programa generado no las lee (saca las salidas de <c>Status</c>); solo las pierde quien las conecte a
/// mano en ladder.
/// </param>
public sealed record EmitContext(DateTimeOffset CreationTime, bool CycleVariablesRetain = false, bool EnableVariables = true,
    bool ConvenienceOutputs = true);

/// <summary>
/// Un emisor para una familia de autómatas: traduce la librería maestra (y, más adelante, el programa del proyecto) a
/// los ficheros que importa su herramienta. Cada plataforma es una DLL propia (<c>FactoryNodes.Emitters.&lt;Plataforma&gt;</c>)
/// que implementa esta interfaz; quien genera el código trabaja solo con ella y elige el emisor por
/// <see cref="Platform"/>. Añadir un PLC nuevo (CODESYS, TwinCAT…) es añadir otra DLL, sin tocar el resto.
/// </summary>
public interface IPlcEmitter
{
    /// <summary>Identificador estable de la plataforma ("TIA", "Sysmac"…): el que se guarda en el proyecto.</summary>
    string Platform { get; }

    /// <summary>Nombre para mostrar ("Siemens TIA Portal").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Genera la salida Hardware: tipos y bloques de la librería, más las funciones de compatibilidad que necesite la
    /// plataforma. Si el resultado tiene errores, los ficheros no deben usarse.
    /// </summary>
    EmitResult EmitHardware(MasterLibrary library, EmitContext context);

    /// <summary>
    /// Genera la salida Programa del proyecto (datos de los elementos, instancias por sección, mapeo de señales y salidas
    /// físicas), que se importa después de la Hardware.
    /// </summary>
    EmitResult EmitProgram(ProgramModel program, EmitContext context);

    /// <summary>Si publicar las E/S en red es una opción al exportar (<see cref="ProgramModel.PublishIo"/>): se pregunta.</summary>
    bool CanPublishIo => false;

    /// <summary>Cómo se leen las variables del programa por OPC-UA en esta plataforma, o null si aún no está validado.</summary>
    IPlcAddressing? Addressing => null;

    /// <summary>
    /// El código de una sección generada (también la de salidas físicas, <see cref="ProgramModel.OutputSection"/>) como lo escribe esta
    /// plataforma, con la línea de cada sentencia (para el depurador de SoftNodes), o null si no lo sabe dar o es una sección de usuario.
    /// <paramref name="library"/>: la del programa, para escribir el código del usuario como al exportar.
    /// </summary>
    SectionListing? ListSection(ProgramModel program, ProgramSection section, MasterLibrary? library = null) => null;

    /// <summary>
    /// Librería y programa juntos, como los exporta la app. Por defecto, los ficheros de las dos salidas; una
    /// plataforma que pueda los junta en uno (Sysmac: un solo XML).
    /// </summary>
    EmitResult EmitProject(MasterLibrary library, ProgramModel program, EmitContext context)
    {
        var hardware = EmitHardware(library, context);
        var code = EmitProgram(program, context);
        return new EmitResult([.. hardware.Files, .. code.Files], [.. hardware.Diagnostics, .. code.Diagnostics]);
    }
}
