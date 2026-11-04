namespace FactoryNodes.Iec61131.Emission;

/// <summary>
/// El aviso legal que lleva cada bloque generado (autor, 2026-10-13): el código es de FactoryNodes, pero quien lo tiene
/// puede usarlo, modificarlo y distribuirlo (licencia MIT); se entrega tal cual y la responsabilidad de su uso es de
/// quien lo usa. Cada emisor lo pone como comentarios <c>//</c> al principio del código de cada POU (la forma de
/// comentario ya validada en los dos PLC). El texto completo: <c>docs/legal/LICENCIA_BLOQUES.md</c> y la web.
/// Fijo (sin la fecha de hoy) para que la salida no cambie de una generación a otra.
/// </summary>
public static class BlockNotice
{
    /// <summary>
    /// Titular de los derechos: la marca y su titular legal (el mismo del aviso legal de la web y de «Acerca de»).
    /// </summary>
    public const string Holder = "FactoryNodes - Vicente Zaragoza Arnau";

    public const string TermsUrl = "https://www.factorynodes.com/terminos";

    /// <summary>En ASCII (Sysmac solo admite ASCII) y en líneas cortas.</summary>
    public static IReadOnlyList<string> Lines { get; } =
    [
        $"(c) 2026 {Holder}. https://www.factorynodes.com",
        "Licencia MIT: se puede usar, copiar, modificar y distribuir libremente, conservando este aviso.",
        "Se entrega TAL CUAL, sin garantia de ningun tipo. Quien lo usa es responsable de comprobarlo en su",
        $"instalacion y de las consecuencias de su uso. Terminos: {TermsUrl}"
    ];

    /// <summary>Las líneas como comentarios <c>//</c>, con esa sangría.</summary>
    public static IEnumerable<string> Comments(string indent = "") => Lines.Select(l => $"{indent}// {l}");
}
