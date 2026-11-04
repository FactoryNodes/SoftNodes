namespace FactoryNodes.StEditing;

/// <summary>Lo que se puede crear desde el editor.</summary>
public enum StNewKind
{
    /// <summary>Un fichero <c>.st</c> nuevo, con su cabecera.</summary>
    File,

    /// <summary><c>TYPE … : STRUCT … END_STRUCT; END_TYPE</c>.</summary>
    Type,

    /// <summary><c>FUNCTION_BLOCK … END_FUNCTION_BLOCK</c>, sin secciones (se crean al añadir la primera fila).</summary>
    FunctionBlock,

    /// <summary>Un bloque de elemento con el núcleo común: <c>UDT_X_Control/_Extern/_Status</c>, <c>UDT_X</c> y <c>X_Block</c>.</summary>
    Element
}

/// <summary>
/// Plantillas de lo nuevo. Siguen <c>docs/MASTER_LIBRARY_CONVENTIONS.md</c> y la forma de los ficheros validados (Pilot,
/// Digital): un bloque de elemento recién creado pasa el validador y el analizador sin errores ni avisos (test).
/// Las líneas van sin salto final; <see cref="StSourceDocument"/> les pone el del fichero.
/// </summary>
public static class StTemplates
{
    /// <summary>Texto de un fichero nuevo: la cabecera <c>(* *)</c> y nada más.</summary>
    public static string File(string name) => string.Join("\n",
        "(* =====================================================================",
        $"   {name} - librería maestra FactoryNodes",
        "   IEC 61131-3 ST estándar. Convenciones: MASTER_LIBRARY_CONVENTIONS.md",
        "   Versión 0.1 - borrador pendiente de validar en TIA y Sysmac.",
        "   ===================================================================== *)",
        "");

    public static IReadOnlyList<string> Type(string name) =>
    [
        $"TYPE {name} :",
        "STRUCT",
        "    Nuevo1 : BOOL;  // Cambia el nombre, el tipo y el comentario en la tabla «Miembros»",
        "END_STRUCT;",
        "END_TYPE"
    ];

    public static IReadOnlyList<string> FunctionBlock(string name) =>
    [
        $"FUNCTION_BLOCK {name}",
        "",
        "",
        "END_FUNCTION_BLOCK"
    ];

    /// <summary>Nombres que crea <see cref="Element"/> para el tipo <paramref name="type"/> (para comprobar que no existen).</summary>
    public static IReadOnlyList<string> ElementNames(string type) =>
        [$"UDT_{type}_Control", $"UDT_{type}_Extern", $"UDT_{type}_Status", $"UDT_{type}", $"{type}_Block"];

    /// <summary>
    /// Un bloque de elemento con el núcleo común y nada más: modos con <c>ModeManager</c>, alarmas con
    /// <c>AlarmManager</c> (ninguna todavía), salida, código HMI y salidas de comodidad. Lo propio del equipo se añade después.
    /// </summary>
    public static IReadOnlyList<string> Element(string type) =>
    [
        "(* ------------------------------ TIPOS ------------------------------ *)",
        "",
        $"TYPE UDT_{type}_Control :",
        "STRUCT",
        "    // --- Núcleo común ---",
        "    Auto_ON         : BOOL;  // En automático: obedece a Run_Conditions",
        "    Man_ON          : BOOL;  // Manual: forzado a marcha",
        "    Man_OFF         : BOOL;  // Manual: forzado a paro",
        "    Run_Conditions  : BOOL;  // Orden de marcha automática (OR de demandas de Loop/Topology)",
        "    Lock_Conditions : BOOL;  // Bloqueo de proceso",
        "END_STRUCT;",
        "END_TYPE",
        "",
        "",
        $"TYPE UDT_{type}_Extern :",
        "STRUCT",
        "    // --- Núcleo común (asignados por zona) ---",
        "    Sel_Auto       : BOOL; /// IO DI DEFAULT FALSE  // Selector del cuadro en posición Automático",
        "    Sel_Man        : BOOL; /// IO DI DEFAULT TRUE   // Selector del cuadro en posición Manual",
        "    Pul_ResetAlarm : BOOL; /// IO DI DEFAULT FALSE  // Pulsador de aceptar/resetear alarmas",
        "END_STRUCT;",
        "END_TYPE",
        "",
        "",
        $"TYPE UDT_{type}_Status :",
        "STRUCT",
        "    // --- Núcleo común ---",
        "    Active_Q : BOOL; /// IO DO  // Orden activa",
        "    Run_OK   : BOOL;  // Marcha confirmada",
        "    Locked   : BOOL;  // Bloqueado por Lock_Conditions",
        "    ALM_SUM  : BOOL;  // Alguna alarma memorizada",
        "    ALM_Word : WORD;  // Alarmas memorizadas, un bit por alarma",
        "    Siren    : BOOL;  // Hay alarmas sin aceptar",
        "    HMI      : UINT;  // Estado para el HMI: 0 parado, 1 activo, 3 alarma, 4 bloqueado",
        "END_STRUCT;",
        "END_TYPE",
        "",
        "",
        $"TYPE UDT_{type} :",
        "STRUCT",
        $"    Control : UDT_{type}_Control;  // Cómo queremos que trabaje (órdenes y configuración)",
        $"    Extern  : UDT_{type}_Extern;   // Señales físicas del cuadro",
        $"    Status  : UDT_{type}_Status;   // Cómo está el elemento",
        "END_STRUCT;",
        "END_TYPE",
        "",
        "",
        "(* --------------------------- BLOQUE -------------------------------- *)",
        "",
        $"FUNCTION_BLOCK {type}_Block",
        "",
        "VAR_INPUT",
        "    Pul_1Sec : BOOL;  // Reloj de 1 s del PLC (entrada estándar)",
        "    Debug    : BOOL;  // Simulación en PC: no genera alarmas",
        "END_VAR",
        "",
        "VAR_IN_OUT",
        $"    Data : UDT_{type};  // Datos del elemento (estructura retentiva)",
        "END_VAR",
        "",
        "VAR_OUTPUT",
        "    Active_Q : BOOL;  // Copia de Status.Active_Q",
        "    Alarm    : BOOL;  // Copia de Status.ALM_SUM",
        "    Siren    : BOOL;  // Copia de Status.Siren",
        "    ALM_Word : WORD;  // Copia de Status.ALM_Word",
        "END_VAR",
        "",
        "VAR CONSTANT",
        "    HMI_OFF    : UINT := 0;",
        "    HMI_ON     : UINT := 1;",
        "    HMI_ALARM  : UINT := 3;",
        "    HMI_LOCKED : UINT := 4;",
        "END_VAR",
        "",
        "VAR",
        "    FirstScan : BOOL := TRUE;  // No retentiva: vuelve a TRUE en cada arranque",
        "    modes     : ModeManager;",
        "    alarms    : AlarmManager;",
        "    cond      : WORD;          // Condiciones de alarma de este ciclo, un bit por alarma",
        "END_VAR",
        "",
        "",
        "/// REGION Selector and mode",
        "// Reglas comunes a todos los elementos (ModeManager).",
        "modes(Sel_Auto    := Data.Extern.Sel_Auto,",
        "      Sel_Man     := Data.Extern.Sel_Man,",
        "      LockActive  := Data.Control.Lock_Conditions,",
        "      AlarmActive := Data.Status.ALM_SUM,",
        "      Auto_ON     := Data.Control.Auto_ON,",
        "      Man_ON      := Data.Control.Man_ON,",
        "      Man_OFF     := Data.Control.Man_OFF);",
        "/// END_REGION",
        "",
        "",
        "/// REGION Output",
        "Data.Status.Active_Q := ((Data.Control.Auto_ON AND Data.Control.Run_Conditions) OR Data.Control.Man_ON)",
        "                        AND NOT Data.Control.Lock_Conditions AND NOT Data.Status.ALM_SUM;",
        "Data.Status.Run_OK   := Data.Status.Active_Q;",
        "Data.Status.Locked   := Data.Control.Lock_Conditions;",
        "/// END_REGION",
        "",
        "",
        "/// REGION Alarms",
        "// Un bit por alarma: declara su constante (ALM_BIT_X : WORD := WORD#16#0001) y añade aquí",
        "// «IF <causa> THEN cond := cond OR ALM_BIT_X; END_IF;». Documenta cada bit en la cabecera.",
        "cond := WORD#16#0000;",
        "",
        "// En Debug no se generan alarmas.",
        "IF Debug THEN",
        "    cond := WORD#16#0000;",
        "END_IF;",
        "",
        "alarms(Conditions    := cond,",
        "       AutoResetMask := WORD#16#0000,",
        "       Reset         := Data.Extern.Pul_ResetAlarm,",
        "       FirstScan     := FirstScan,",
        "       Latched       := Data.Status.ALM_Word,",
        "       Siren         := Data.Status.Siren);",
        "",
        "Data.Status.ALM_SUM := Data.Status.ALM_Word <> WORD#16#0000;",
        "/// END_REGION",
        "",
        "",
        "/// REGION HMI state code and outputs",
        "IF Data.Status.ALM_SUM THEN",
        "    Data.Status.HMI := HMI_ALARM;",
        "ELSIF Data.Status.Locked THEN",
        "    Data.Status.HMI := HMI_LOCKED;",
        "ELSIF Data.Status.Active_Q THEN",
        "    Data.Status.HMI := HMI_ON;",
        "ELSE",
        "    Data.Status.HMI := HMI_OFF;",
        "END_IF;",
        "",
        "Active_Q := Data.Status.Active_Q;",
        "Alarm    := Data.Status.ALM_SUM;",
        "Siren    := Data.Status.Siren;",
        "ALM_Word := Data.Status.ALM_Word;",
        "",
        "FirstScan := FALSE;",
        "/// END_REGION",
        "",
        "END_FUNCTION_BLOCK"
    ];

    /// <summary>
    /// <c>order.txt</c> con <paramref name="name"/> añadido al final (detrás de todo: sus dependencias ya están), con el
    /// salto de línea del fichero. Igual si ya está.
    /// </summary>
    public static string AddToOrder(string orderText, string name)
    {
        var listed = orderText.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim())
            .Any(l => l.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (listed) return orderText;
        var newline = orderText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = orderText.Length == 0 || orderText.EndsWith('\n') ? orderText : orderText + newline;
        return text + name + newline;
    }
}
