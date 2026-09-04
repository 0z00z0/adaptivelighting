using System.Reflection;

using AdaptiveLighting.NetDaemon;

using NetDaemon.AppModel;
using NetDaemon.Extensions.Logging;
using NetDaemon.Extensions.Scheduler;
using NetDaemon.Runtime;

// The smallest host that runs AdaptiveLighting: the standard NetDaemon boilerplate
// (https://netdaemon.xyz) plus the two calls AdaptiveLighting.NetDaemon adds. No generated entity
// file, no YAML-configured apps — AdaptiveLightingApp.cs is the only app in this assembly.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Host
	.UseNetDaemonAppSettings()
	.UseNetDaemonDefaultLogging()
	.UseNetDaemonRuntime();

builder.Services
	.AddAppsFromAssembly(Assembly.GetExecutingAssembly())
	.AddNetDaemonStateManager()
	.AddNetDaemonScheduler();

builder.AddAdaptiveLighting();

WebApplication app = builder.Build();

app.UseAdaptiveLighting();

await app.RunAsync();
