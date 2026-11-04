using System.Text.RegularExpressions;

namespace FactoryNodes.SoftNodes;

/// <summary>Dónde cae una línea del código de un loop: el loop, la etapa y, si la línea es de una transición, su prioridad.</summary>
/// <param name="Loop">Nombre del loop como identificador (<c>DosifSilo01</c>): el <c>BaseName</c> del bloque <c>FB_Loop_&lt;Nombre&gt;</c>.</param>
/// <param name="Stage">Número de la etapa en cuyo código está la línea, o null si es de otra parte del bloque (arranque, alarmas, código de usuario…).</param>
/// <param name="Priority">Prioridad de la transición de esa etapa, si la línea es de una.</param>
public sealed record LoopPlace(string Loop, int? Stage, int? Priority);

/// <summary>
/// De una línea del código de un bloque de loop a su etapa de FactoryNodes («Ir al loop»). El bloque lo escribe
/// <c>LoopBlockWriter</c>: dentro de <c>/// REGION Steps</c> cada etapa empieza con <c>NN:  // …</c> y cada transición con
/// <c>// Prioridad P -&gt; etapa M (…)</c>; las etapas 900 y 1000 van en sus propias regiones (<c>Step 900 - disabled</c>). Si
/// cambia cómo se escribe, cambia esto: lo comprueba una prueba contra el escritor (<c>Emitters.Tests</c>).
/// </summary>
public static class LoopSourceMap
{
    /// <summary>Los bloques de loop se llaman <c>FB_Loop_&lt;Nombre&gt;</c>.</summary>
    public const string BlockPrefix = "FB_Loop_";

    private static readonly Regex StageLabel = new(@"^\s*(\d+):\s*//", RegexOptions.Compiled);
    private static readonly Regex Transition = new(@"^\s*//\s*Prioridad\s+(\d+)\s*->", RegexOptions.Compiled);
    private static readonly Regex SpecialStage = new(@"^/// REGION Step (\d+) - ", RegexOptions.Compiled);

    public static bool IsLoopBlock(string pouName) => pouName.StartsWith(BlockPrefix, StringComparison.OrdinalIgnoreCase);

    /// <param name="pouName">El bloque que se ve (<c>FB_Loop_DosifSilo01</c>).</param>
    /// <param name="lines">Su código línea a línea, con <c>/// REGION …</c> y <c>/// END_REGION</c> como en el depurador.</param>
    /// <param name="line">La línea (desde 1).</param>
    /// <returns>Dónde cae; null si el bloque no es de un loop o la línea no existe.</returns>
    public static LoopPlace? Locate(string pouName, IReadOnlyList<string> lines, int line)
    {
        if (!IsLoopBlock(pouName) || line < 1 || line > lines.Count) return null;

        string? region = null;
        int? stage = null, priority = null;
        for (var i = 0; i < line; i++)
        {
            var text = lines[i];
            if (text.StartsWith("/// REGION ", StringComparison.Ordinal))
            {
                region = text["/// REGION ".Length..].Trim();
                priority = null;
                stage = SpecialStage.Match(text) is { Success: true } special ? int.Parse(special.Groups[1].Value) : null;
            }
            else if (text.StartsWith("/// END_REGION", StringComparison.Ordinal))
            {
                (region, stage, priority) = (null, null, null);
            }
            else if (region == "Steps")
            {
                if (StageLabel.Match(text) is { Success: true } label)
                    (stage, priority) = (int.Parse(label.Groups[1].Value), null);
                else if (Transition.Match(text) is { Success: true } transition)
                    priority = int.Parse(transition.Groups[1].Value);
            }
        }

        return new LoopPlace(pouName[BlockPrefix.Length..], stage, priority);
    }
}
