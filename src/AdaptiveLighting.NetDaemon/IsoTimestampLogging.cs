using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace AdaptiveLighting.NetDaemon;

/// <summary>Stamps the log with a full ISO date, since an add-on log is read days later and across midnight, and keeps a durable copy.</summary>
public static class IsoTimestampLogging
{
	/// <summary>Replaces the host's console logger with one whose timestamps carry the date.</summary>
	/// <remarks>
	///     Chain this after <c>UseNetDaemonDefaultLogging()</c>: it replaces the logger, so an earlier call brings the
	///     default template back and drops every Debug line. <c>minimumLevel</c> covers everything but the Microsoft
	///     namespaces, which stay at Warning. The durable copy attaches here, because a second <c>UseSerilog</c> would
	///     replace this one; a host with no <c>AdaptiveLighting:ConfigPath</c> gets the console alone.
	/// </remarks>
	public static IHostBuilder UseIsoTimestampLogging(
		this IHostBuilder builder,
		LogEventLevel minimumLevel = LogEventLevel.Debug)
	{
		ArgumentNullException.ThrowIfNull(builder);

		// Levels are set here, never read from configuration: this replaces the logger the host built from
		// Logging:LogLevel, and Serilog's ReadFrom.Configuration wants a "Serilog" section most hosts do not have,
		// leaving Information and silently dropping every Debug line.
		return builder.UseSerilog((context, logger) =>
		{
			logger
				.MinimumLevel.Is(minimumLevel)
				.MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
				.Enrich.FromLogContext()
				.WriteTo.Console(
					outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
					theme: AnsiConsoleTheme.Code);

			if (DurableDirectoryFor(context) is { } directory)
			{
				// The file sink reports a failure to open only through SelfLog, which nothing else here turns on.
				SelfLog.Enable(new LogFailureReport().Write);

				DurableLogFile.AddTo(logger, directory, DurableDirectory.Stem(context.Configuration));
			}
		});
	}

	/// <summary>Where the durable copy goes, or <c>null</c> when this machine has nowhere that outlives a deploy.</summary>
	private static string? DurableDirectoryFor(HostBuilderContext context)
	{
		// This runs while the logger is being built, so there is no host logger to resolve; the console is what exists.
		using ILoggerFactory factory = LoggerFactory.Create(logging => logging.AddConsole());

		return DurableDirectory.Subfolder(
			context.Configuration,
			context.HostingEnvironment.ContentRootPath,
			DurableLogFile.FolderName,
			factory.CreateLogger(typeof(IsoTimestampLogging).FullName!));
	}
}
