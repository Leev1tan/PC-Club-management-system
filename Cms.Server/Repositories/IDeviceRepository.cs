namespace Cms.Server.Repositories;

using Cms.Server.Controllers;

public interface IDeviceRepository
{
    DeviceRegistrationResult Register(DeviceRegistrationRequest request);
    /// <summary>Records a heartbeat. Returns the device status, or null when the key is unknown or revoked.</summary>
    string? Heartbeat(string deviceKey, DeviceHeartbeatRequest request);
    IEnumerable<DeviceView> List();
    CommandView? EnqueueCommand(Guid deviceId, EnqueueCommandRequest request);
    IEnumerable<CommandView> PollCommands(Guid deviceId, int max);
    bool AckCommand(Guid deviceId, Guid commandId, AckCommandRequest request);
}

