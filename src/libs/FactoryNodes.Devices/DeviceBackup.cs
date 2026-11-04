using System.Globalization;
using System.Text;

namespace FactoryNodes.Devices;

/// <summary>
/// Una copia de los parámetros de ajuste de un equipo (autor, 2026-10-13): se guarda en la Factory Box y sirve para pasarlos
/// a un equipo nuevo de la misma marca y modelo tras una avería. Los valores van en crudo (los registros tal cual, sin
/// escala), por código del fabricante. Se exporta e importa en CSV.
/// </summary>
public sealed record DeviceBackup(string Device, string ModelKey, DateTimeOffset TakenAt, string TakenBy, string Note,
    IReadOnlyDictionary<string, long> Values)
{
    /// <summary>
    /// CSV con punto y coma (lo abre Excel en España): una cabecera <c>#</c> con dispositivo, modelo, fecha, quién y nota, y una
    /// fila por parámetro: código, valor crudo, valor con unidad y descripción (las dos últimas solo para leerlo; al importar
    /// manda el crudo).
    /// </summary>
    public string ToCsv(DeviceModel model)
    {
        var csv = new StringBuilder();
        csv.AppendLine($"# FactoryNodes;copia de parámetros;{Escape(Device)};{Escape(ModelKey)};{TakenAt:O};{Escape(TakenBy)};{Escape(Note)}");
        csv.AppendLine("Código;Crudo;Valor;Descripción");
        foreach (var (code, raw) in Values)
        {
            var parameter = model.Parameter(code);
            var text = parameter is null ? "" : DeviceValues.Text(DeviceValues.Resolve(parameter, RawOf), raw, CultureInfo.GetCultureInfo("es-ES"));
            csv.AppendLine($"{Escape(code)};{raw.ToString(CultureInfo.InvariantCulture)};{Escape(text)};{Escape(parameter?.Description ?? "")}");
        }

        return csv.ToString();
    }

    /// <summary>El crudo guardado de un parámetro (por código, sin distinguir mayúsculas), o null.</summary>
    public long? RawOf(string code) =>
        Values.TryGetValue(code, out var raw) ? raw
        : Values.FirstOrDefault(v => v.Key.Equals(code, StringComparison.OrdinalIgnoreCase)) is { Key: not null } found ? found.Value : null;

    /// <summary>Lee un CSV de <see cref="ToCsv"/>; null y el porqué si no lo es.</summary>
    public static (DeviceBackup? Backup, string Problem) FromCsv(string csv)
    {
        csv = csv.TrimStart((char)0xFEFF);   // el BOM con que se guarda para Excel
        var lines = csv.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2 || Split(lines[0]) is not { Count: >= 7 } head || head[0] != "# FactoryNodes" || head[1] != "copia de parámetros")
            return (null, "No es una copia de parámetros de FactoryNodes.");
        if (!DateTimeOffset.TryParse(head[4], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return (null, "La fecha de la copia no se entiende.");
        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (line, number) in lines.Skip(2).Select((l, i) => (l, i + 3)))
        {
            var cells = Split(line);
            if (cells.Count < 2 || !long.TryParse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
                return (null, $"Línea {number}: falta el código o el valor crudo.");
            values[cells[0]] = raw;
        }

        return (new DeviceBackup(head[2], head[3], at, head[5], head[6], values), "");
    }

    private static string Escape(string text) =>
        text.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;

    private static List<string> Split(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ';') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }

        cells.Add(cell.ToString());
        return cells;
    }
}

/// <summary>Una línea de lo que haría restaurar: el parámetro, lo que tiene ahora y lo que tendría.</summary>
public sealed record DeviceRestoreLine(DeviceParameter Parameter, long? Current, long Wanted)
{
    public bool Changes => Current != Wanted;

    /// <summary>Marcada de entrada para escribir: lo que cambia, salvo la calibración (es de cada báscula).</summary>
    public bool CheckedByDefault => Changes && !Parameter.Calibration;
}

/// <summary>El resultado de restaurar: lo escrito y comprobado, y lo que falló con su porqué.</summary>
public sealed record DeviceRestoreResult(int Written, IReadOnlyList<string> Problems);

/// <summary>
/// Pasar una copia a un equipo (autor, 2026-10-13: «en caso de avería, poder transferir los parámetros a un nuevo dispositivo,
/// misma marca y modelo»). Solo desde la configuración de la Factory Box, con contraseña: nunca por la API. Antes, lo que
/// cambiaría (<see cref="Plan"/>); después de escribir, se vuelve a leer para comprobarlo.
/// </summary>
public static class DeviceRestore
{
    /// <summary>Por qué no se puede restaurar esa copia en ese modelo, o vacío.</summary>
    public static string Problem(DeviceModel model, DeviceBackup backup) =>
        !string.Equals(model.Key, backup.ModelKey, StringComparison.OrdinalIgnoreCase)
            ? $"La copia es de un {backup.ModelKey} y el equipo es un {model.Key}: solo se restaura en la misma marca y modelo."
            : !model.Verified
                ? $"El catálogo del {model.Key} aún no está comprobado contra un equipo real: se puede leer y guardar, pero no restaurar."
                : "";

    /// <summary>
    /// Los parámetros de ajuste escribibles de la copia, con lo que tiene ahora el equipo. Cada línea lleva su parámetro con
    /// los decimales de la copia (para enseñarlo). Lo que se escribe lo marca quien restaura, línea a línea o por grupos (autor,
    /// 2026-10-16: «pondría checks a lo que queremos mandar y tal vez un check grupal»); de entrada, todo menos la calibración
    /// (<see cref="DeviceRestoreLine.CheckedByDefault"/>).
    /// </summary>
    public static IReadOnlyList<DeviceRestoreLine> Plan(DeviceModel model, DeviceBackup backup, IReadOnlyList<DeviceReading> current) =>
        backup.Values
            .Select(v => (Parameter: model.Parameter(v.Key), Raw: v.Value))
            .Where(v => v.Parameter is { Kind: DeviceParameterKind.Setting, Writable: true })
            .Select(v => new DeviceRestoreLine(DeviceValues.Resolve(v.Parameter!, backup.RawOf),
                current.FirstOrDefault(c => c.Parameter.Code.Equals(v.Parameter!.Code, StringComparison.OrdinalIgnoreCase))?.Raw, v.Raw))
            .ToList();

    /// <summary>El aviso de la calibración que queda sin marcar (de cada báscula), o vacío.</summary>
    public static string CalibrationNote(IEnumerable<DeviceRestoreLine> lines) =>
        lines.Count(l => l.Changes && l.Parameter.Calibration) is var n and > 0
            ? $"{n} de calibración sin marcar: la calibración es de cada báscula y en otra la estropearía. Márcalos solo si es la misma."
            : "";

    /// <summary>Escribe lo que cambia, uno a uno, lo vuelve a leer y, si el modelo lo pide, manda guardar.</summary>
    /// <param name="allowUnverified">
    /// Escribir aunque el catálogo no esté comprobado: solo el EXE, con el técnico delante del equipo y tras confirmarlo
    /// expresamente (autor, 2026-10-13: módulo Modbus «modificar y transferir»). La caja, nunca.
    /// </param>
    public static async Task<DeviceRestoreResult> ApplyAsync(IModbusClient client, byte unit, DeviceModel model,
        IReadOnlyList<DeviceRestoreLine> lines, CancellationToken cancellation = default, bool allowUnverified = false)
    {
        if (!model.Verified && !allowUnverified) return new DeviceRestoreResult(0, [$"El catálogo del {model.Key} no está comprobado: no se restaura."]);
        var problems = new List<string>();
        var written = 0;
        foreach (var line in lines.Where(l => l.Changes))
        {
            var p = line.Parameter;
            try
            {
                await client.WriteMultipleRegistersAsync(unit, model.PduAddress(p), DeviceValues.Registers(p, line.Wanted, model.Protocol.WordOrder), cancellation);
                var back = await client.ReadHoldingRegistersAsync(unit, model.PduAddress(p), (ushort)p.Registers, cancellation);
                var raw = DeviceValues.Raw(p, back, model.Protocol.WordOrder);
                if (raw == line.Wanted) written++;
                else problems.Add($"{p.Code}: se escribió {line.Wanted} y el equipo tiene {raw}.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                problems.Add($"{p.Code} ({p.Description}): {ex.Message}");
            }
        }

        if (model.Protocol.Save is { } save && written > 0)
        {
            try
            {
                var address = new DeviceParameter { Address = save.Address }.AddressNumber!.Value - model.Protocol.RegisterBase;
                await client.WriteMultipleRegistersAsync(unit, (ushort)address, [(ushort)save.Value], cancellation);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add($"No se pudo mandar guardar ({save.Description}): {ex.Message}");
            }
        }

        return new DeviceRestoreResult(written, problems);
    }
}
