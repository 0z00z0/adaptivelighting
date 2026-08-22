using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace AdaptiveLighting.NetDaemon;

/// <summary>Stamps the log with a full ISO date instead of a bare time, and keeps a durable copy of it.</summary>
/// <remarks>
///     NetDaemon's default console template writes <c>HH:mm:ss</c>, and an add-on log is read days later and across
///     midnight, where a bare time cannot be placed at all.
/// </remarks>
public static class IsoTimestampLogging
{
	/// <summary>Replaces the host's console logger with one whose timestamps carry the date.</summary>
	/// <remarks>
	///     Chain this after the host's own logging call, which for NetDaemon is <c>UseNetDaemonDefaultLogging()</c>.
	///     It replaces the logger instead of reconfiguring it, so whichever runs last wins: put it earlier and the
	///     default template returns, taking every Debug line with it.
	///     <para>
	///         <c>minimumLevel</c> is the floor for everything but the Microsoft namespaces, which stay at Warning.
	///     </para>
	///     <para>
	///         The durable copy attaches to this same call, because a second <c>UseSerilog</c> would replace this one
	///         instead of adding to it. A host with no <c>AdaptiveLighting:ConfigPath</c> gets the console alone.
	///     </para>
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

			if (DurableCopy(context) is { } durable)
				logger.WriteTo.Sink(durable);
		});
	}

	/// <summary>The sink that outlives a restart, or <c>null</c> when this machine has nowhere durable to put it.</summary>
	private static CircularLogSink? DurableCopy(HostBuilderContext context)
	{
		// This runs while the logger is being built, so there is no host logger to resolve; the console is what exists.
		using ILoggerFactory factory = LoggerFactory.Create(logging => logging.AddConsole());

		string? directory = DurableDirectory.Subfolder(
			context.Configuration,
			context.HostingEnvironment.ContentRootPath,
			CircularLogWriter.FolderName,
			factory.CreateLogger(typeof(IsoTimestampLogging).FullName!));

		return directory is null
			? null
			: new CircularLogSink(new CircularLogWriter(directory, DurableDirectory.Stem(context.Configuration)));
	}
}
