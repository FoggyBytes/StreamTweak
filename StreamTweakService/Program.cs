using StreamTweakService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "StreamTweakService");
builder.Services.AddSingleton<GpuPowerMonitor>();
builder.Services.AddHostedService<PipeWorker>();

var host = builder.Build();
host.Run();
