using Cms.Agent.Service;
using Cms.Client;
using Microsoft.Extensions.Logging.EventLog;

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

// Logs (Windows test 2026-11-27, defect 6): the text log %ProgramData%\ClubAgent\logs\agent.log (Information and up,
// Administrators only) and the Application event log (Warning and up). The installer registers the event source
// "Cms.Agent.Service"; LocalService cannot create it. HttpClient writes a line per request: only its warnings are kept.
AgentLog.EnsureDirectory();
builder.Logging.AddEventLog();
builder.Services.Configure<EventLogSettings>(settings =>
{
	settings.SourceName = "Cms.Agent.Service";
	settings.LogName = "Application";
});
builder.Logging.AddProvider(new FileLoggerProvider(new RollingLogFile(AgentLog.Directory)));
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

builder.Services.AddHttpClient();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
