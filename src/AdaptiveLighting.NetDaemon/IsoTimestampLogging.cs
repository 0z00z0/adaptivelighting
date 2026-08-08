using Microsoft.Extensions.Hosting;

using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace AdaptiveLighting.NetDaemon;

/// <summary>Stamps the log with a full ISO date instead of a bare time.</summary>
/// <remarks>
///     NetDaemon's default console template writes <c>HH:mm:ss</c>. An add-on log is read days later and across
///     midnight, where a bare <c>09:45:57</c> cannot be placed at all: on 2026-08-08 an hour went into working
///     out which day an event belonged to, and the answer changed the diagnosis.
/// </remarks>
public static class IsoTimestampLogging
{
	/// <summary>Replaces the host's console logger with one whose timestamps carry the date.</summary>
	/// <remarks>
	///     <b>Chain this after the host's own logging call</b>, which for NetDaemon is
	///     <c>UseNetDaemonDefaultLogging()</c>. It replaces the logger rather than reconfiguring it, so whichever
	///     runs last wins — put it earlier and the default template silently returns, taking every Debug line
	///     with it. That is why this is its own named call and not a flag on
	///     <see cref="AdaptiveLightingHouse.AddAdaptiveLighting"/>: the constraint is an ordering one, and an
	///     option on an unordered call cannot express it.
	///     <para>
	///         <c>minimumLevel</c> is the floor for everything but the Microsoft namespaces, which stay at
	///         Warning. It defaults to Debug, as NetDaemon's own logging does.
	///     </para>
	/// </remarks>
	public static IHostBuilder UseIsoTimestampLogging(
		this IHostBuilder builder,
		LogEventLevel minimumLevel = LogEventLevel.Debug)
	{
		ArgumentNullException.ThrowIfNull(builder);

		// Levels are set here rather than read from configuration: this replaces the logger the host built from
		// Logging:LogLevel, and Serilog's own ReadFrom.Configuration looks for a "Serilog" section most hosts do
		// not have — leaving Information, and dropping every Debug line without saying so.
		return builder.UseSerilog((_, logger) => logger
			.MinimumLevel.Is(minimumLevel)
			.MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
			.Enrich.FromLogContext()
			.WriteTo.Console(
				outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
				theme: AnsiConsoleTheme.Code));
	}
}
