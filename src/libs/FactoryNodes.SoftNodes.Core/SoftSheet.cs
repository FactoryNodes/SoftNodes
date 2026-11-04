namespace FactoryNodes.SoftNodes;

/// <summary>Quién manda una entrada física en la instalación.</summary>
public enum SoftControlKind
{
    /// <summary>«Correcto» que una persona puede dejar caer: guardamotor, seccionador, setas, relé de seguridad, variador, alarma externa. La planta lo mantiene correcto.</summary>
    Protection,

    /// <summary>Posición de un selector del cuadro (Auto, Manual): dos señales del mismo equipo forman un selector Auto / 0 / Man.</summary>
    Selector,

    /// <summary>Pulsador del cuadro (reset de alarmas, marcha, paro…).</summary>
    Pushbutton,

    /// <summary>Señal de proceso (nivel, presencia, fotocélula…): no la mueve la planta ni un mando fijo del cuadro.</summary>
    Signal,

    /// <summary>Respuesta de la instalación a una orden (contactor que confirma, válvula que llega, tajadera, sensor de giro): la hace la planta.</summary>
    Feedback
}

/// <summary>
/// Una entrada física de una hoja del sinóptico tal como la ve quien maneja el cuadro simulado.
/// </summary>
/// <param name="Variable">Nombre en <c>io_&lt;programa&gt;</c> (<c>e_M101_QM</c>).</param>
/// <param name="Label">Nombre propio junto al punto: el del hijo (QM, KM…) o el del equipo.</param>
/// <param name="Element">Equipo del sinóptico al que pertenece (<c>M101</c>); agrupa sus señales.</param>
/// <param name="Member">Miembro del bloque al que está conectada (<c>QM_OK</c>, <c>Sel_Auto</c>); vacío si no se conectó a ninguno.</param>
/// <param name="Inverted">El programa la lee negada: el valor lógico es el contrario del que hay en la variable.</param>
/// <param name="X">Centro en el mundo del sinóptico (para colocarla en su sitio más adelante).</param>
public sealed record SoftControl(string Variable, string Label, string Element, string Member, SoftControlKind Kind, bool Inverted, string DataType,
    double X, double Y);

/// <summary>
/// Un dibujo del sinóptico (un equipo o el dibujo de un hijo suyo: KM, QM, un sensor…) en su sitio: caja de la esquina
/// superior izquierda, con el giro y el reflejo de la hoja. Coordenadas del mundo del sinóptico, las mismas que las de
/// <see cref="SoftControl.X"/>/<see cref="SoftControl.Y"/>.
/// </summary>
/// <param name="Svg">El dibujo del catálogo (SVG); vacío si el elemento no tiene.</param>
public sealed record SoftDrawing(string Name, string Svg, double X, double Y, double Width, double Height, double Rotation = 0,
    bool FlipHorizontal = false, bool FlipVertical = false);

/// <summary>
/// Una hoja del sinóptico con las entradas físicas del cuadro que se pueden simular (una pestaña de SoftNodes) y, para poner
/// los mandos en su sitio, el dibujo de la hoja.
/// </summary>
/// <param name="Drawings">Los equipos y los dibujos de sus hijos, de abajo arriba; null en un paquete sin dibujos.</param>
public sealed record SoftSheet(string Name, IReadOnlyList<SoftControl> Controls, IReadOnlyList<SoftDrawing>? Drawings = null);
