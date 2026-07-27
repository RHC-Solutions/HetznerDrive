using HetznerDrive.Core;
using HetznerDrive.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Host for the HetznerDrive Windows service.
//
// Runs as LocalSystem so its mounts exist before anyone signs in. It only ever mounts to directory
// mountpoints: a drive letter created in session 0 is invisible to interactive users, which would
// look like a silent failure rather than a configuration mistake.
//
// It can also be run directly from a console for troubleshooting, which is far easier than
// attaching a debugger to a service.

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = ServiceControl.ServiceName;
});

builder.Services.AddHostedService<MountWorker>();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// The Event Log is where an administrator will actually look when mounts are missing, and it is
// the only sink available before the ProgramData directory exists.
builder.Logging.AddEventLog(settings =>
{
    settings.SourceName = ServiceControl.ServiceName;
});

// A rolling file alongside the rest of the app's logs, for the detail the Event Log would bury.
builder.Logging.AddProvider(new FileLoggerProvider(MachinePaths.LogsDir));

builder.Logging.SetMinimumLevel(LogLevel.Information);

var host = builder.Build();
await host.RunAsync();
