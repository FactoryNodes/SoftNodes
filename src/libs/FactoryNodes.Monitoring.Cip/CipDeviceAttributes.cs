using FactoryNodes.Devices;

namespace FactoryNodes.Monitoring.Cip;

/// <summary>
/// <see cref="IDeviceAttributeClient"/> de los dispositivos (<c>FactoryNodes.Devices</c>) sobre <see cref="CipDeviceClient"/>: con él,
/// <c>DeviceReader.ReadAsync</c> lee por EtherNet/IP los parámetros del catálogo que tienen dirección CIP.
/// </summary>
public sealed class CipDeviceAttributes(CipDeviceClient client) : IDeviceAttributeClient
{
    public async Task<IReadOnlyList<(byte[]? Bytes, string Problem)>> ReadAttributesAsync(
        IReadOnlyList<(int Class, int Instance, int Attribute)> attributes, CancellationToken cancellation = default)
    {
        var values = await client.ReadAsync([.. attributes.Select(a => new CipAttribute(a.Class, a.Instance, a.Attribute))], cancellation);
        return [.. values.Select(v => (v.Bytes, v.Problem))];
    }

    public Task WriteAttributeAsync((int Class, int Instance, int Attribute) attribute, byte[] value, CancellationToken cancellation = default) =>
        client.WriteAsync(new CipAttribute(attribute.Class, attribute.Instance, attribute.Attribute), value, cancellation);
}
