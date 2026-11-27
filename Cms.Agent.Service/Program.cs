using Cms.Agent.Service;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
	options.ServiceName = "ClubAgent";
});

// Second safety net (Windows test 2026-11-27, defect 1): an exception that leaves the Worker must not stop the
// service host. The Worker itself never ends on a server, network or timeout error (Cms.Client.AgentLoops), and the
// installer sets restart actions for the service (sc.exe failure ClubAgent).
builder.Services.Configure<HostOptions>(options =>
{
	options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

builder.Services.AddLogging(logging =>
{
	logging.AddEventLog();
});

builder.Services.AddHttpClient();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
