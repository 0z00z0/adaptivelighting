using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Ha;

/// <summary>Publishes area snapshots as Home Assistant events, and logs them.</summary>
public sealed class HaStatePublisher : IStatePublisher
{
	public const string EventType = "adaptive_lighting_area";

	private readonly IHaContext _ha;
	private readonly ILogger _logger;

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

		// Called from inside an area's lock, so a throw here takes the area's thread with it.
		try
		{
			// Fields here are only ever added, never renamed or removed: consumers bind them by name.
			_ha.SendEvent(EventType, new
			{
				area = snapshot.AreaName,
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
				auto_on_blocked_by = snapshot.AutoOnBlockedBy?.ToString(),
				auto_on_blocking_entity = snapshot.AutoOnBlockingEntity,
				is_held_lit = snapshot.IsHeldLit,
				held_lit_by = snapshot.HeldLitBy,
				scene_applied = snapshot.SceneApplied,
				testing_period_id = snapshot.TestingPeriodId,
				test_ends_at = snapshot.TestEndsAt,
				levels_from_room = snapshot.LevelsFromRoom?.ToString(),
				// Flat fields, so an automation trigger can read one without walking an object.
				is_anyone_home = snapshot.IsAnyoneHome,
				mode_forced_kind = snapshot.Forced?.Kind.ToString(),
				mode_forced_option = snapshot.Forced?.OptionValue,
				mode_forced_source = snapshot.Forced?.Source.ToString(),
				mode_forced_by = snapshot.Forced?.EntityId,
				mode_forced_by_state = snapshot.Forced?.EntityState
			});
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Could not publish the snapshot for area {Area}.", snapshot.AreaName);
		}
	}
}
