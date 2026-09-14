using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;

namespace AdaptiveLighting.Web.Services;

/// <summary>The configuration document as last written, parsed once per write for everything that only reads it.</summary>
/// <remarks>
///     Readers share the instance and must not edit it. An editor takes its own copy from
///     <see cref="LightingConfigStore.Load"/> and saves through the engine host.
/// </remarks>
public sealed class DocumentCache
{
	private readonly LightingEngineHost _engine;
	private readonly ILogger<DocumentCache> _logger;
	private readonly Lock _gate = new();

	private AdaptiveLightingConfig? _document;
	private DateTimeOffset? _stamp;

	public DocumentCache(LightingEngineHost engine, ILogger<DocumentCache> logger)
	{
		_engine = engine ?? throw new ArgumentNullException(nameof(engine));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>The document, re-read only when the store's last-write time has moved.</summary>
	/// <returns>
	///     The same instance until the file changes, so a caller can tell a new document by reference. The last good
	///     copy when a re-read does not parse, and <c>null</c> when there is no file or nothing has parsed yet.
	/// </returns>
	/// <remarks>The dashboard ticker reads this every second, so an unchanged file costs a stat and not a YAML parse.</remarks>
	public AdaptiveLightingConfig? Read()
	{
		if (!_engine.Store.Exists)
			return null;

		DateTimeOffset? stamp = _engine.Store.LastWrittenUtc;

		lock (_gate)
		{
			if (_document is not null && stamp == _stamp)
				return _document;

			try
			{
				_document = _engine.Store.Load();
				_stamp = stamp;
			}
			catch (LightingConfigException exception)
			{
				_logger.LogDebug(exception, "Could not re-read the lighting configuration; keeping the last good copy.");
			}

			return _document;
		}
	}
}
