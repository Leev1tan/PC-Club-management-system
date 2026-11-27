using System.Net;
using Cms.Server.Devices;

namespace Cms.Server.Demo;

/// <summary>Wake-on-LAN in the demo: no network traffic; the simulator starts the simulated PC.</summary>
public sealed class DemoWakeOnLanSender : IWakeOnLanSender
{
    private readonly DemoSimulator _simulator;

    public DemoWakeOnLanSender(DemoSimulator simulator) => _simulator = simulator;

    public Task SendAsync(Guid deviceId, byte[] mac, IPEndPoint target, CancellationToken ct = default)
    {
        _simulator.Wake(deviceId);
        return Task.CompletedTask;
    }
}
