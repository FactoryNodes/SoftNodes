using System.Globalization;
using FactoryNodes.Iec61131.Emission;
using FactoryNodes.SoftNodes.Runtime;
using Opc.Ua;
using Opc.Ua.Server;

namespace FactoryNodes.SoftNodes.OpcUa;

/// <summary>
/// Construye el árbol de nodos con la memoria del programa y lee y escribe en ella con el candado del ciclo. Cada nodo
/// lleva el identificador que da el <see cref="IPlcAddressing"/> de la plataforma a su ruta neutra, para que el cliente
/// de FactoryNodes lo encuentre por su identificador sin navegar. Las estructuras y los arrays son objetos
/// (<c>Elements</c> y debajo <c>Elements[3]</c>, como los direcciona cada PLC); las variables elementales son las hojas.
/// </summary>
internal sealed class SoftNodeManager : CustomNodeManager2
{
    /// <summary>Cada cuánto se miran en la memoria las variables suscritas (la mitad del ciclo por defecto).</summary>
    public const int RefreshMs = 50;

    private readonly IPlcAddressing _addressing;
    private readonly SoftProgram _program;

    /// <summary>Cómo se lee de la memoria cada variable publicada (para refrescar las suscritas).</summary>
    private readonly Dictionary<NodeState, Func<object>> _readers = [];
    private Timer? _refresh;

    public SoftNodeManager(IServerInternal server, ApplicationConfiguration configuration, IPlcAddressing addressing, SoftProgram program)
        : base(server, configuration, addressing.OpcUaNamespaceUri)
    {
        _addressing = addressing;
        _program = program;
    }

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var references))
                externalReferences[ObjectIds.ObjectsFolder] = references = new List<IReference>();

            var root = new FolderState(null)
            {
                SymbolicName = "SoftNodes",
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.FolderType,
                NodeId = new NodeId("SoftNodes", NamespaceIndex),
                BrowseName = new QualifiedName("SoftNodes", NamespaceIndex),
                DisplayName = "SoftNodes",
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                EventNotifier = EventNotifiers.None
            };
            root.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
            references.Add(new NodeStateReference(ReferenceTypes.Organizes, false, root.NodeId));
            AddPredefinedNode(SystemContext, root);

            lock (_program.Sync)
            {
                foreach (var (name, slot) in PublishedRoots())
                    Build(root, [name], slot);
            }
        }

        _refresh = new Timer(_ => Refresh(), null, RefreshMs, RefreshMs);
    }

    /// <summary>
    /// Las suscripciones de OPC UA solo avisan cuando el nodo dice que ha cambiado, y aquí cambia la memoria (el programa), no el nodo: sin
    /// esto, un cliente suscrito solo veía lo que escribía él (los modos de un motor se quedaban como los pulsó el HMI aunque el bloque los
    /// cambiara). Se miran solo las variables que alguien tiene suscritas.
    /// </summary>
    private void Refresh()
    {
        try
        {
            lock (Lock)
            {
                foreach (var monitored in MonitoredNodes.Values)
                {
                    if (monitored.Node is not BaseDataVariableState variable || !_readers.TryGetValue(variable, out var read)) continue;
                    var value = read();
                    if (Equals(value, variable.Value)) continue;
                    variable.Value = value;
                    variable.Timestamp = DateTime.UtcNow;
                    variable.ClearChangeMasks(SystemContext, false);
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // el servidor se está parando: el siguiente tic ya no llega
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refresh?.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Lo que publica un PLC: los datos del proyecto y las señales físicas (DB en TIA, globales en Sysmac).</summary>
    private IEnumerable<(string Name, Slot Slot)> PublishedRoots() =>
        _program.Globals.Where(g => g.Value.Value is StructValue && (g.Key.StartsWith("hw_", StringComparison.OrdinalIgnoreCase) ||
                                                                      g.Key.StartsWith("io_", StringComparison.OrdinalIgnoreCase)))
            .Select(g => (g.Key, g.Value));

    private void Build(NodeState parent, List<string> path, Slot slot)
    {
        var name = path[^1];
        var identifier = _addressing.OpcUaIdentifier(path);
        switch (slot.Value)
        {
            case StructValue structure:
            {
                var node = AddObject(parent, name, identifier);
                foreach (var (member, child) in structure.Members) Build(node, [.. path, member], child);
                break;
            }

            case ArrayValue array:
            {
                var node = AddObject(parent, name, identifier);
                for (var i = 0; i < array.Items.Length; i++)
                {
                    var index = array.Lower + i;
                    var element = array.Items[i];
                    var item = $"{name}[{index}]";
                    // el elemento cuelga del array pero su camino sustituye al tramo del array (Elements → Elements[3])
                    Build(node, [.. path.Take(path.Count - 1), item], element);
                }

                break;
            }

            case FbInstance:
                break;   // la instancia de un bloque no se publica (en el PLC es un DB de instancia aparte)

            default:
                AddVariable(parent, name, identifier, slot);
                break;
        }
    }

    private BaseObjectState AddObject(NodeState parent, string name, string identifier)
    {
        var node = new BaseObjectState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = ObjectTypeIds.BaseObjectType,
            NodeId = new NodeId(identifier, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            EventNotifier = EventNotifiers.None
        };
        parent.AddChild(node);
        AddPredefinedNode(SystemContext, node);
        return node;
    }

    private void AddVariable(NodeState parent, string name, string identifier, Slot slot)
    {
        var (dataType, toOpc, fromOpc) = OpcTypes.For(slot.Type);
        var variable = new BaseDataVariableState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = new NodeId(identifier, NamespaceIndex),
            BrowseName = new QualifiedName(name, NamespaceIndex),
            DisplayName = name,
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            DataType = dataType,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentReadOrWrite,
            UserAccessLevel = AccessLevels.CurrentReadOrWrite,
            Historizing = false,
            StatusCode = StatusCodes.Good,
            Timestamp = DateTime.UtcNow,
            Value = toOpc(slot.Value)
        };

        object Read()
        {
            lock (_program.Sync) return toOpc(slot.Value);
        }

        _readers[variable] = Read;
        variable.OnSimpleReadValue = (ISystemContext context, NodeState node, ref object value) =>
        {
            value = Read();
            return ServiceResult.Good;
        };

        variable.OnSimpleWriteValue = (ISystemContext context, NodeState node, ref object value) =>
        {
            try
            {
                var converted = StInterpreter.Coerce(fromOpc(value), slot.Type);
                lock (_program.Sync) slot.Value = converted;
                return ServiceResult.Good;
            }
            catch (Exception e) when (e is InvalidCastException or FormatException or OverflowException)
            {
                return StatusCodes.BadTypeMismatch;
            }
        };

        parent.AddChild(variable);
        AddPredefinedNode(SystemContext, variable);
    }
}

/// <summary>Cómo se ve un tipo del PLC por OPC UA, y cómo se convierte el valor en cada sentido.</summary>
internal static class OpcTypes
{
    public static (NodeId DataType, Func<object, object> ToOpc, Func<object, object> FromOpc) For(StType type)
    {
        switch (type.Kind)
        {
            case StTypeKind.Bool:
                return (DataTypeIds.Boolean, v => v, v => Convert.ToBoolean(v, CultureInfo.InvariantCulture));
            case StTypeKind.Real:
                return type.Is32BitReal
                    ? (DataTypeIds.Float, v => (float)(double)v, v => Convert.ToDouble(v, CultureInfo.InvariantCulture))
                    : (DataTypeIds.Double, v => v, v => Convert.ToDouble(v, CultureInfo.InvariantCulture));
            case StTypeKind.Time:
                return (DataTypeIds.Int32, v => (int)(long)v, v => Convert.ToInt64(v, CultureInfo.InvariantCulture));
            case StTypeKind.String:
                return (DataTypeIds.String, v => v, v => (string)v);
            case StTypeKind.Int:
                Func<object, object> from = v => Convert.ToInt64(v, CultureInfo.InvariantCulture);
                return (type.Bits, type.Signed) switch
                {
                    (8, true) => (DataTypeIds.SByte, v => (sbyte)(long)v, from),
                    (8, false) => (DataTypeIds.Byte, v => (byte)(long)v, from),
                    (16, true) => (DataTypeIds.Int16, v => (short)(long)v, from),
                    (16, false) => (DataTypeIds.UInt16, v => (ushort)(long)v, from),
                    (32, true) => (DataTypeIds.Int32, v => (int)(long)v, from),
                    (32, false) => (DataTypeIds.UInt32, v => (uint)(long)v, from),
                    (_, true) => (DataTypeIds.Int64, v => (long)v, from),
                    _ => (DataTypeIds.UInt64, v => (ulong)(long)v, from)
                };
            default:
                throw new NotSupportedException($"El tipo {type} no se publica por OPC UA.");
        }
    }
}
