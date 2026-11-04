using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FactoryNodes.Devices;

/// <summary>Tipo de un parámetro en sus registros Modbus (16 bits: INT/UINT; 32 bits, dos registros: DINT/UDINT/REAL).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceValueType { UINT, INT, UDINT, DINT, REAL }

/// <summary>
/// Qué es un parámetro: de ajuste (se guarda en las copias y se restaura en un equipo nuevo) o un valor en vivo (velocidad,
/// intensidad, peso: se lee y se puede llevar al PLC, pero no se guarda ni se restaura).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceParameterKind { Setting, Live }

/// <summary>Orden de las dos palabras de un valor de 32 bits.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceWordOrder { HighFirst, LowFirst }

/// <summary>Un grupo de parámetros: su número (el orden en el listado) y su texto («Datos del motor»).</summary>
public sealed record DeviceGroup(int Number, string Text);

/// <summary>
/// Los decimales de un parámetro que no son fijos: los da otro (autor, 2026-10-16, básculas: el peso del DAT 400 lleva los
/// del valor de división; el del eNod4, los del byte alto de 0x0008). Decimales = <c>(crudo de From » Shift) &amp; Mask</c>
/// y, con <see cref="Map"/>, lo que diga su tabla.
/// </summary>
public sealed class DeviceDecimals
{
    /// <summary>Código del parámetro que los da.</summary>
    public string From { get; set; } = "";

    /// <summary>Bits a la derecha antes de la máscara (8: el byte alto).</summary>
    public int Shift { get; set; }

    /// <summary>Bits que cuentan después de <see cref="Shift"/>; 0 = todos.</summary>
    public long Mask { get; set; }

    /// <summary>De cada valor (en número, «0») a sus decimales; sin tabla, el valor son los decimales.</summary>
    public Dictionary<string, int>? Map { get; set; }

    /// <summary>Los decimales con ese crudo de <see cref="From"/>, o null si el valor no está en la tabla o no tiene sentido.</summary>
    public int? Of(long raw)
    {
        var value = raw >> Shift;
        if (Mask != 0) value &= Mask;
        int? decimals = Map is null ? (int)Math.Min(value, int.MaxValue)
            : Map.TryGetValue(value.ToString(CultureInfo.InvariantCulture), out var mapped) ? mapped : null;
        return decimals is >= 0 and <= 6 ? decimals : null;
    }
}

/// <summary>
/// Un parámetro del catálogo. <see cref="Address"/> es la dirección como la escribe el manual del fabricante («0x1103» o
/// «8502»); <see cref="DeviceProtocol.RegisterBase"/> dice si hay que restarle 1 para la trama.
/// </summary>
public sealed class DeviceParameter
{
    /// <summary>Código del fabricante (<c>F002</c>, <c>ACC</c>): único en el modelo, es la clave en las copias.</summary>
    public string Code { get; set; } = "";

    public string Address { get; set; } = "";

    /// <summary>
    /// La dirección EtherNet/IP del mismo parámetro (PENDIENTES 45): clase/instancia/atributo como en el manual (<c>0x64/1/0x08</c>). Vacía =
    /// solo Modbus. Un equipo que solo habla EtherNet/IP deja <see cref="Address"/> vacía. El valor va en little-endian con el tamaño de su
    /// <see cref="Type"/> (2 bytes INT/UINT, 4 los de 32 bits).
    /// </summary>
    public string Cip { get; set; } = "";

    public DeviceValueType Type { get; set; } = DeviceValueType.UINT;

    /// <summary>Valor = registro × Scale (0,01 → centésimas).</summary>
    public double Scale { get; set; } = 1;

    public string Unit { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Número de grupo (<see cref="DeviceModel.Groups"/>); 0 = sin grupo, al final.</summary>
    public int Group { get; set; }

    /// <summary>Sale el primero en el listado, en «Básicos».</summary>
    public bool Basic { get; set; }

    public DeviceParameterKind Kind { get; set; } = DeviceParameterKind.Setting;

    /// <summary>Se puede escribir (restaurar). Un ajuste de solo lectura (versión, capacidad) se guarda pero no se escribe.</summary>
    public bool Writable { get; set; } = true;

    /// <summary>Textos de los valores de un parámetro de opciones (<c>"0": "Potenciómetro"</c>).</summary>
    public Dictionary<string, string>? Options { get; set; }

    /// <summary>Qué significa cada bit de una palabra de estado o de mando (<c>"0": "Listo"</c>); se enseñan los que están a 1.</summary>
    public Dictionary<string, string>? Bits { get; set; }

    /// <summary>Lo que hay que saber de este parámetro (unidad que depende de otro, cuándo se aplica…).</summary>
    public string Note { get; set; } = "";

    /// <summary>Sus decimales los da otro parámetro (un peso); se suman a los de <see cref="Scale"/>. Null = solo la escala.</summary>
    public DeviceDecimals? DecimalsFrom { get; set; }

    /// <summary>
    /// Es de la calibración de este equipo (básculas: capacidad, sensibilidad, puntos de calibración…): se guarda en las copias,
    /// pero al restaurar una copia o cargar un CSV no se escribe, porque en otra báscula la descalibraría (autor, 2026-10-16).
    /// Se puede cambiar a mano.
    /// </summary>
    public bool Calibration { get; set; }

    /// <summary>El mismo parámetro con otra escala (la de sus decimales de ahora: <see cref="DeviceValues.Resolve"/>).</summary>
    public DeviceParameter WithScale(double scale)
    {
        var copy = (DeviceParameter)MemberwiseClone();
        copy.Scale = scale;
        return copy;
    }

    [JsonIgnore]
    public int Registers => Type is DeviceValueType.UINT or DeviceValueType.INT ? 1 : 2;

    /// <summary>La dirección EtherNet/IP en números (clase, instancia, atributo); null si no tiene o no se entiende.</summary>
    [JsonIgnore]
    public (int Class, int Instance, int Attribute)? CipAddress
    {
        get
        {
            var parts = Cip.Split('/', StringSplitOptions.TrimEntries);
            if (parts.Length != 3) return null;
            var values = parts.Select(Number).ToArray();
            return values.All(v => v is >= 0 and <= 0xFFFF) ? (values[0]!.Value, values[1]!.Value, values[2]!.Value) : null;
        }
    }

    /// <summary>Lo que ocupa en EtherNet/IP: 2 bytes los de 16 bits, 4 los de 32.</summary>
    [JsonIgnore]
    public int CipBytes => 2 * Registers;

    private static int? Number(string part) =>
        part.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(part[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : null
            : int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : null;

    /// <summary>La dirección del manual en número (null si no se entiende).</summary>
    [JsonIgnore]
    public int? AddressNumber =>
        Address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(Address[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : null
            : int.TryParse(Address, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : null;
}

/// <summary>Cómo se habla con el modelo.</summary>
public sealed class DeviceProtocol
{
    /// <summary>1 si el manual numera los registros desde 1 (en la trama va dirección − 1; Omron MX2); 0 si ya es la de la trama.</summary>
    public int RegisterBase { get; set; }

    public DeviceWordOrder WordOrder { get; set; } = DeviceWordOrder.HighFirst;

    /// <summary>Registros como mucho por lectura.</summary>
    public int MaxRegistersPerRead { get; set; } = 16;

    /// <summary>
    /// Algunos equipos pierden lo escrito por Modbus al apagarse si no se les manda guardar: registro (como
    /// <see cref="DeviceParameter.Address"/>) y valor a escribir tras restaurar. Null = lo guardan solos.
    /// </summary>
    public DeviceSaveCommand? Save { get; set; }

    /// <summary>Lo que hay que saber antes de restaurar (equipo parado, en qué modo…), para enseñarlo.</summary>
    public string RestoreNotes { get; set; } = "";
}

public sealed record DeviceSaveCommand(string Address, int Value, string Description);

/// <summary>Dónde contesta el equipo a una orden y qué valores dicen «hecha» y «error» (eNod4: 0x0091, 2 y 3).</summary>
public sealed record DeviceCommandResponse(string Address, int Done, int Error);

/// <summary>
/// Una orden del equipo como botón (autor, 2026-10-16, básculas: cero, tara, borrar el pico; arrancar o parar la dosificación),
/// en vez de escribir un número en su registro de órdenes. <see cref="ClearFirst"/>: escribir 0 antes (eNod4);
/// <see cref="Response"/>: esperar su respuesta; <see cref="Confirm"/>: pedir confirmación antes (lo que mueve algo o borra).
/// </summary>
public sealed class DeviceCommand
{
    public string Text { get; set; } = "";
    public string Address { get; set; } = "";
    public int Value { get; set; }
    public bool ClearFirst { get; set; }
    public DeviceCommandResponse? Response { get; set; }
    public bool Confirm { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>
/// Un modelo del catálogo de dispositivos (autor, 2026-10-13): un JSON por marca y modelo en <c>library/devices/</c>, que se
/// va ampliando. Formato: <c>src/libs/FactoryNodes.Devices/FORMAT.md</c>.
/// </summary>
public sealed class DeviceModel
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Brand { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>«Variador», «Visor de peso», «Arrancador»…</summary>
    public string Kind { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>De dónde salen las direcciones (manual, página).</summary>
    public string Source { get; set; } = "";

    /// <summary>Probado contra un equipo real. Si no, se avisa antes de restaurar.</summary>
    public bool Verified { get; set; }

    /// <summary>El dibujo del equipo (SVG completo).</summary>
    public string Svg { get; set; } = "";

    public DeviceProtocol Protocol { get; set; } = new();
    public List<DeviceGroup> Groups { get; set; } = [];
    public List<DeviceParameter> Parameters { get; set; } = [];

    /// <summary>Las órdenes que se ofrecen como botones (cero, tara…). Vacío en los que no tienen.</summary>
    public List<DeviceCommand> Commands { get; set; } = [];

    /// <summary>«Omron MX2»: lo que identifica el modelo (en los dispositivos del proyecto y en las copias).</summary>
    [JsonIgnore]
    public string Key => KeyOf(Brand, Model);

    public static string KeyOf(string brand, string model) => $"{brand.Trim()} {model.Trim()}";

    public DeviceParameter? Parameter(string code) => Parameters.FirstOrDefault(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Esos parámetros y los que dan sus decimales (para leerlos juntos), cada uno una vez.</summary>
    public IReadOnlyList<DeviceParameter> WithDecimalSources(IEnumerable<DeviceParameter> parameters)
    {
        var list = parameters.ToList();
        foreach (var source in list.Where(p => p.DecimalsFrom is not null).Select(p => Parameter(p.DecimalsFrom!.From)).OfType<DeviceParameter>().ToList())
            if (!list.Contains(source)) list.Add(source);
        return list;
    }

    /// <summary>Dirección de la trama de un parámetro.</summary>
    public ushort PduAddress(DeviceParameter parameter) => (ushort)(parameter.AddressNumber!.Value - Protocol.RegisterBase);

    /// <summary>Dirección de la trama de una dirección como la escribe el manual («0x0090», «503»), o null si no vale.</summary>
    public ushort? AddressOf(string address) =>
        new DeviceParameter { Address = address }.AddressNumber is { } number && number - Protocol.RegisterBase is >= 0 and <= 0xFFFF
            ? (ushort)(number - Protocol.RegisterBase) : null;

    public string GroupText(int number) => Groups.FirstOrDefault(g => g.Number == number)?.Text ?? (number == 0 ? "Otros" : $"Grupo {number}");

    /// <summary>
    /// El listado como se enseña (autor, 2026-10-13): primero los básicos y luego los grupos por su número (el 0, al final),
    /// cada parámetro una sola vez.
    /// </summary>
    public IReadOnlyList<(string Title, int? Group, IReadOnlyList<DeviceParameter> Parameters)> Listing()
    {
        var list = new List<(string, int?, IReadOnlyList<DeviceParameter>)>();
        var basic = Parameters.Where(p => p.Basic).ToList();
        if (basic.Count > 0) list.Add(("Básicos", null, basic));
        foreach (var group in Parameters.Where(p => !p.Basic).GroupBy(p => p.Group).OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key))
            list.Add((GroupText(group.Key), group.Key, group.ToList()));
        return list;
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Cómo se escribe (MapNodes, PENDIENTES 200): con sangría, sin lo nulo y con los acentos tal cual (no <c>é</c>), como los ficheros
    /// de <c>library/devices</c> escritos a mano. Lo lee igual <see cref="Parse"/>.
    /// </summary>
    public static readonly JsonSerializerOptions WriteJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>El fichero del modelo (lo que lee <see cref="Parse"/>), con un salto de línea al final.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, WriteJson).ReplaceLineEndings("\n") + "\n";

    /// <summary>Dónde va en el catálogo: <c>&lt;marca&gt;/&lt;modelo&gt;.json</c> (la marca en minúsculas, como <c>library/devices/omron/MX2.json</c>).</summary>
    public string CatalogPath() => $"{SafeName(Brand).ToLowerInvariant()}/{SafeName(Model)}.json";

    private static string SafeName(string text)
    {
        // lo que no vale en un nombre de fichero en Windows ni en Linux: el mismo nombre en los dos
        var name = new string(text.Trim().Select(c => c < ' ' || "/\\:*?\"<>|".Contains(c) ? '-' : c).ToArray());
        return name.Length == 0 ? "sin-nombre" : name;
    }

    /// <summary>Lee un modelo y dice qué no vale (sin excepciones: un fichero roto del catálogo no tumba nada). Con algún problema, sin modelo.</summary>
    public static (DeviceModel? Model, IReadOnlyList<string> Problems) Parse(string json)
    {
        var (model, problems) = Read(json);
        return (problems.Count == 0 ? model : null, problems);
    }

    /// <summary>
    /// Lo que dice <see cref="Parse"/>, pero con el modelo aunque tenga problemas (null solo si no es un JSON de modelo): para un editor
    /// (MapNodes), que tiene que abrir un fichero a medias para arreglarlo.
    /// </summary>
    public static (DeviceModel? Model, IReadOnlyList<string> Problems) Read(string json)
    {
        DeviceModel? model;
        try
        {
            model = JsonSerializer.Deserialize<DeviceModel>(json, Json);
        }
        catch (JsonException ex)
        {
            return (null, [$"No es un JSON válido: {ex.Message}"]);
        }

        if (model is null) return (null, ["Vacío."]);
        var problems = new List<string>();
        if (model.Brand.Trim().Length == 0 || model.Model.Trim().Length == 0) problems.Add("Faltan la marca o el modelo.");
        if (model.Protocol.RegisterBase is not (0 or 1)) problems.Add("registerBase tiene que ser 0 o 1.");
        if (model.Protocol.MaxRegistersPerRead is < 1 or > 125) problems.Add("maxRegistersPerRead, entre 1 y 125.");
        foreach (var duplicate in model.Parameters.GroupBy(p => p.Code, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"El código {duplicate.Key} está repetido.");
        foreach (var p in model.Parameters)
        {
            if (p.Code.Trim().Length == 0) problems.Add($"Un parámetro sin código ({p.Description}).");
            if (p.Address.Length == 0 && p.Cip.Length == 0) problems.Add($"{p.Code}: no tiene dirección (ni Modbus ni EtherNet/IP).");
            else if (p.Address.Length > 0 && (p.AddressNumber is not { } address || address - model.Protocol.RegisterBase is < 0 or > 0xFFFF))
                problems.Add($"{p.Code}: la dirección «{p.Address}» no vale.");
            if (p.Cip.Length > 0 && p.CipAddress is null)
                problems.Add($"{p.Code}: la dirección EtherNet/IP «{p.Cip}» no vale (clase/instancia/atributo, p. ej. 0x64/1/0x08).");
            if (p.Scale == 0) problems.Add($"{p.Code}: la escala no puede ser 0.");
            if (p.Options?.Keys.FirstOrDefault(k => !long.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) is { } option)
                problems.Add($"{p.Code}: la opción «{option}» no es un número.");
            if (p.Bits?.Keys.FirstOrDefault(k => !int.TryParse(k, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bit) || bit is < 0 or > 31) is { } bit)
                problems.Add($"{p.Code}: el bit «{bit}» no vale.");
            if (p.DecimalsFrom is { } decimals)
            {
                if (model.Parameter(decimals.From) is not { } source) problems.Add($"{p.Code}: sus decimales salen de «{decimals.From}», que no existe.");
                else if (source.DecimalsFrom is not null) problems.Add($"{p.Code}: sus decimales salen de {source.Code}, que también los toma de otro.");
                if (decimals.Shift is < 0 or > 31) problems.Add($"{p.Code}: el desplazamiento de sus decimales, entre 0 y 31.");
                if (decimals.Map?.FirstOrDefault(kv => !long.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) || kv.Value is < 0 or > 6) is { Key: not null } bad)
                    problems.Add($"{p.Code}: en la tabla de decimales, «{bad.Key}: {bad.Value}» no vale (decimales de 0 a 6).");
            }
        }

        if (model.Protocol.Save is { } save && new DeviceParameter { Address = save.Address }.AddressNumber is null)
            problems.Add($"La dirección de guardar «{save.Address}» no vale.");
        foreach (var c in model.Commands)
        {
            if (c.Text.Trim().Length == 0) problems.Add($"Una orden sin texto ({c.Address} = {c.Value}).");
            if (model.AddressOf(c.Address) is null) problems.Add($"Orden «{c.Text}»: la dirección «{c.Address}» no vale.");
            if (c.Value is < 0 or > 0xFFFF) problems.Add($"Orden «{c.Text}»: el valor, entre 0 y 65535.");
            if (c.Response is { } r && model.AddressOf(r.Address) is null) problems.Add($"Orden «{c.Text}»: la dirección de la respuesta «{r.Address}» no vale.");
        }

        foreach (var duplicate in model.Commands.GroupBy(c => c.Text.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"La orden «{duplicate.Key}» está repetida.");
        return (model, problems);
    }
}
