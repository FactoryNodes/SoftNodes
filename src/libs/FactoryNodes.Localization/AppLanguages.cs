using System.Globalization;

namespace FactoryNodes.Localization;

/// <summary>Un idioma del programa: su código ISO («ca») y su nombre escrito en ese idioma («Català»), como sale en el selector.</summary>
public sealed record AppLanguage(string Code, string NativeName)
{
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Code);
}

/// <summary>
/// Los idiomas de FactoryNodes y SoftNodes (autor, 2026-10-20): castellano peninsular (el de partida, en el que está escrito todo),
/// catalán, inglés, francés, alemán y portugués.
/// </summary>
public static class AppLanguages
{
    public static AppLanguage Spanish { get; } = new("es", "Español");

    public static IReadOnlyList<AppLanguage> All { get; } =
    [
        Spanish,
        new("ca", "Català"),
        new("en", "English"),
        new("fr", "Français"),
        new("de", "Deutsch"),
        new("pt", "Português")
    ];

    /// <summary>El idioma de un código («ca», «ca-ES», «EN»), o null si no es uno de los seis.</summary>
    public static AppLanguage? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var language = code.Trim().Split('-', '_')[0];
        return All.FirstOrDefault(l => l.Code.Equals(language, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// El idioma con el que arranca el programa (autor, 2026-10-20): el último elegido; la primera vez, el de Windows si es uno de
    /// los seis; si no, castellano.
    /// </summary>
    public static AppLanguage Resolve(string? saved, CultureInfo system) =>
        Find(saved) ?? Find(system.TwoLetterISOLanguageName) ?? Spanish;

    /// <summary>¿Es castellano? Entonces el texto es la propia clave y no hace falta buscarlo.</summary>
    public static bool IsSpanish(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("es", StringComparison.OrdinalIgnoreCase) || culture.Equals(CultureInfo.InvariantCulture);
}
