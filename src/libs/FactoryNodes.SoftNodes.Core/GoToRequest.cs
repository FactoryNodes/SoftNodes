using System.Text.Json;

namespace FactoryNodes.SoftNodes;

/// <summary>
/// Una petición de SoftNodes a FactoryNodes: «llévame a este loop (y a esta etapa)». SoftNodes nunca edita, así que el sitio
/// donde se edita es FactoryNodes. Viaja como un fichero <see cref="FileName"/> en la carpeta de datos de FactoryNodes
/// (<c>%LOCALAPPDATA%\FactoryNodes</c>): SoftNodes lo escribe y FactoryNodes, si está abierto, lo lee, lo borra y se va a la etapa.
/// </summary>
/// <param name="Project">Nombre del proyecto del programa que se está ejecutando: FactoryNodes solo hace caso si es el que tiene abierto.</param>
/// <param name="Loop">Nombre del loop como identificador (<see cref="LoopPlace.Loop"/>).</param>
public sealed record GoToRequest(string Project, string Loop, int? Stage, int? Priority, DateTime At)
{
    public const string FileName = "ir-a.json";

    /// <summary>Una petición más vieja que esto se ignora: no se quiere abrir un loop minutos después de haberlo pedido.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Lee una petición; null si el texto no lo es (un fichero a medio escribir).</summary>
    public static GoToRequest? TryParse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<GoToRequest>(text, Json) is { Loop.Length: > 0 } request ? request : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>¿Sigue vigente a <paramref name="now"/>?</summary>
    public bool IsFresh(DateTime now) => now - At <= MaxAge && At - now <= MaxAge;
}
