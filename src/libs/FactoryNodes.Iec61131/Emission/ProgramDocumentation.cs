namespace FactoryNodes.Iec61131.Emission;

/// <summary>
/// Explicación del programa en palabras (quién es cada cosa, qué significa y cómo funciona), junto al
/// <see cref="ProgramModel"/>: la construye el mismo <c>ProgramBuilder</c> que genera el código, así que coincide siempre
/// con él. Los emisores de PLC no la usan; el de IA (<c>FactoryNodes.Emitters.AI</c>) la escribe para que una IA entienda
/// la instalación (docs/design/AI_KNOWLEDGE.md). Solo texto y nombres: no depende de los modelos del editor.
/// </summary>
/// <param name="Scope">«Proyecto» o «Pestaña «Hoja1»»: qué parte de la instalación cubre.</param>
/// <param name="Io">Todas las E/S físicas dibujadas (también selectores, resets y grupos de alarmas), para el cuadrista.</param>
/// <param name="Notes">Las observaciones de los equipos del sinóptico, para el cuadrista (PENDIENTES 97).</param>
/// <param name="Plcs">Los autómatas del proyecto con su ficha, para el cuadrista (PENDIENTES 166); vacío = uno solo sin dibujar.</param>
public sealed record ProgramDocumentation(
    string ProjectName,
    string Scope,
    IReadOnlyList<SheetDocumentation> Sheets,
    IReadOnlyList<ElementDocumentation> Elements,
    IReadOnlyList<LoopDocumentation> Loops,
    IReadOnlyList<IoSignalDocumentation>? Io = null,
    IReadOnlyList<EquipmentNoteDocumentation>? Notes = null,
    IReadOnlyList<PlcDocumentation>? Plcs = null);

/// <summary>Las observaciones que ha escrito el usuario en un equipo del sinóptico (texto libre), con su hoja.</summary>
public sealed record EquipmentNoteDocumentation(string Sheet, string Equipment, string Text);

/// <summary>
/// La ficha de un autómata para el cuadrista (autor, 2026-10-29, PENDIENTES 166: «cuando genere el esquema eléctrico lo puede anotar en la
/// hoja… sobre todo es útil la IP del autómata»): nombre, marca y modelo (texto libre), plataforma (<c>TIA</c>/<c>Sysmac</c>; vacío = no
/// dicha) e IP (vacío = no dicha). Las hojas que lo tienen por defecto, en su orden.
/// </summary>
public sealed record PlcDocumentation(string Name, string Model, string Platform, string Ip, IReadOnlyList<string> Sheets);

/// <summary>
/// Una E/S física para el «Resumen E/S» del cuadrista (autor, 2026-10-16): el nombre que tiene que respetar en su esquema,
/// su tipo de E/S (las de seguridad aparte de las normales) y dónde está dibujada.
/// </summary>
/// <param name="TypeCode">Código del tipo de E/S (DI, DO, Safety I, Safety O, AI, AO…).</param>
/// <param name="TypeName">Nombre del tipo («Entradas digitales (DI)»).</param>
/// <param name="TypeOrder">Orden del tipo en los listados: entradas, salidas, seguridad, analógicas y el resto.</param>
/// <param name="Description">Qué es la señal (su elemento del catálogo y, si lo tiene, su nombre propio en el equipo).</param>
/// <param name="Plc">El autómata al que va su equipo (PENDIENTES 166); vacío sin autómatas dibujados.</param>
public sealed record IoSignalDocumentation(
    string Variable, string TypeCode, string TypeName, int TypeOrder, string Sheet, string Equipment, string Description, bool Inverted,
    string Plc = "");

/// <summary>Una hoja del sinóptico y los equipos que tiene.</summary>
public sealed record SheetDocumentation(string Name, IReadOnlyList<string> Elements);

/// <summary>
/// Un equipo: su variable de datos, su tipo del catálogo (con su descripción), su bloque, la hoja y sus señales físicas.
/// </summary>
/// <param name="Value">El valor escrito en el dibujo (potencia, capacidad…), si lo tiene.</param>
/// <param name="Controls">Qué loops le dan órdenes y en qué tramo («Loop_Prensa01: marcha de la etapa 40 a la 120»).</param>
public sealed record ElementDocumentation(
    string Name,
    string Sheet,
    string TypeName,
    string TypeDescription,
    string Block,
    string Value,
    IReadOnlyList<SignalDocumentation> Signals,
    string Selector,
    string Reset,
    IReadOnlyList<string> Controls);

/// <summary>Una señal física del equipo: variable, qué es, si entra o sale y a qué miembro del bloque va.</summary>
public sealed record SignalDocumentation(string Variable, string Meaning, bool IsInput, string Member, bool Inverted);

/// <summary>Un loop explicado: etapas, transiciones en frases, variables, consignas, alarmas, órdenes y estados especiales.</summary>
public sealed record LoopDocumentation(
    string Name,
    string Description,
    string DataType,
    IReadOnlyList<string> Instances,
    IReadOnlyList<StageDocumentation> Stages,
    IReadOnlyList<TransitionDocumentation> Transitions,
    IReadOnlyList<LoopVariableDocumentation> Extern,
    IReadOnlyList<LoopVariableDocumentation> Setpoints,
    IReadOnlyList<LoopAlarmDocumentation> Alarms,
    IReadOnlyList<string> Orders,
    IReadOnlyList<string> SpecialStages);

/// <param name="Time">Tiempo de la etapa en palabras («tmpo_Limpieza, 30 s por defecto»), o vacío.</param>
public sealed record StageDocumentation(uint Number, string Title, string Description, string Time);

/// <param name="Condition">Las condiciones en una frase («cuando «PeticionPrensa» está activo y …», o «sin condiciones: …»).</param>
public sealed record TransitionDocumentation(uint From, string FromTitle, uint To, string ToTitle, int Priority, string Condition, string Description);

/// <param name="Source">De dónde sale en cada instancia (Extern) o su valor por defecto y unidad (consignas).</param>
public sealed record LoopVariableDocumentation(string Name, string Type, string Description, string Source);

public sealed record LoopAlarmDocumentation(string Name, int Bit, string Description, string Trigger, bool Siren);
