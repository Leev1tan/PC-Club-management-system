namespace Cms.Server.Repositories;

using Cms.Server.Controllers;

public interface IDeviceRepository
{
    DeviceRegistrationResponse Register(DeviceRegistrationRequest request);
    bool Heartbeat(string deviceKey, DeviceHeartbeatRequest request);
    IEnumerable<DeviceView> List();
    CommandView? EnqueueCommand(Guid deviceId, EnqueueCommandRequest request);
    IEnumerable<CommandView> PollCommands(Guid deviceId, int max);
    bool AckCommand(Guid deviceId, Guid commandId, AckCommandRequest request);
}

