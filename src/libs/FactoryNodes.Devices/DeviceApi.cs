using System.Globalization;

namespace FactoryNodes.Devices;

/// <summary>Un dispositivo Modbus en la lista de la API de la Factory Box (<c>GET /api/devices</c>).</summary>
/// <param name="Model">El modelo del catálogo («Omron MX2»).</param>
/// <param name="Kind">«Variador», «Visor de peso»… (vacío si el modelo no está en el catálogo de la caja).</param>
/// <param name="Problem">Por qué no se puede leer (modelo que no está en el catálogo…), o vacío.</param>
public sealed record DeviceApiInfo(string Name, string Description, string Model, string Kind, string Host, int Port, int Unit, bool Enabled, string Problem);

/// <summary>
/// Un parámetro leído. <see cref="Value"/> es el valor con su escala y sus decimales (12,5), <see cref="Raw"/> el registro tal cual y
/// <see cref="Text"/> como se enseña en pantalla («12,5 s», «1 · Potenciómetro», «16#0003 · Listo · En marcha»). Si la lectura falló,
/// <see cref="Ok"/> es false, <see cref="Value"/>, <see cref="Raw"/> y <see cref="Text"/> van vacíos y <see cref="Problem"/> dice por qué.
/// </summary>
public sealed record DeviceApiParameter(string Code, string Description, string Group, string Kind, string Type, string Unit, bool Writable,
    bool Ok, double? Value, long? Raw, string Text, string Problem);

/// <summary>Lo leído de un dispositivo en el momento de la llamada.</summary>
public sealed record DeviceApiReadout(string Name, string Model, DateTimeOffset ReadAt, int Read, int Failed, IReadOnlyList<DeviceApiParameter> Parameters);

/// <summary>Una copia de parámetros guardada en la caja, en la lista de <c>GET /api/devices/{name}/backups</c>.</summary>
public sealed record DeviceApiBackupInfo(long Id, DateTimeOffset TakenAt, string TakenBy, string Note, int Parameters);

/// <summary>Un valor de una copia: el registro tal cual y cómo se vería.</summary>
public sealed record DeviceApiBackupValue(string Code, string Description, string Unit, long Raw, double? Value, string Text);

/// <summary>Una copia con todos sus valores (<c>GET /api/devices/{name}/backups/{id}</c>).</summary>
public sealed record DeviceApiBackup(long Id, string Device, string Model, DateTimeOffset TakenAt, string TakenBy, string Note, IReadOnlyList<DeviceApiBackupValue> Values);

/// <summary>
/// Los dispositivos Modbus tal como los sirve la API de la Factory Box (autor, 2026-10-18: «expón dispositivos e históricos; no les
/// limitemos la funcionalidad»). **Solo lectura**: restaurar copias o escribir parámetros de un equipo sigue siendo solo desde la
/// configuración de la caja, con contraseña, nunca por la API. Aquí está la parte que no toca la red ni el disco.
/// </summary>
public static class DeviceApi
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    public static DeviceApiInfo Info(ProjectDevice device, DeviceModel? model) =>
        new(device.Name, device.Description, device.ModelKey, model?.Kind ?? "", device.Host, device.Port, device.Unit, device.Enabled,
            model is null ? $"«{device.ModelKey}» no está en el catálogo de dispositivos de la caja." : "");

    /// <summary>
    /// Qué parámetros leer: los códigos pedidos (separados por comas) o, sin ellos, los «en vivo» (<c>kind=live</c>, el valor por defecto:
    /// velocidad, intensidad, peso), los de ajuste (<c>setting</c>) o todos (<c>all</c>). Devuelve también el motivo si algo no vale.
    /// </summary>
    public static (IReadOnlyList<DeviceParameter> Parameters, string Problem) Select(DeviceModel model, string? kind, string? parameters)
    {
        if (!string.IsNullOrWhiteSpace(parameters))
        {
            var chosen = new List<DeviceParameter>();
            foreach (var code in parameters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (model.Parameter(code) is not { } found) return ([], $"«{model.Key}» no tiene el parámetro «{code}».");
                if (!chosen.Contains(found)) chosen.Add(found);
            }

            return (chosen, "");
        }

        return (kind?.Trim().ToLowerInvariant() ?? "live") switch
        {
            "live" or "" => (model.Parameters.Where(p => p.Kind == DeviceParameterKind.Live).ToList(), ""),
            "setting" => (model.Parameters.Where(p => p.Kind == DeviceParameterKind.Setting).ToList(), ""),
            "all" => (model.Parameters.ToList(), ""),
            var other => ([], $"«kind={other}» no vale: live, setting o all.")
        };
    }

    /// <summary>
    /// Lo leído, con el valor de cada parámetro pedido. <paramref name="readings"/> trae también los parámetros que dan los decimales de
    /// otros (un peso necesita su valor de división) aunque no se hayan pedido: se usan para resolverlos y no salen en el resultado.
    /// </summary>
    public static DeviceApiReadout Readout(ProjectDevice device, DeviceModel model, IReadOnlyList<DeviceReading> readings,
        IReadOnlyCollection<DeviceParameter> wanted, DateTimeOffset now)
    {
        var list = readings.Where(r => wanted.Contains(r.Parameter)).Select(r => Parameter(model, r, DeviceValues.Resolve(r.Parameter, readings))).ToList();
        return new DeviceApiReadout(device.Name, model.Key, now, list.Count(p => p.Ok), list.Count(p => !p.Ok), list);
    }

    private static DeviceApiParameter Parameter(DeviceModel model, DeviceReading reading, DeviceParameter resolved)
    {
        var p = reading.Parameter;
        var group = p.Basic ? "Básicos" : model.GroupText(p.Group);
        return reading.Raw is { } raw
            ? new(p.Code, p.Description, group, p.Kind.ToString(), p.Type.ToString(), p.Unit, p.Writable, true,
                IsNumber(p) ? Math.Round(DeviceValues.Value(resolved, raw), 6) : null, raw, DeviceValues.Text(resolved, raw, Spanish), "")
            : new(p.Code, p.Description, group, p.Kind.ToString(), p.Type.ToString(), p.Unit, p.Writable, false, null, null, "", reading.Problem);
    }

    /// <summary>Los parámetros de opciones y de bits son códigos, no medidas: sin valor numérico escalado.</summary>
    private static bool IsNumber(DeviceParameter p) => p.Options is not { Count: > 0 } && p.Bits is not { Count: > 0 };

    public static DeviceApiBackupInfo BackupInfo(long id, DeviceBackup backup) =>
        new(id, backup.TakenAt, backup.TakenBy, backup.Note, backup.Values.Count);

    public static DeviceApiBackup Backup(long id, DeviceBackup backup, DeviceModel? model) =>
        new(id, backup.Device, backup.ModelKey, backup.TakenAt, backup.TakenBy, backup.Note,
            backup.Values.Select(v =>
            {
                var parameter = model?.Parameter(v.Key);
                var resolved = parameter is null ? null : DeviceValues.Resolve(parameter, backup.RawOf);
                return new DeviceApiBackupValue(v.Key, parameter?.Description ?? "", parameter?.Unit ?? "", v.Value,
                    parameter is null || !IsNumber(parameter) ? null : Math.Round(DeviceValues.Value(resolved!, v.Value), 6),
                    resolved is null ? "" : DeviceValues.Text(resolved, v.Value, Spanish));
            }).ToList());
}
