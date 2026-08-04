using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Hosting;

using Microsoft.Extensions.Hosting;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     Files the engine's own house-wide notices in the activity record: it started, or a save rebuilt every room.
/// </summary>
/// <remarks>
///     In process, where every other entry in the record round-trips through Home Assistant as an area event. A
///     rebuild is not something an area reported, and routing it through the areas would put one row per room in
///     the record for a single save.
/// </remarks>
public sealed class EngineNoticeRecorder : IHostedService, IDisposable
{
	private readonly LightingEngineHost _engine;
	private readonly ActivityLog _activity;
	private readonly ILogger<EngineNoticeRecorder> _logger;

	private IDisposable? _subscription;

	public EngineNoticeRecorder(LightingEngineHost engine, ActivityLog activity, ILogger<EngineNoticeRecorder> logger)
	{
		_engine = engine ?? throw new ArgumentNullException(nameof(engine));
		_activity = activity ?? throw new ArgumentNullException(nameof(activity));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_subscription = _engine.Notices.SubscribeSafe(Record, _logger);
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		Dispose();
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		_subscription?.Dispose();
		_subscription = null;
	}

	private void Record(EngineNotice notice) => _activity.Record(notice);
}
