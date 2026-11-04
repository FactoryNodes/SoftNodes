using System.Text.RegularExpressions;

namespace FactoryNodes.SoftNodes;

/// <summary>
/// El perfil de un PLC simulado («TIA» como un S7-1500, «Sysmac» como un NX/NJ): lo que difiere de la librería ST estándar. Las
/// funciones <c>/// COMPAT</c> de la librería maestra llevan un cuerpo de referencia (el de TIA) y cada emisor pone el de su plataforma;
/// el perfil de SoftNodes hace lo mismo: sustituye el cuerpo de cada una por el de su PLC antes de ejecutar. El cuerpo sustituido es
/// el que ve y recorre el depurador. (El otro efecto del perfil, el direccionamiento OPC UA, lo decide el exe con el mismo nombre.)
/// </summary>
/// <param name="Name">«TIA» o «Sysmac».</param>
/// <param name="CompatBodies">Función <c>/// COMPAT</c> → su sentencia en ST para este PLC. Las que no están, ejecutan el cuerpo de referencia.</param>
public sealed record SoftProfile(string Name, IReadOnlyDictionary<string, string> CompatBodies)
{
    /// <summary>El de referencia: el cuerpo escrito en la librería es la implementación TIA (S7 4-20 mA, TIME en milisegundos).</summary>
    public static readonly SoftProfile Tia = new("TIA", new Dictionary<string, string>());

    /// <summary>
    /// Omron: las tarjetas NX del autor no desbordan y dan 32737 o más con el cable cortado (<c>RAW_OVERFLOW</c> y
    /// <c>RAW_CABLE_BREAK</c>, como las expande <c>SysmacEmitter</c>). Las demás (<c>TENTHS_TO_TIME</c>, <c>TIME_TO_SECONDS</c>,
    /// <c>TIME_TO_MS</c>) dan el mismo resultado con otras funciones de Omron (<c>NanoSecToTime</c>…): su cuerpo de referencia vale.
    /// </summary>
    public static readonly SoftProfile Sysmac = new("Sysmac", new Dictionary<string, string>
    {
        ["RAW_OVERFLOW"] = "RAW_OVERFLOW := FALSE;",
        ["RAW_CABLE_BREAK"] = "RAW_CABLE_BREAK := Raw >= 32737;"
    });

    public static IReadOnlyList<SoftProfile> All { get; } = [Tia, Sysmac];

    /// <summary>El perfil por su nombre (sin distinguir mayúsculas); el de TIA si no se conoce.</summary>
    public static SoftProfile ByName(string? name) =>
        All.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Tia;

    /// <summary>
    /// Los ficheros de la librería con el cuerpo de las funciones <c>/// COMPAT</c> de este perfil cambiado. La sentencia sustituye a
    /// la de referencia en su misma línea (los comentarios y la numeración no se mueven). Una función que no existe en la librería
    /// no se toca; una que no tiene una sola sentencia de referencia es un error (se cambió la librería y hay que revisar el perfil).
    /// </summary>
    public IReadOnlyList<(string Name, string Text)> Apply(IReadOnlyList<(string Name, string Text)> files)
    {
        if (CompatBodies.Count == 0) return files;
        var result = new List<(string Name, string Text)>();
        foreach (var (name, text) in files)
        {
            var changed = text;
            foreach (var (function, statement) in CompatBodies)
                changed = Replace(changed, function, statement);
            result.Add((name, changed));
        }

        return result;
    }

    private static string Replace(string text, string function, string statement)
    {
        // FUNCTION <Nombre> … END_VAR <cuerpo> END_FUNCTION: el cuerpo son los comentarios y UNA sentencia de referencia
        var match = Regex.Match(text, $@"(?<head>\bFUNCTION\s+{Regex.Escape(function)}\b.*?\bEND_VAR[ \t]*\r?\n)(?<body>.*?)(?<tail>\bEND_FUNCTION\b)",
            RegexOptions.Singleline);
        if (!match.Success) return text;

        var lines = match.Groups["body"].Value.Split('\n');
        var code = lines.Select((line, i) => (line, i)).Where(x => x.line.Trim().Length > 0 && !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal)).ToList();
        if (code.Count != 1)
            throw new InvalidOperationException($"La función /// COMPAT {function} no tiene una sola sentencia de referencia ({code.Count}): hay que revisar el perfil.");

        var (original, index) = code[0];
        var carriageReturn = original.EndsWith('\r') ? "\r" : "";
        lines[index] = statement + carriageReturn;
        return text[..match.Groups["body"].Index] + string.Join('\n', lines) + text[(match.Groups["body"].Index + match.Groups["body"].Length)..];
    }
}
