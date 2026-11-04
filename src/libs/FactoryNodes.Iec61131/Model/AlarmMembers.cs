using System.Globalization;
using System.Text.RegularExpressions;

namespace FactoryNodes.Iec61131.Model;

/// <summary>
/// La copia legible de cada bit de <c>Status.ALM_Word</c> (MASTER_LIBRARY_CONVENTIONS §5.3): cada bloque escribe
/// <c>Data.Status.ALM_Fuse := (Data.Status.ALM_Word AND máscara) &lt;&gt; WORD#16#0000;</c>, con la máscara en número o en una
/// constante del bloque (<c>ALM_BIT_FUSE</c>). De ahí sale qué miembro es cada bit, y con él si su entrada está cableada
/// (una alarma de una entrada sin cablear no puede saltar: docs/design/ALARMS.md).
/// </summary>
public static partial class AlarmMembers
{
    [GeneratedRegex(@"^\s*Data\.Status\.(?<member>ALM_\w+)\s*:=\s*\(\s*Data\.Status\.ALM_Word\s+AND\s+(?<mask>[\w#]+)\s*\)\s*<>\s*WORD#16#0+\s*;", RegexOptions.IgnoreCase)]
    private static partial Regex CopyRegex();

    [GeneratedRegex(@"^WORD#16#(?<hex>[0-9A-Fa-f]+)$")]
    private static partial Regex MaskRegex();

    /// <summary>Bit → miembro de <c>Status</c> (<c>Status.ALM_Fuse</c>) de un FUNCTION_BLOCK; solo las máscaras de un bit.</summary>
    public static IReadOnlyDictionary<int, string> Of(Pou pou)
    {
        var constants = pou.Variables.Where(v => v.InitialValue is not null)
            .ToDictionary(v => v.Name, v => v.InitialValue!.Trim(), StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<int, string>();
        foreach (var line in pou.Body.Lines.OfType<CodeLine>())
        {
            if (CopyRegex().Match(line.Text) is not { Success: true } m) continue;
            var mask = m.Groups["mask"].Value;
            if (constants.TryGetValue(mask, out var value)) mask = value;
            if (MaskRegex().Match(mask) is not { Success: true } hex ||
                !int.TryParse(hex.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits) ||
                bits == 0 || (bits & (bits - 1)) != 0) continue;
            map.TryAdd(System.Numerics.BitOperations.Log2((uint)bits), $"Status.{m.Groups["member"].Value}");
        }

        return map;
    }
}
