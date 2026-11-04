namespace FactoryNodes.SoftNodes;

/// <summary>Cómo da su señal una entrada según la magnitud de su proceso.</summary>
public enum SoftSignalKind
{
    /// <summary>TRUE desde <see cref="SoftSignal.At"/> hacia arriba: una sonda de nivel (la de máximo arriba, la de mínimo abajo).</summary>
    Above,

    /// <summary>TRUE desde <see cref="SoftSignal.At"/> hacia abajo: el final de carrera del principio del recorrido.</summary>
    Below,

    /// <summary>TRUE mientras la magnitud está en <see cref="SoftSignal.At"/> (± <see cref="SoftSignal.NearTolerance"/>): el inductivo de una posición del carro.</summary>
    Near,

    /// <summary>Una entrada analógica (un radar): la magnitud de su mínimo a su máximo, en el bruto de la tarjeta o, si es REAL, escalada.</summary>
    Analog
}

/// <summary>
/// Una entrada física (<c>io_</c>) que da un proceso simulado.
/// </summary>
/// <param name="Variable">Nombre en <c>io_&lt;programa&gt;</c> (<c>e_SondaMax_CCS1_108_01</c>).</param>
/// <param name="Element">El equipo del que es, para enseñarlo (<c>SondaMax_CCS1_108_01</c>); en una analógica, también su variable de datos, de la que se
/// leen <c>RawMin</c>/<c>RawMax</c> (o <c>MinScale</c>/<c>MaxScale</c> si la entrada es REAL).</param>
/// <param name="At">El punto de la magnitud donde da señal (<see cref="SoftSignalKind.Above"/>, <see cref="SoftSignalKind.Below"/> y <see cref="SoftSignalKind.Near"/>).</param>
/// <param name="Inverted">La variable vale lo contrario (cableada a normalmente cerrado, o el programa la lee negada).</param>
public sealed record SoftSignal(string Variable, string Element, SoftSignalKind Kind = SoftSignalKind.Above, double At = 100, bool Inverted = false)
{
    /// <summary>Cuánto se puede apartar de su punto una señal <see cref="SoftSignalKind.Near"/> y seguir dando señal.</summary>
    public const double NearTolerance = 0.05;
}

/// <summary>
/// Una causa que mueve la magnitud: mientras se cumple <see cref="When"/>, sube (o baja, sin <see cref="Rises"/>) <see cref="PerSecond"/> por segundo.
/// El ritmo es el de por defecto: cada instancia lo puede cambiar sin volver a transferir el programa.
/// </summary>
/// <param name="Name">Lo que es, para enseñarlo y para guardar su ritmo (<c>Carga (CargaZ2)</c>, <c>Adelante</c>).</param>
/// <param name="When">Expresión ST booleana sobre el programa (<c>Topo_CargaZ2_CCS2_108_01.Status.State = 50</c>).</param>
public sealed record SoftRate(string Name, string When, double PerSecond, bool Rises = true);

/// <summary>
/// Un valor que simula la instalación (el nivel de un silo, la posición de un carro, la presión de un propulsor): va de <see cref="Min"/> a
/// <see cref="Max"/>, lo mueven sus <see cref="Rates"/> y da sus <see cref="Signals"/>.
/// </summary>
public sealed record SoftQuantity(string Name, string Unit, double Min, double Max, IReadOnlyList<SoftRate> Rates, IReadOnlyList<SoftSignal> Signals, double Initial = 0);

/// <summary>
/// Un proceso simulado de la instalación (docs/PENDIENTES.md 73): una o varias magnitudes. SoftNodes lo ejecuta sin saber qué es; <see cref="Kind"/>
/// solo dice de qué plantilla salió (<see cref="Silo"/>, <see cref="Carriage"/>…) para enseñarlo.
/// </summary>
/// <param name="Owner">La instancia de loop que lo gobierna (<c>Loop_Silo_CCS1_108_01</c>), para enseñar si está en marcha, en reposo o en alarma; null si
/// no hay ninguna.</param>
public sealed record SoftProcess(string Name, string Kind, IReadOnlyList<SoftQuantity> Quantities, string? Owner = null)
{
    public const string Silo = "Silo";
    public const string Carriage = "Carro";
}

/// <summary>Un detector de nivel de un depósito (paquetes de antes de los procesos: se leen como un <see cref="SoftProcess"/>).</summary>
public sealed record SoftSensor(string Variable, double AtPercent = 100, bool Inverted = false);

/// <summary>
/// Un depósito de los paquetes de antes de los procesos (<c>tanks.json</c>): la tolva de un destino que se llena mientras se cumple
/// <see cref="FillWhen"/>. Se lee como un <see cref="SoftProcess"/> (<see cref="ToProcess"/>).
/// </summary>
public sealed record SoftTank(string Name, string FillWhen, IReadOnlyList<SoftSensor> Sensors, string DrainWhen = "")
{
    public SoftProcess ToProcess()
    {
        List<SoftRate> rates = [new(SoftProcessDefaults.Fill, FillWhen, SoftProcessDefaults.FillPerSecond)];
        if (!string.IsNullOrWhiteSpace(DrainWhen)) rates.Add(new SoftRate(SoftProcessDefaults.Drain, DrainWhen, 0, Rises: false));
        var signals = Sensors.Select(s => new SoftSignal(s.Variable, s.Variable, SoftSignalKind.Above, s.AtPercent, s.Inverted)).ToList();
        return new SoftProcess(Name, SoftProcess.Silo, [new SoftQuantity(SoftProcessDefaults.Level, "%", 0, 100, rates, signals)]);
    }
}

/// <summary>Los nombres y ritmos de las plantillas, que también usan quienes las rellenan.</summary>
public static class SoftProcessDefaults
{
    public const string Level = "Nivel";
    public const string Position = "Posición";
    public const string Fill = "Llenado";
    public const string Drain = "Vaciado";
    public const string Forward = "Adelante";
    public const string Reverse = "Atrás";

    /// <summary>% por segundo: un silo vacío tarda 50 s en llenarse.</summary>
    public const double FillPerSecond = 2;

    /// <summary>% por segundo: un silo lleno tarda 100 s en vaciarse.</summary>
    public const double DrainPerSecond = 1;

    /// <summary>Lo que recorre un carro más allá de su primera y de su última posición, hasta sus finales de carrera (en posiciones).</summary>
    public const double CarriageOvertravel = 0.5;

    /// <summary>Lo que tarda un carro de una posición a la siguiente.</summary>
    public const double SecondsPerPosition = 6;
}
