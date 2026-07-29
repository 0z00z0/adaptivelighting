using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Ha;

/// <summary>
///     Publishes area snapshots as Home Assistant events, and logs them.
/// </summary>
/// <remarks>
///     An HA event is enough to build an automation or a dashboard on, and costs nothing when nobody listens.
///     A per-area MQTT entity would be friendlier to the HA UI and is the obvious next step — which is the
///     whole reason this sits behind <see cref="IStatePublisher"/> rather than inside the state machine.
/// </remarks>
public sealed class HaStatePublisher : IStatePublisher
{
	/// <summary>The event type area snapshots are published under.</summary>
	public const string EventType = "adaptive_lighting_area";

	private readonly IHaContext _ha;
	private readonly ILogger _logger;

	/// <summary>Creates a publisher.</summary>
	/// <param name="ha">Where the events go.</param>
	/// <param name="logger">The second sink: the log line is what you read when the event bus is not open.</param>
	public HaStatePublisher(IHaContext ha, ILogger logger)
	{
		_ha = ha ?? throw new ArgumentNullException(nameof(ha));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <inheritdoc/>
	public void Publish(AreaSnapshot snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		_logger.LogInformation(
			"Area {Area} is {State} ({Reason}); house {Mode}, dark {IsDark}, period {Period}, brightness {Brightness}, kelvin {Kelvin}.",
			snapshot.AreaName, snapshot.State, snapshot.Reason, snapshot.Mode, snapshot.IsDark,
			snapshot.PeriodName, snapshot.BrightnessPct, snapshot.ColorTempKelvin);

		// Called from inside an area's lock, so a throw here would take the area's thread with it. There is
		// nothing useful to do about a failed event either way: the log line above already carries the news.
		try
		{
			_ha.SendEvent(EventType, new
			{
				area = snapshot.AreaName,
				// Additive: a consumer that never learned about area_id keeps reading `area` exactly as before.
				area_id = snapshot.AreaId,
				state = snapshot.State.ToString(),
				reason = snapshot.Reason.ToString(),
				mode = snapshot.Mode.ToString(),
				house_mode_value = snapshot.HouseModeValue,
				kill_switch_active = snapshot.KillSwitchActive,
				is_dark = snapshot.IsDark,
				period = snapshot.PeriodName,
				brightness_pct = snapshot.BrightnessPct,
				color_temp_kelvin = snapshot.ColorTempKelvin,
				timestamp = snapshot.Timestamp,
				last_command_at = snapshot.LastCommandAt,
				last_motion_at = snapshot.LastMotionAt,
				next_change_at = snapshot.NextChangeAt,
				next_change_from = snapshot.NextChangeFrom,
				darkness_detail = snapshot.DarknessDetail,
				// Additive, exactly as area_id was: a consumer that never learned about the auto-on gate reads
				// every field it already knew unchanged, and sees them as absent rather than as "nothing blocks".
				auto_on_blocked_by = snapshot.AutoOnBlockedBy?.ToString(),
				auto_on_blocking_entity = snapshot.AutoOnBlockingEntity,
				// Additive again: which of this room's levels it names for itself during this period, absent on a
				// consumer's side rather than reading as "none" if they never learned about it.
				levels_from_room = snapshot.LevelsFromRoom?.ToString()
			});
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Could not publish the snapshot for area {Area}.", snapshot.AreaName);
		}
	}
}
