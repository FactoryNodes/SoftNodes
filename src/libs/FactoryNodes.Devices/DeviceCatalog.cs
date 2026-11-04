using System.Text.Json;

namespace FactoryNodes.Devices;

/// <summary>
/// El catálogo de dispositivos: los modelos (un JSON cada uno, en <c>library/devices/&lt;marca&gt;/&lt;modelo&gt;.json</c>), por
/// su clave («Omron MX2»). Un fichero que no vale no entra y queda en <see cref="Problems"/>. El texto de cada modelo se
/// guarda tal cual, para pasárselo a la Factory Box.
/// </summary>
public sealed class DeviceCatalog
{
    private readonly Dictionary<string, (DeviceModel Model, string Json)> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _problems = [];

    public static DeviceCatalog Empty { get; } = new([]);

    /// <param name="files">Nombre (para los avisos) y texto de cada modelo.</param>
    public DeviceCatalog(IEnumerable<(string Name, string Json)> files)
    {
        foreach (var (name, json) in files)
        {
            var (model, problems) = DeviceModel.Parse(json);
            if (model is null) _problems.Add($"{name}: {string.Join(" ", problems)}");
            else if (!_models.TryAdd(model.Key, (model, json))) _problems.Add($"{name}: «{model.Key}» ya está en otro fichero.");
        }
    }

    public IReadOnlyList<DeviceModel> Models => _models.Values.Select(m => m.Model).OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<string> Problems => _problems;

    public DeviceModel? Find(string key) => _models.TryGetValue(key, out var m) ? m.Model : null;

    /// <summary>El texto de los modelos que usan esos dispositivos habilitados (lo que viaja a la caja).</summary>
    public IReadOnlyList<string> JsonFor(IEnumerable<ProjectDevice> devices) =>
        devices.Where(d => d.Enabled).Select(d => d.ModelKey).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(_models.ContainsKey).Select(k => _models[k].Json).ToList();

    /// <summary>Lee el array que llega a la caja (<c>dispositivos.json</c>).</summary>
    public static DeviceCatalog FromArray(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DeviceCatalog(document.RootElement.EnumerateArray().Select((e, i) => ($"modelo {i + 1}", e.GetRawText())));
    }
}
