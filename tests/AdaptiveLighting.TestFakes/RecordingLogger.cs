using Microsoft.Extensions.Logging;

namespace AdaptiveLighting.TestFakes;

/// <summary>Captures everything logged at warning or above.</summary>
public sealed class RecordingLogger : ILogger
{
	private readonly List<string> _warnings = [];

	public IReadOnlyList<string> Warnings => _warnings;

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(
		LogLevel logLevel,
		EventId eventId,
		TState state,
		Exception? exception,
		Func<TState, Exception?, string> formatter)
	{
		ArgumentNullException.ThrowIfNull(formatter);

		if (logLevel >= LogLevel.Warning)
			_warnings.Add(formatter(state, exception));
	}
}
