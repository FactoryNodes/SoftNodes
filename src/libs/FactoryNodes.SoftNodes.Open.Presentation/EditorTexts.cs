namespace FactoryNodes.SoftNodes.Open.Presentation;

/// <summary>Los textos del panel «Buscar» (Ctrl+F) del código del depurador, en el idioma elegido.</summary>
public static class EditorTexts
{
    public static string MatchCase => L.T("Distinguir mayúsculas y minúsculas");
    public static string MatchWholeWords => L.T("Solo palabras completas");
    public static string UseRegex => L.T("Expresión regular");
    public static string FindNext => L.T("Buscar siguiente (F3)");
    public static string FindPrevious => L.T("Buscar anterior (Mayús+F3)");
    public static string Error => L.T("Error:");
    public static string NoMatches => L.T("No se ha encontrado");

    // menú del botón derecho del código
    public static string ShowWhitespace => L.T("Ver espacios y tabuladores");
    public static string ShowRuler => L.T("Regla vertical en la columna 120");
    public static string ResetZoom => L.T("Tamaño de letra normal (Ctrl+0)");
}
