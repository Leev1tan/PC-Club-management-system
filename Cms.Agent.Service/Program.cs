using Cms.Agent.Service;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
	options.ServiceName = "ClubAgent";
});

builder.Services.AddLogging(logging =>
{
	logging.AddEventLog();
});

builder.Services.AddHttpClient();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
