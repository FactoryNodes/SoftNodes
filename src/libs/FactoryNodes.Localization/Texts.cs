using System.Globalization;
using System.Resources;

namespace FactoryNodes.Localization;

/// <summary>
/// Los textos traducidos de una lib: sus <c>Strings.&lt;idioma&gt;.resx</c>, cuya <b>clave es el texto en castellano</b> tal cual sale hoy
/// en pantalla (WHY.md). Lo que no está traducido sale en castellano: se puede traducir por partes.
/// </summary>
public sealed class Texts(ResourceManager resources)
{
    /// <summary>Los de <paramref name="baseName"/> (p. ej. «FactoryNodes.Presentation.Localization.Strings») en el ensamblado de <typeparamref name="T"/>.</summary>
    public static Texts For<T>(string baseName) => new(new ResourceManager(baseName, typeof(T).Assembly));

    /// <summary>El texto en el idioma de la interfaz (<see cref="CultureInfo.CurrentUICulture"/>), o el castellano si no está traducido.</summary>
    public string T(string spanish) => T(spanish, CultureInfo.CurrentUICulture);

    public string T(string spanish, CultureInfo culture)
    {
        if (spanish.Length == 0 || AppLanguages.IsSpanish(culture)) return spanish;
        if (Find(spanish, culture) is { } translated) return translated;
        // «EMPRESA» (cajetín) se traduce como «Empresa» y se pone en mayúsculas: un .resx no admite dos claves que solo cambian
        // en mayúsculas (WHY.md)
        return UpperCaseKey(spanish) is { } normal && Find(normal, culture) is { } word ? word.ToUpper(culture) : spanish;
    }

    /// <summary>«EMPRESA» → «Empresa»; null si el texto no está todo en mayúsculas.</summary>
    public static string? UpperCaseKey(string spanish) =>
        spanish.Length > 1 && spanish.Any(char.IsLetter) && spanish == spanish.ToUpperInvariant()
            ? spanish[..1] + spanish[1..].ToLowerInvariant()
            : null;

    private string? Find(string spanish, CultureInfo culture)
    {
        try
        {
            return resources.GetString(spanish, culture) is { Length: > 0 } translated ? translated : null;
        }
        catch (MissingManifestResourceException)
        {
            return null;   // la lib aún no tiene ningún texto traducido
        }
    }

    /// <summary>Un texto con huecos <c>{0}</c>, <c>{1}</c>…: se traduce el modelo y luego se rellena.</summary>
    public string F(string spanishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(spanishFormat), args);
}
