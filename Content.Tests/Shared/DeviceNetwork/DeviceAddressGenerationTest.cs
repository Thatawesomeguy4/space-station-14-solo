using Content.Shared.DeviceNetwork;
using Content.Shared.DeviceNetwork.Systems;
using NUnit.Framework;

namespace Content.Tests.Shared.DeviceNetwork;

[TestFixture]
public sealed class DeviceAddressGenerationTest
{
    [TestCase(0)]
    [TestCase(1)]
    public void RepeatedInvalidOrOccupiedDrawsUseAnAvailableAddress(int draw)
    {
        var network = new DeviceNet();
        network.Devices[1] = default;
        network.Devices[2] = default;
        var calls = 0;
        var address = DeviceNetworkSystem.GenerateValidAddressId(network, () =>
        {
            calls++;
            Assert.That(calls, Is.LessThanOrEqualTo(8), "Address allocation must not retry random draws forever.");
            return draw;
        });
        Assert.That((int) address, Is.EqualTo(3));
        Assert.That(network.Devices.ContainsKey(address), Is.False);
        Assert.That(calls, Is.EqualTo(8));
    }

    [Test]
    public void AvailableRandomAddressIsPreserved()
    {
        var address = DeviceNetworkSystem.GenerateValidAddressId(new DeviceNet(), () => 42);
        Assert.That((int) address, Is.EqualTo(42));
    }
}
