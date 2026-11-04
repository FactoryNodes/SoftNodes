using System.Text;
using FactoryNodes.Iec61131.Model;

namespace FactoryNodes.Iec61131.Emission;

/// <summary>
/// Un fichero generado para un PLC: nombre, texto y cómo se codifica al guardarlo. Quien llama decide dónde
/// escribirlo (las libs no tocan disco).
/// </summary>
public sealed record EmittedFile(string FileName, string Content, bool Utf8Bom)
{
    /// <summary>Bytes tal como los espera la herramienta del fabricante (TIA: UTF-8 con BOM; Sysmac: ASCII sin BOM).</summary>
    public byte[] ToBytes() => new UTF8Encoding(Utf8Bom).GetPreamble().Concat(Encoding.UTF8.GetBytes(Content)).ToArray();
}

/// <summary>Resultado de un emisor: los ficheros y los diagnósticos (si hay errores, los ficheros no deben usarse).</summary>
public sealed record EmitResult(IReadOnlyList<EmittedFile> Files, IReadOnlyList<LibraryDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}
