using FactoryNodes.Iec61131.Model;

namespace FactoryNodes.StEditing.Semantics;

/// <summary>Un parámetro de un bloque o función: nombre, tipo y sentido.</summary>
public sealed record StParameter(string Name, string TypeName, VarSectionKind Direction, string? Comment = null);

/// <summary>Un bloque de función estándar IEC con sus parámetros (los que admite la librería y algunos más, para el mensaje).</summary>
public sealed record StandardBlock(string Name, IReadOnlyList<StParameter> Parameters, string Description);

/// <summary>Bloques de función estándar IEC. La librería solo admite TON, TOF, R_TRIG, F_TRIG y RS (lo valida ML107).</summary>
public static class StandardBlocks
{
    private static StParameter In(string n, string t, string c) => new(n, t, VarSectionKind.Input, c);
    private static StParameter Out(string n, string t, string c) => new(n, t, VarSectionKind.Output, c);

    public static readonly IReadOnlyList<StandardBlock> All =
    [
        new("TON", [In("IN", "BOOL", "Arranca al ponerse a TRUE"), In("PT", "TIME", "Tiempo"), Out("Q", "BOOL", "TRUE pasado PT con IN activo"), Out("ET", "TIME", "Tiempo transcurrido")],
            "Retardo a la conexión"),
        new("TOF", [In("IN", "BOOL", "Al pasar a FALSE empieza a contar"), In("PT", "TIME", "Tiempo"), Out("Q", "BOOL", "Sigue TRUE durante PT tras caer IN"), Out("ET", "TIME", "Tiempo transcurrido")],
            "Retardo a la desconexión"),
        new("TP", [In("IN", "BOOL", "Flanco que dispara el pulso"), In("PT", "TIME", "Duración del pulso"), Out("Q", "BOOL", "Pulso"), Out("ET", "TIME", "Tiempo transcurrido")],
            "Pulso (no admitido en la librería)"),
        new("R_TRIG", [In("CLK", "BOOL", "Señal"), Out("Q", "BOOL", "TRUE un ciclo en el flanco de subida")], "Flanco de subida"),
        new("F_TRIG", [In("CLK", "BOOL", "Señal"), Out("Q", "BOOL", "TRUE un ciclo en el flanco de bajada")], "Flanco de bajada"),
        new("RS", [In("SET", "BOOL", "Activa"), In("RESET1", "BOOL", "Desactiva (manda sobre SET)"), Out("Q1", "BOOL", "Salida")], "Biestable con prioridad al reset"),
        new("SR", [In("SET1", "BOOL", "Activa (manda sobre RESET)"), In("RESET", "BOOL", "Desactiva"), Out("Q1", "BOOL", "Salida")], "Biestable con prioridad al set (no admitido)"),
        new("CTU", [In("CU", "BOOL", "Cuenta"), In("RESET", "BOOL", "Pone a 0"), In("PV", "INT", "Valor final"), Out("Q", "BOOL", "CV ≥ PV"), Out("CV", "INT", "Cuenta")],
            "Contador ascendente (no admitido)"),
        new("CTD", [In("CD", "BOOL", "Descuenta"), In("LOAD", "BOOL", "Carga PV"), In("PV", "INT", "Valor inicial"), Out("Q", "BOOL", "CV ≤ 0"), Out("CV", "INT", "Cuenta")],
            "Contador descendente (no admitido)")
    ];

    public static StandardBlock? Get(string name) => All.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Cómo se calcula el tipo que devuelve una función estándar genérica.</summary>
public enum GenericResult
{
    /// <summary>El del primer argumento (ABS, MIN, MAX, SQRT…).</summary>
    First,

    /// <summary>El del segundo (SEL: G, IN0, IN1; LIMIT: MN, IN, MX; MUX: K, IN0…).</summary>
    Second,

    /// <summary>Un entero (TRUNC).</summary>
    Integer,

    Bool
}

/// <summary>Una función estándar IEC genérica: sin tipos fijos en los parámetros.</summary>
public sealed record StandardFunction(string Name, GenericResult Result, int MinArguments, int MaxArguments, string Description);

/// <summary>Funciones estándar IEC que se pueden llamar desde el cuerpo, y las conversiones <c>X_TO_Y</c>.</summary>
public static class StandardFunctions
{
    public static readonly IReadOnlyList<StandardFunction> All =
    [
        new("ABS", GenericResult.First, 1, 1, "Valor absoluto"),
        new("SQRT", GenericResult.First, 1, 1, "Raíz cuadrada"),
        new("LN", GenericResult.First, 1, 1, "Logaritmo natural"),
        new("LOG", GenericResult.First, 1, 1, "Logaritmo en base 10"),
        new("EXP", GenericResult.First, 1, 1, "e elevado a"),
        new("EXPT", GenericResult.First, 2, 2, "Potencia"),
        new("SIN", GenericResult.First, 1, 1, "Seno"),
        new("COS", GenericResult.First, 1, 1, "Coseno"),
        new("TAN", GenericResult.First, 1, 1, "Tangente"),
        new("ASIN", GenericResult.First, 1, 1, "Arco seno"),
        new("ACOS", GenericResult.First, 1, 1, "Arco coseno"),
        new("ATAN", GenericResult.First, 1, 1, "Arco tangente"),
        new("MIN", GenericResult.First, 2, 8, "El menor"),
        new("MAX", GenericResult.First, 2, 8, "El mayor"),
        new("LIMIT", GenericResult.Second, 3, 3, "LIMIT(MN, IN, MX): IN entre MN y MX"),
        new("SEL", GenericResult.Second, 3, 3, "SEL(G, IN0, IN1): IN1 si G, si no IN0"),
        new("MUX", GenericResult.Second, 3, 8, "MUX(K, IN0, IN1…): el de índice K"),
        new("MOVE", GenericResult.First, 1, 1, "Copia"),
        new("SHL", GenericResult.First, 2, 2, "Desplaza bits a la izquierda"),
        new("SHR", GenericResult.First, 2, 2, "Desplaza bits a la derecha"),
        new("ROL", GenericResult.First, 2, 2, "Rota bits a la izquierda"),
        new("ROR", GenericResult.First, 2, 2, "Rota bits a la derecha"),
        new("TRUNC", GenericResult.Integer, 1, 1, "Parte entera de un real")
    ];

    public static StandardFunction? Get(string name) => All.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tipos entre los que hay conversión <c>X_TO_Y</c>.</summary>
    public static readonly IReadOnlyList<string> ConvertibleTypes =
    [
        "BOOL", "BYTE", "WORD", "DWORD", "LWORD", "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT", "REAL", "LREAL",
        "TIME", "STRING"
    ];

    /// <summary><c>REAL_TO_INT</c> → ("REAL", "INT"); null si no es una conversión.</summary>
    public static (string From, string To)? Conversion(string name)
    {
        var at = name.IndexOf("_TO_", StringComparison.OrdinalIgnoreCase);
        if (at <= 0) return null;
        var from = name[..at].ToUpperInvariant();
        var to = name[(at + 4)..].ToUpperInvariant();
        return ConvertibleTypes.Contains(from) && ConvertibleTypes.Contains(to) ? (from, to) : null;
    }
}
