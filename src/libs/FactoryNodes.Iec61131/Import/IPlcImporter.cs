namespace FactoryNodes.Iec61131.Import;

/// <summary>Nombre del lenguaje de la librería maestra (ST estándar IEC 61131-3 con las directivas <c>///</c>).</summary>
public static class MasterLanguage
{
    /// <summary>Provisional (2026-10-02, autor): cambiarlo aquí lo cambia en los textos que lo nombran.</summary>
    public const string Name = "NodeTalk";
}

public enum ImportNoteLevel
{
    /// <summary>Se ha cambiado algo para que valga (un nombre, un tipo); conviene mirarlo.</summary>
    Info,

    /// <summary>No se ha podido convertir del todo: hay que completarlo a mano.</summary>
    Warning
}

/// <summary>Lo que el importador avisa: dónde (bloque, segmento, variable) y qué.</summary>
public sealed record ImportNote(ImportNoteLevel Level, string Where, string Message)
{
    public override string ToString() => $"{(Level == ImportNoteLevel.Warning ? "AVISO" : "info")} · {Where}: {Message}";
}

/// <summary>
/// Un fichero importado ya convertido a la librería maestra: <see cref="Text"/> es un <c>.st</c> que lee
/// <c>MasterLibraryReader</c>; lo que no se pudo convertir va en <see cref="Notes"/> (y en la cabecera del texto).
/// </summary>
public sealed record ImportResult(string Name, string Text, IReadOnlyList<ImportNote> Notes)
{
    public int Warnings => Notes.Count(n => n.Level == ImportNoteLevel.Warning);
}

/// <summary>
/// Importa lo que exporta la herramienta de un fabricante (TIA Portal, Sysmac Studio…) y lo convierte, en lo posible,
/// a la librería maestra. Cada plataforma es una DLL (<c>FactoryNodes.Import.&lt;Plataforma&gt;</c>), como los emisores; quien
/// importa solo ve esta interfaz. Sin disco: recibe el nombre y el texto del fichero.
/// </summary>
public interface IPlcImporter
{
    /// <summary>Identificador de la plataforma ("TIA", "Sysmac"), el mismo que el de su emisor.</summary>
    string Platform { get; }

    /// <summary>Nombre para mostrar ("Siemens TIA Portal").</summary>
    string DisplayName { get; }

    /// <summary>Extensiones que lee, con punto (".xml", ".scl").</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>¿Es este fichero suyo? Mira el contenido, no solo la extensión (TIA y Sysmac exportan los dos .xml).</summary>
    bool CanImport(string fileName, string content);

    ImportResult Import(string fileName, string content);
}
