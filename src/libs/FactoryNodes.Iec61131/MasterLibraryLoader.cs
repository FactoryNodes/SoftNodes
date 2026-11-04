using FactoryNodes.Iec61131.Reading;
using FactoryNodes.Iec61131.Validation;

namespace FactoryNodes.Iec61131;

/// <summary>
/// Punto de entrada: lee la librería maestra y, si se ha podido leer, valida sus convenciones. Los emisores solo
/// deben trabajar con una librería sin errores.
/// </summary>
public static class MasterLibraryLoader
{
    /// <param name="files">Nombre (p. ej. "Motor") y texto de cada fichero .st, en el orden de la librería
    /// (primero las dependencias: Compat, AlarmManager, Motor…). Quien llama lee los ficheros: esta lib no toca disco.</param>
    public static LibraryReadResult Load(IEnumerable<(string Name, string Text)> files)
    {
        var read = MasterLibraryReader.Read(files);
        if (read.HasErrors) return read;
        return read with { Diagnostics = [.. read.Diagnostics, .. MasterLibraryValidator.Validate(read.Library)] };
    }

    /// <summary>
    /// Lee <c>library/master/order.txt</c>: un nombre de fichero (sin extensión) por línea, primero las dependencias.
    /// Las líneas vacías y las que empiezan por <c>#</c> se ignoran.
    /// </summary>
    public static IReadOnlyList<string> ParseOrder(string orderText) =>
        orderText.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
}
