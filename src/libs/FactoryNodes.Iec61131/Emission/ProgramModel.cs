namespace FactoryNodes.Iec61131.Emission;

/// <summary>
/// Descripción neutral de la salida "Programa" (docs/design/SYNOPTIC_BINDING.md, 4b): qué variables hay, en qué sección
/// va cada elemento y cómo se conecta. La construye el enlace sinóptico → bloques; cada emisor la escribe en su formato
/// (TIA: fuente SCL con DB y FB por sección; Sysmac: variables en TSV para pegar y una sección ST por FB).
/// </summary>
/// <param name="ProjectName">Nombre del proyecto, ya válido como identificador.</param>
/// <param name="DataName">Datos del proyecto: <c>hw_&lt;Proyecto&gt;</c> (DB remanente en TIA; variables globales retentivas en Sysmac).</param>
/// <param name="IoName">Señales físicas: <c>io_&lt;Proyecto&gt;</c> (DB no remanente en TIA; variables globales en Sysmac).</param>
public sealed record ProgramModel(
    string ProjectName,
    string DataName,
    string IoName,
    IReadOnlyList<ProgramVariable> IoVariables,
    IReadOnlyList<ProgramSection> Sections,
    ProgramSection OutputSection,
    IReadOnlyList<ProgramOutput> Outputs)
{
    public IEnumerable<ProgramElement> Elements => Sections.SelectMany(s => s.Elements);

    /// <summary>
    /// Todas las variables de datos (retentivas, en <c>hw_</c>): la de cada elemento y, detrás, sus compañeras
    /// (<see cref="ProgramElement.Companions"/>).
    /// </summary>
    public IEnumerable<ProgramDataVariable> DataVariables => Elements.SelectMany(e => e.DataVariables).Concat(SharedVariables);

    /// <summary>
    /// Otras variables de datos (retentivas, en <c>hw_</c>) que no son de ningún bloque: las estructuras que viajan a otro autómata
    /// (<c>EIP_OUT_B</c>, <c>EIP_IN_B</c>; PENDIENTES 167). Cada una dice de qué elemento depende (<see cref="ProgramDataVariable.Owner"/>, el
    /// estado de su enlace) y cuenta como compañera suya (sin valor inicial); se publican en red como los datos de los equipos.
    /// </summary>
    public IReadOnlyList<ProgramDataVariable> SharedVariables { get; init; } = [];

    /// <summary>
    /// Los enlaces con otros autómatas (PENDIENTES 167): lo que la tarea de comunicación de cada emisor tiene que hacer en la sección
    /// <see cref="ProgramLink.Section"/>, delante de sus elementos. Vacía sin transferencia de datos.
    /// </summary>
    public IReadOnlyList<ProgramLink> Links { get; init; } = [];

    /// <summary>
    /// Publicar en red las señales físicas y <c>DebugSim</c> (se pregunta al exportar, autor 2026-10-02: para forzar el
    /// selector y las E/S desde el simulador o un HMI). Solo cambia algo donde publicar es una opción de cada variable
    /// (Sysmac: «Solo publicar»); en TIA el DB de señales ya se ve por OPC UA.
    /// </summary>
    public bool PublishIo { get; init; }

    /// <summary>La explicación del programa en palabras (la usa el emisor de IA); null = sin ella.</summary>
    public ProgramDocumentation? Documentation { get; init; }

    /// <summary>
    /// Constantes globales del programa: las de las etapas de los loops (<c>DosifSilo01_0010_EsperaInicio</c> = 10). Las
    /// expresiones las nombran con <see cref="ProgramValue.NamedConstant"/>; donde la plataforma tiene
    /// constantes globales (Sysmac) se declaran y se usan por su nombre; si no (TIA, de momento), va el valor con el
    /// nombre en un comentario.
    /// </summary>
    public IReadOnlyList<ProgramConstant> Constants { get; init; } = [];

    /// <summary>
    /// Las entradas que da SoftNodes en el banco de pruebas (PENDIENTES 110 y 136), en el orden que da su sitio en <c>Sim</c>
    /// (<see cref="SimPlantLayout.Of"/>). Vacía sin la llave del banco (y en los programas de antes del 2026-10-28, que las tenían sueltas).
    /// </summary>
    public IReadOnlyList<SimPlantInput> SimInputs { get; init; } = [];

    /// <summary>
    /// Las variables que este autómata comparte por Modbus TCP como servidor (PENDIENTES 199): cada una en su zona y su dirección. Null = sin
    /// Modbus. Hoy solo lo sirve SoftNodes; los emisores de TIA y Sysmac aún no lo escriben.
    /// </summary>
    public ProgramModbus? Modbus { get; init; }
}

/// <summary>Las cuatro zonas de Modbus: de bits (bobinas, entradas discretas) y de registros de 16 bits (holding, input).</summary>
public enum ModbusArea
{
    /// <summary>Bobinas (funciones 01, 05 y 15): BOOL que los demás leen y escriben.</summary>
    Coil,

    /// <summary>Entradas discretas (función 02): BOOL que los demás solo leen.</summary>
    DiscreteInput,

    /// <summary>Holding registers (03, 06 y 16): valores que los demás leen y escriben.</summary>
    HoldingRegister,

    /// <summary>Input registers (función 04): valores que los demás solo leen.</summary>
    InputRegister
}

/// <summary>
/// El mapa Modbus TCP de un autómata (PENDIENTES 199): qué variable va en cada dirección. Las direcciones son las de la trama (desde 0).
/// </summary>
/// <param name="LowWordFirst">32 y 64 bits: la palabra baja primero (lo piden algunos SCADA); por defecto, la alta primero.</param>
public sealed record ProgramModbus(IReadOnlyList<ProgramModbusEntry> Entries, bool LowWordFirst = false);

/// <summary>
/// Una variable en el mapa Modbus: su zona, su primera dirección (desde 0), su camino en el programa (<c>MB_Data.Consigna</c>,
/// <c>hw_P.M101.Status.Run_OK</c>) y su tipo IEC. En registros: BOOL, 8 y 16 bits ocupan uno; DINT, UDINT, DWORD, REAL y TIME, dos; LREAL y
/// los de 64 bits, cuatro. En las zonas de bits, solo BOOL. <paramref name="Negated"/>: un BOOL que se sirve al revés (el estado «sin alarma» de
/// un equipo es <c>NOT ALM_SUM</c>).
/// </summary>
public sealed record ProgramModbusEntry(ModbusArea Area, int Address, string Variable, string DataType, string Comment = "", bool Negated = false)
{
    /// <summary>Cuántas direcciones ocupa en su zona (0 si el tipo no cabe en ella).</summary>
    public int Size => ModbusSizes.Of(Area, DataType);
}

/// <summary>Cuántas direcciones ocupa cada tipo IEC en cada zona de Modbus.</summary>
public static class ModbusSizes
{
    public static bool IsBitArea(ModbusArea area) => area is ModbusArea.Coil or ModbusArea.DiscreteInput;

    /// <summary>Los demás pueden escribir en esta zona.</summary>
    public static bool IsWritable(ModbusArea area) => area is ModbusArea.Coil or ModbusArea.HoldingRegister;

    public static int Of(ModbusArea area, string dataType)
    {
        var type = dataType.Trim().ToUpperInvariant();
        if (IsBitArea(area)) return type == "BOOL" ? 1 : 0;
        return type switch
        {
            "BOOL" or "SINT" or "USINT" or "BYTE" or "INT" or "UINT" or "WORD" => 1,
            "DINT" or "UDINT" or "DWORD" or "REAL" or "TIME" => 2,
            "LINT" or "ULINT" or "LWORD" or "LREAL" => 4,
            _ => 0
        };
    }
}

/// <summary>Una constante global del programa: nombre, tipo IEC, valor (literal ST) y comentario.</summary>
public sealed record ProgramConstant(string Name, string DataType, string Value, string Comment);

/// <summary>
/// Un enlace de este autómata con otro (PENDIENTES 167): lo que la tarea de comunicación tiene que hacer, en forma neutra; cada emisor lo
/// escribe con la instrucción de su PLC (Sysmac: <c>CIPUCMMRead</c>), delante de los elementos de la sección <see cref="Section"/>. El bloque
/// <c>EipLink</c> del elemento <see cref="Status"/> hace el resto (latido, plazo, alarma) y lee el resultado en las variables
/// <see cref="Done"/>, <see cref="Error"/> y <see cref="ErrorId"/> de la sección, que el emisor declara y escribe.
/// </summary>
/// <param name="Remote">El nombre del autómata remoto (el sufijo de las variables).</param>
/// <param name="RemoteIp">Su IP (vacía si no está en su ficha: la tarea no se puede generar).</param>
/// <param name="RemotePlatform">Su plataforma (<c>Sysmac</c>, <c>TIA</c>…).</param>
/// <param name="Status">La variable de datos del enlace (<c>EIP_Status_B : UDT_EipLink</c>), un elemento de la sección.</param>
/// <param name="In">Lo que llega del remoto (<c>EIP_IN_B</c>): la tarea lo escribe entero con cada lectura.</param>
/// <param name="Out">Lo que este autómata manda (<c>EIP_OUT_B</c>): el remoto lo lee por su nombre.</param>
/// <param name="RemoteOut">El nombre que tiene en el remoto lo que leemos (<c>EIP_OUT_A</c>, siendo A este autómata).</param>
/// <param name="InType">El tipo de <paramref name="In"/> (idéntico en los dos programas).</param>
/// <param name="Pulls">Este autómata lee al remoto (los dos pueden preguntar: cada uno tira de lo suyo).</param>
/// <param name="PeriodMs">Cada cuánto lee, en ms.</param>
/// <param name="Section">La sección (<c>fnc005_DataTransfer</c>) donde va la tarea.</param>
public sealed record ProgramLink(string Remote, string RemoteIp, string RemotePlatform, string Status, string In, string Out, string RemoteOut,
    string InType, bool Pulls, int PeriodMs, string Section)
{
    public string Done => $"Link_{Remote}_Done";
    public string Busy => $"Link_{Remote}_Busy";
    public string Error => $"Link_{Remote}_Error";
    public string ErrorId => $"Link_{Remote}_ErrorID";

    /// <summary>La instancia de la instrucción de lectura en el PLC (<c>Link_B_Read</c>); su tipo lo pone cada emisor.</summary>
    public string Reader => $"Link_{Remote}_Read";

    /// <summary>Las variables que declara la sección por este enlace, con su tipo IEC (las escribe la instrucción de lectura).</summary>
    public IEnumerable<(string Name, string DataType)> Variables => [(Done, "BOOL"), (Busy, "BOOL"), (Error, "BOOL"), (ErrorId, "WORD")];

    /// <summary>
    /// El remoto es un autómata externo (PENDIENTES 180): no tiene nuestro programa, así que no se le lee <see cref="RemoteOut"/> ni lee él a
    /// nadie. La tarea lee sus etiquetas (<see cref="Reads"/>, a los campos de <see cref="In"/>) y **escribe** en ellas los campos de
    /// <see cref="Out"/> (<see cref="Writes"/>), cada período. La cabecera de <see cref="In"/> la pone la tarea: <c>Heartbeat</c> es la copia de
    /// <see cref="HeartbeatField"/> o, sin él, sube con cada ronda buena; <c>Layout</c> se queda a 0 (no se compara).
    /// </summary>
    public bool External { get; init; }

    /// <summary>Con un externo: cada etiqueta que se le lee y el campo de <see cref="In"/> donde va.</summary>
    public IReadOnlyList<LinkTag> Reads { get; init; } = [];

    /// <summary>Con un externo: cada campo de <see cref="Out"/> y la etiqueta del externo donde se escribe.</summary>
    public IReadOnlyList<LinkTag> Writes { get; init; } = [];

    /// <summary>Con un externo: el campo de <see cref="In"/> que es su latido (null = cada ronda buena cuenta como latido).</summary>
    public string? HeartbeatField { get; init; }
}

/// <summary>Una variable de un autómata externo en su enlace (PENDIENTES 180): nuestro campo, su etiqueta tal cual y el tipo IEC.</summary>
public sealed record LinkTag(string Field, string Tag, string DataType);

public enum ProgramVariableKind
{
    PhysicalInput,
    PhysicalOutput,

    /// <summary>Otra variable global (p. ej. una demanda simulada en la prueba).</summary>
    Other
}

/// <summary>
/// Una variable de las señales físicas (o auxiliar), con su comentario y su tipo IEC: BOOL las digitales; las analógicas,
/// el tipo del miembro del bloque al que van (<c>INT</c> el bruto de la tarjeta, <c>REAL</c> un valor ya escalado).
/// </summary>
/// <param name="Published">
/// Se publica en red siempre, se pida o no al exportar (<see cref="ProgramModel.PublishIo"/>): las de la planta simulada del banco de pruebas
/// (<c>SimPlant</c>, <c>Sim_</c>, <c>SimUse_</c>; PENDIENTES 110), que escribe SoftNodes y lee el HMI. Solo cambia algo en Sysmac.
/// </param>
/// <param name="Cabinet">
/// En el <c>SimUse_</c> de una entrada del <b>cuadro</b> (selector, pulsador…; la llave nunca): SoftNodes solo la da en el modo «Oficina» de la
/// planta simulada (autor, 2026-10-27: «dos modos, uno "oficina" y otro "con cuadro"»); con cuadro, la real.
/// </param>
public sealed record ProgramVariable(string Name, ProgramVariableKind Kind, string Comment, string DataType = "BOOL", bool Published = false,
    bool Cabinet = false);

/// <summary>
/// Un FB (TIA) o programa (Sysmac) por sección: <c>fnc</c> + número + nombre (<c>fnc050_Motors</c>; autor, 2026-10-27, PENDIENTES 137: «fnc…
/// un autonumérico de 3 cifras + el nombre de la sección», «de 10 en 10»). Cada sección generada tiene su número fijo, de 10 en 10 en el orden
/// del ciclo, esté o no en el programa; una del usuario, el de la que tiene delante más 1…9: meter una no cambia el nombre de las demás. Mismo
/// nombre en todos los PLC.
/// </summary>
public sealed record ProgramSection(string Number, string Name, IReadOnlyList<ProgramElement> Elements)
{
    /// <summary>Lo que llevan delante todas las secciones (FactoryNodes Code).</summary>
    public const string Prefix = "fnc";

    public string FullName => SectionName(Number, Name);

    /// <summary>El nombre en el PLC: <c>fnc060_Motors</c>.</summary>
    public static string SectionName(string number, string name) => $"{Prefix}{number}_{name}";

    /// <summary>El número fijo de la generada en el puesto <paramref name="position"/> de las posibles (0 = la primera): 010, 020…</summary>
    public static string NumberAt(int position) => ((position + 1) * 10).ToString("000", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// La <paramref name="nth"/>-ésima (1…9) de las del usuario que van detrás de la sección <paramref name="anchor"/> (<c>050</c> → <c>051</c>);
    /// <paramref name="anchor"/> null, delante de todas (<c>001</c>…). Más de nueve en el mismo hueco repiten el 9 (el nombre sigue siendo único).
    /// </summary>
    public static string After(string? anchor, int nth)
    {
        var start = anchor is null ? 0 : int.Parse(anchor, System.Globalization.CultureInfo.InvariantCulture);
        return (start + Math.Clamp(nth, 1, 9)).ToString("000", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Sección de código libre del usuario (módulo IEC, PENDIENTES 80): sin elementos, con su código ST y sus variables. Null =
    /// sección generada. Va en la lista en el orden en que se ejecuta.
    /// </summary>
    public ProgramUserCode? User { get; init; }

    /// <summary>
    /// Código del usuario al principio de una sección generada (PENDIENTES 88): va antes del primer elemento, en la misma sección. Ve las
    /// instancias de la sección (<see cref="ProgramElement.Instance"/>), las globales y sus propias variables (estáticas de la sección).
    /// </summary>
    public ProgramUserCode? Prologue { get; init; }

    /// <summary>Código del usuario al final de una sección generada, después del último elemento (o de las salidas físicas).</summary>
    public ProgramUserCode? Epilogue { get; init; }

    /// <summary>Las variables propias del código del usuario de esta sección (principio y final, en ese orden).</summary>
    public IEnumerable<ProgramLocal> InsertVariables =>
        (Prologue?.Variables ?? []).Concat(Epilogue?.Variables ?? []);
}

/// <summary>
/// El código de una sección de usuario: ST estándar de la librería maestra (sin <c>#</c> ni comillas), que nombra por su
/// nombre las globales del programa (datos de los elementos, señales, <c>DebugSim</c>, constantes) y sus propias variables.
/// Cada emisor lo traduce a su plataforma.
/// </summary>
/// <param name="FunctionBlock">
/// El nombre del bloque con el que lo ejecuta SoftNodes (<c>FB_User_Inicio</c>): un FB de la librería con las variables de la
/// sección y este código (<c>ProgramBuildResult.UserSources</c>). Los PLC no lo usan.
/// </param>
public sealed record ProgramUserCode(string Code, IReadOnlyList<ProgramLocal> Variables, string FunctionBlock, string Description = "");

/// <summary>
/// Variable propia de una sección de usuario: no retentiva, conserva su valor de un ciclo al siguiente (TIA: estática del FB de la
/// sección; Sysmac: variable del programa). <see cref="InitialValue"/> es un literal ST neutro o null.
/// </summary>
public sealed record ProgramLocal(string Name, string DataType, string? InitialValue, string Comment);

/// <summary>
/// Un elemento: su variable de datos (<see cref="Name"/> : <c>UDT_&lt;BlockType&gt;</c>), su instancia
/// <c>&lt;Name&gt;_FB</c> y el código de conexión (bloqueo, orden, mapeo de señales) antes de llamarla.
/// </summary>
public sealed record ProgramElement(
    string Name,
    string Description,
    string BlockType,
    ProgramAssignment Lock,
    ProgramAssignment Run,
    IReadOnlyList<ProgramAssignment> Mapping)
{
    public string DataType => $"UDT_{BlockType}";

    /// <summary>
    /// Valores iniciales propios de este equipo (docs/design/BLOCK_PARAMETERS.md: los del catálogo y la instancia; los de
    /// la librería y la empresa ya van en el tipo). Cada emisor los pone en la declaración de la variable de datos.
    /// </summary>
    public IReadOnlyList<ProgramInitialValue> InitialValues { get; init; } = [];

    /// <summary><c>&lt;BlockType&gt;_Block</c> para un equipo; un loop lo cambia (<c>FB_Loop_&lt;Nombre&gt;</c>).</summary>
    public string FunctionBlock { get; init; } = $"{BlockType}_Block";
    public string Instance => $"{Name}_FB";

    /// <summary>
    /// Otras variables de datos del elemento, cada una global y pasada a su bloque por su parámetro (<c>VAR_IN_OUT</c>).
    /// Para datos que no caben en una sola variable publicable: el OPC UA de Omron no publica estructuras de más de 3
    /// niveles ni variables grandes (W588 §1-2-2), así que una topology va en <c>Topo_Planta</c>, <c>Topo_Planta_Rt</c>…
    /// </summary>
    public IReadOnlyList<ProgramCompanion> Companions { get; init; } = [];

    /// <summary>Su variable de datos y sus compañeras, en ese orden.</summary>
    public IEnumerable<ProgramDataVariable> DataVariables =>
        Companions.Select(c => new ProgramDataVariable(c.Name, c.DataType, c.Description, this))
            .Prepend(new ProgramDataVariable(Name, DataType, Description, this));
}

/// <summary>Una variable de datos compañera de un elemento: <c>&lt;Parameter&gt; := &lt;Name&gt;</c> en la llamada a su bloque.</summary>
public sealed record ProgramCompanion(string Parameter, string Name, string DataType, string Description);

/// <summary>Una variable de datos del programa (la de un elemento o una compañera), con el elemento al que pertenece.</summary>
public sealed record ProgramDataVariable(string Name, string DataType, string Description, ProgramElement Owner)
{
    public bool IsCompanion => !Name.Equals(Owner.Name, StringComparison.OrdinalIgnoreCase);
}

public enum ProgramValueKind
{
    /// <summary>Constante tal cual (<c>TRUE</c>, <c>FALSE</c>).</summary>
    Constant,

    /// <summary>Variable de señales físicas (o auxiliar), por nombre.</summary>
    IoVariable,

    /// <summary>Miembro del propio elemento, relativo a su estructura (<c>Status.KM_VFD_ON</c>).</summary>
    OwnMember,

    /// <summary>
    /// Expresión ST que lee datos de otros elementos (<see cref="ProgramValue.References"/>), escritos en el texto como
    /// <c>{Nombre}</c>: cada emisor los cambia por su forma (TIA <c>"hw_P".Nombre</c>, Sysmac <c>Nombre</c>, que además
    /// declara como variable externa del programa). P. ej. las órdenes de los loops a un equipo.
    /// </summary>
    Expression
}

/// <summary>Valor de una asignación, con NOT si la señal se invierte.</summary>
public sealed record ProgramValue(ProgramValueKind Kind, string Text, bool Not = false)
{
    /// <summary>Elementos (datos en <c>hw_</c>) que lee una <see cref="ProgramValueKind.Expression"/>.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    public static ProgramValue Constant(string text) => new(ProgramValueKind.Constant, text);
    public static ProgramValue Io(string name, bool not = false) => new(ProgramValueKind.IoVariable, name, not);
    public static ProgramValue Own(string path) => new(ProgramValueKind.OwnMember, path);

    /// <param name="template">ST con <c>{Nombre}</c> donde va cada elemento de <paramref name="references"/>.</param>
    public static ProgramValue Expression(string template, IEnumerable<string> references) =>
        new(ProgramValueKind.Expression, template) { References = references.Distinct(StringComparer.OrdinalIgnoreCase).ToList() };

    /// <summary>
    /// El texto con cada <c>{Nombre}</c> cambiado por <paramref name="data"/>(Nombre) y cada constante
    /// (<see cref="NamedConstant"/>) por <paramref name="constant"/>(nombre, valor); sin él, el valor.
    /// </summary>
    /// <param name="clock">El reloj de 1 s de la plataforma (<see cref="Clock"/>): TIA <c>"Clock_1Hz"</c>, Sysmac <c>Get1sClk()</c>; sin él, <c>Clock_1s</c>.</param>
    public string Resolve(Func<string, string> data, Func<string, string, string>? constant = null, string? clock = null)
    {
        var text = References.Aggregate(Text, (t, name) => t.Replace("{" + name + "}", data(name), StringComparison.Ordinal));
        text = text.Replace(Clock, clock ?? "Clock_1s", StringComparison.Ordinal);
        return ConstantPattern.Replace(text, m => constant?.Invoke(m.Groups["name"].Value, m.Groups["value"].Value) ?? m.Groups["value"].Value);
    }

    /// <summary>
    /// El reloj de 1 s del PLC (la misma onda que <c>Pul_1Sec</c> de los bloques) dentro del texto de una
    /// <see cref="ProgramValueKind.Expression"/>: lo cambia cada emisor por el suyo. Para el parpadeo del piloto de alarma (PENDIENTES 117).
    /// </summary>
    public const string Clock = "{#Clock_1s}";

    /// <summary>Las constantes globales que nombra el texto (<see cref="ProgramModel.Constants"/>), en orden.</summary>
    public IEnumerable<string> ConstantNames => ConstantPattern.Matches(Text).Select(m => m.Groups["name"].Value).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Una constante global dentro del texto de una <see cref="ProgramValueKind.Expression"/>: <c>{=Nombre|valor}</c>. Lleva
    /// el valor para la plataforma que no tiene constantes globales (TIA, de momento).
    /// </summary>
    public static string NamedConstant(string name, string value) => $"{{={name}|{value}}}";

    private static readonly System.Text.RegularExpressions.Regex ConstantPattern = new(@"\{=(?<name>[A-Za-z_][A-Za-z0-9_]*)\|(?<value>[^}]*)\}");
}

/// <summary>
/// <c>&lt;elemento&gt;.&lt;Target&gt; := &lt;Value&gt;;   // Comment</c>. <see cref="Target"/> es relativo a la estructura del
/// elemento, o a la de su compañera <see cref="Variable"/> si se da.
/// </summary>
public sealed record ProgramAssignment(string Target, ProgramValue Value, string? Comment)
{
    /// <summary>Compañera del elemento a la que se escribe (<c>Topo_Planta_Rt</c>); null = su propia variable de datos.</summary>
    public string? Variable { get; init; }

    /// <summary>
    /// La línea va comentada (<c>// elemento.Target := Value;   // Comment</c>): la escribe el usuario a mano (órdenes por
    /// programa). El programa no la ejecuta.
    /// </summary>
    public bool Commented { get; init; }

    /// <summary>
    /// Solo se escribe mientras se cumple (<c>IF When THEN elemento.Target := Value; END_IF;</c>): una orden de un loop de escribir un valor en
    /// su tramo de etapas (2026-10-21). Fuera, el miembro se queda con lo último que tuvo. Null = siempre.
    /// </summary>
    public ProgramValue? When { get; init; }

    /// <summary>Los elementos que lee la línea: los del valor y los de la condición.</summary>
    public IEnumerable<string> References => When is null ? Value.References : Value.References.Concat(When.References);
}

/// <summary>
/// Valor inicial de un miembro de la variable de datos de un equipo: <see cref="Path"/> relativo a su estructura
/// (<c>SGDelay</c>, <c>Control.Enable_Smooth</c>) y <see cref="Literal"/> como literal ST neutro (<c>100</c>, <c>12.5</c>,
/// <c>TRUE</c>).
/// </summary>
public sealed record ProgramInitialValue(string Path, string Literal);

/// <summary>Salida física: <c>&lt;Physical&gt; := [NOT] &lt;Element&gt;.&lt;Member&gt;;</c> (sección final de salidas).</summary>
public sealed record ProgramOutput(string Physical, string Element, string Member, bool Not, string Comment);

/// <summary>
/// Una entrada que puede dar SoftNodes en el banco de pruebas (PENDIENTES 110 y 136): su señal, su tipo y si es del cuadro (solo en el modo
/// «Oficina») o de campo.
/// </summary>
public sealed record SimPlantInput(string Name, string DataType, bool Cabinet);

/// <summary>
/// Los nombres de la planta simulada del banco de pruebas (PENDIENTES 110): los genera <c>ProgramBuilder</c> (con la llave) y los escribe SoftNodes
/// cuando hace de planta de un PLC externo. Globales no retentivas, con las señales. Desde el 2026-10-28 (PENDIENTES 136; autor: «en una estructura
/// me parece genial. Dos variables si quieres Sim y SimUse. Dentro todas sus definiciones», «el código (carpeta de variables) está muy sucio con
/// tanta simXXXX» → «se pueden evitar esos booleanos y agruparlos en words», «con AND's»): una sola global <c>Sim</c> con arrays de palabras y de
/// valores (<see cref="SimPlantLayout"/>), en vez de dos globales por entrada (<c>Sim_&lt;señal&gt;</c>, <c>SimUse_&lt;señal&gt;</c>).
/// </summary>
public static class SimPlantNames
{
    /// <summary>La llave girada y sin <c>DebugSim</c>: el PLC usa las entradas de campo simuladas.</summary>
    public const string Variable = "SimPlant";

    /// <summary>La estructura con lo que da SoftNodes: qué entradas (<c>Sim.Used</c>) y sus valores (<c>Sim.Bits</c>, <c>Sim.Ints</c>…).</summary>
    public const string ValueVariable = "Sim";

    /// <summary>Su tipo, generado con el programa (<see cref="SimPlantLayout"/>).</summary>
    public const string ValueType = "UDT_Sim";

    /// <summary>Las entradas de campo que se pueden simular en un programa (sin las del cuadro).</summary>
    public static IReadOnlyList<string> FieldInputs(ProgramModel program) => Inputs(program, cabinet: false);

    /// <summary>
    /// Las entradas del cuadro que se pueden simular (selectores, pulsadores…), las del modo «Oficina». Un programa de antes del 2026-10-27 no
    /// tiene ninguna.
    /// </summary>
    public static IReadOnlyList<string> CabinetInputs(ProgramModel program) => Inputs(program, cabinet: true);

    private static IReadOnlyList<string> Inputs(ProgramModel program, bool cabinet)
    {
        var io = program.IoVariables.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return program.SimInputs.Where(i => i.Cabinet == cabinet).Select(i => i.Name).Where(io.Contains).ToList();
    }

    /// <summary>¿Es una de las variables de la planta simulada (<c>SimPlant</c>, <c>Sim</c>)?</summary>
    public static bool IsSimPlantVariable(string name) =>
        name.Equals(Variable, StringComparison.Ordinal) || name.Equals(ValueVariable, StringComparison.Ordinal);
}
