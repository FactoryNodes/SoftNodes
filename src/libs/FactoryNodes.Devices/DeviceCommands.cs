namespace FactoryNodes.Devices;

/// <summary>Lo que pasó al mandar una orden: si se hizo y el texto para enseñarlo.</summary>
public sealed record DeviceCommandResult(bool Ok, string Text);

/// <summary>
/// Mandar una orden del catálogo (<see cref="DeviceCommand"/>): con su protocolo (escribir 0 antes si el equipo lo pide,
/// la orden y, si tiene respuesta, esperarla).
/// </summary>
public static class DeviceCommands
{
    /// <summary>Lo que se espera la respuesta de una orden.</summary>
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    public static async Task<DeviceCommandResult> RunAsync(IModbusClient client, byte unit, DeviceModel model, DeviceCommand command,
        CancellationToken cancellation = default, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        if (model.AddressOf(command.Address) is not { } address) return new(false, $"«{command.Text}»: la dirección {command.Address} no vale.");
        try
        {
            if (command.ClearFirst) await client.WriteMultipleRegistersAsync(unit, address, [0], cancellation);
            await client.WriteMultipleRegistersAsync(unit, address, [(ushort)command.Value], cancellation);
            if (command.Response is not { } response || model.AddressOf(response.Address) is not { } at) return new(true, $"«{command.Text}» mandada.");

            var waited = TimeSpan.Zero;
            var step = TimeSpan.FromMilliseconds(100);
            while (true)
            {
                var value = (await client.ReadHoldingRegistersAsync(unit, at, 1, cancellation))[0];
                if (value == response.Done) return new(true, $"«{command.Text}» hecha.");
                if (value == response.Error) return new(false, $"El equipo no pudo hacer «{command.Text}».");
                if (waited >= ResponseTimeout) return new(false, $"«{command.Text}»: el equipo no ha dicho que la haya hecho (respuesta {value}).");
                await delay(step, cancellation);
                waited += step;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(false, $"No se pudo mandar «{command.Text}»: {ex.Message}");
        }
    }
}

/// <summary>
/// Qué líneas de una restauración se escriben (autor, 2026-10-16: «checks a lo que queremos mandar y tal vez un check
/// grupal»): una casilla por línea y una por grupo del modelo; de entrada, lo que cambia menos la calibración.
/// </summary>
public sealed class DeviceRestoreChoice
{
    private readonly HashSet<DeviceRestoreLine> _checked;

    public DeviceRestoreChoice(DeviceModel model, IReadOnlyList<DeviceRestoreLine> lines)
    {
        Lines = lines.Where(l => l.Changes).ToList();
        _checked = Lines.Where(l => l.CheckedByDefault).ToHashSet();
        Groups = Lines.GroupBy(l => l.Parameter.Group).OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key)
            .Select(g => (model.GroupText(g.Key), (IReadOnlyList<DeviceRestoreLine>)g.ToList())).ToList();
    }

    /// <summary>Solo lo que cambia.</summary>
    public IReadOnlyList<DeviceRestoreLine> Lines { get; }

    public IReadOnlyList<(string Title, IReadOnlyList<DeviceRestoreLine> Lines)> Groups { get; }

    public bool IsChecked(DeviceRestoreLine line) => _checked.Contains(line);

    public void Set(DeviceRestoreLine line, bool on)
    {
        if (on) _checked.Add(line);
        else _checked.Remove(line);
    }

    /// <summary>true si están todas, false si ninguna, null si algunas.</summary>
    public bool? GroupState(IReadOnlyList<DeviceRestoreLine> group) =>
        group.All(_checked.Contains) ? true : group.Any(_checked.Contains) ? null : false;

    /// <summary>La casilla del grupo: si estaban todas, ninguna; si no, todas.</summary>
    public void ToggleGroup(IReadOnlyList<DeviceRestoreLine> group)
    {
        var on = GroupState(group) != true;
        foreach (var line in group) Set(line, on);
    }

    public IReadOnlyList<DeviceRestoreLine> Selected => Lines.Where(_checked.Contains).ToList();

    public string CalibrationNote => DeviceRestore.CalibrationNote(Lines.Where(l => !_checked.Contains(l)));
}
