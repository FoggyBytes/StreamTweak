using StreamTweakService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "StreamTweakService");
builder.Services.AddSingleton<GpuPowerMonitor>();
// Controllers off with the PC (10.3.0): the suspend callback, and the pipe's Xbox verbs.
builder.Services.AddSingleton<IXboxGipControl, XboxGip>();
builder.Services.AddSingleton<XboxControllerPower>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<XboxControllerPower>());
builder.Services.AddHostedService<PipeWorker>();

var host = builder.Build();
host.Run();
