using System.Globalization;
using System.Text;

using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace AdaptiveLighting.NetDaemon;

/// <summary>Writes each log event to the durable copy, rendering it from the template instead of the message.</summary>
/// <remarks>
///     <para>
///         <c>LogEvent.RenderMessage</c> is never called: it hands back the interpolated string, secrets and all.
///         Rendering from <see cref="MessageTemplate.Tokens"/> keeps the literal halves and the runtime values
///         apart, so every value goes through <see cref="LoggedValue"/> before it is joined to anything, and no
///         caller has a way in that skips the filter.
///     </para>
///     <para>
///         A property's format specifier and alignment are dropped: honouring them would run an unseen formatter
///         on a value this type is trying to bound.
///     </para>
/// </remarks>
public sealed class CircularLogSink : ILogEventSink
{
	/// <summary>Full ISO date, and the offset with it: this file is read days later and across midnight.</summary>
	public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fffzzz";

	/// <summary>How much of an exception a line will carry, stack trace included.</summary>
	public const int MaxExceptionLength = 2000;

	private const string SourceContextProperty = "SourceContext";
	private const int MaxTemplateTextLength = 1024;

	private readonly CircularLogWriter _writer;

	public CircularLogSink(CircularLogWriter writer) =>
		_writer = writer ?? throw new ArgumentNullException(nameof(writer));

	public void Emit(LogEvent logEvent)
	{
		ArgumentNullException.ThrowIfNull(logEvent);

		_writer.Append(Render(logEvent));
	}

	/// <summary>The one line <paramref name="logEvent"/> becomes.</summary>
	public static string Render(LogEvent logEvent)
	{
		ArgumentNullException.ThrowIfNull(logEvent);

		StringBuilder line = new(256);

		line.Append(logEvent.Timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture))
			.Append(' ')
			.Append(Abbreviate(logEvent.Level))
			.Append(' ')
			.Append(SourceContext(logEvent))
			.Append(" | ");

		foreach (MessageTemplateToken token in logEvent.MessageTemplate.Tokens)
			line.Append(Rendered(token, logEvent));

		if (logEvent.Exception is { } exception)
			line.Append(" | ").Append(LoggedValue.Text(exception.ToString(), MaxExceptionLength));

		return line.ToString();
	}

	private static string Rendered(MessageTemplateToken token, LogEvent logEvent) =>
		token switch
		{
			PropertyToken property => logEvent.Properties.TryGetValue(property.PropertyName, out LogEventPropertyValue? value)
				? LoggedValue.Of(property.PropertyName, value)
				: "{" + property.PropertyName + "}",
			TextToken text => LoggedValue.Text(text.Text, MaxTemplateTextLength),
			_ => string.Empty
		};

	private static string SourceContext(LogEvent logEvent) =>
		logEvent.Properties.TryGetValue(SourceContextProperty, out LogEventPropertyValue? value)
			? LoggedValue.Of(SourceContextProperty, value)
			: "-";

	// Serilog's own {Level:u3}, so the two logs read the same way.
	private static string Abbreviate(LogEventLevel level) =>
		level switch
		{
			LogEventLevel.Verbose => "VRB",
			LogEventLevel.Debug => "DBG",
			LogEventLevel.Information => "INF",
			LogEventLevel.Warning => "WRN",
			LogEventLevel.Error => "ERR",
			_ => "FTL"
		};
}
