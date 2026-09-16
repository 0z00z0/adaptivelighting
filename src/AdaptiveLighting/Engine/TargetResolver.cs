using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>What an area is aiming at now: the room's own answer, and one per light that states its own.</summary>
// Lights is empty for a room with no per-light levels, so the fan-out commands its entries as a whole.
internal sealed record AreaTargets(LightTarget Room, IReadOnlyDictionary<string, LightTarget> Lights);

/// <summary>Turns an instant into this area's levels, and a level into the command that carries it.</summary>
// Keeps nothing of the area's own: every answer is resolved for the instant and the house state it is handed,
// so the caller's lock is the only one. Called only from inside AreaController's lock.
internal sealed class TargetResolver
{
	private readonly ResolvedArea _area;
	private readonly GlobalConfig _global;
	private readonly IReadOnlyList<TimePeriodConfig> _periods;
	private readonly CircadianCalculator _circadian;

	// One per light that states levels of its own, built on that light's rows merged onto the room's. Empty for
	// every room that states nothing per light, which is the whole of the safety property.
	private readonly IReadOnlyDictionary<string, CircadianCalculator> _lightCalculators;

	private readonly LuxBrightnessCurve _luxBrightness;

	// The fade length, which the area picks from the darkness verdict it last read, so the caller refreshes
	// that verdict before it asks for a command.
	private readonly Func<double> _transitionSeconds;

	private readonly ILogger _logger;
	private readonly string _name;

	public TargetResolver(
		ResolvedArea area,
		GlobalConfig global,
		IReadOnlyList<TimePeriodConfig> periods,
		CircadianCalculator circadian,
		IReadOnlyDictionary<string, CircadianCalculator> lightCalculators,
		LuxBrightnessCurve luxBrightness,
		Func<double> transitionSeconds,
		ILogger logger)
	{
		_area = area;
		_global = global;
		_periods = periods;
		_circadian = circadian;
		_lightCalculators = lightCalculators;
		_luxBrightness = luxBrightness;
		_transitionSeconds = transitionSeconds;
		_logger = logger;
		_name = area.Name;
	}

	/// <summary>When the schedule next changes what this area is aiming at.</summary>
	public DateTimeOffset? NextBoundary(DateTimeOffset now) => _circadian.NextBoundary(now);

	/// <summary>The period resolving at <paramref name="now"/>, before any shaping.</summary>
	public LightTarget? PeriodAt(DateTimeOffset now) => _circadian.GetTarget(now);

	/// <summary>The levels a named period holds for the room, or <c>null</c> when the schedule no longer has it.</summary>
	public LightTarget? PeriodTarget(string periodKey) => _circadian.GetPeriodTarget(periodKey);

	/// <summary>The same for one light, from its own levels where it states them.</summary>
	public LightTarget? PeriodTarget(string lightEntityId, string periodKey) =>
		(_lightCalculators.TryGetValue(lightEntityId, out CircadianCalculator? own) ? own : _circadian)
			.GetPeriodTarget(periodKey);

	/// <summary>The room's target and one for every light that states levels of its own.</summary>
	// The daylight curve runs before the sleep clamp, so a bright reading during an afternoon nap cannot lift
	// the room past the night rules. Each light is shaped as the room is, so no light can climb past them by
	// stating a level. period is the unshaped room target, which the caller keeps for the snapshot.
	public AreaTargets? Resolve(DateTimeOffset now, HouseState house, out LightTarget? period)
	{
		period = _circadian.GetTarget(now);

		if (period is null)
		{
			_logger.LogWarning("{Area}: no circadian period resolves at {Now}; commanding nothing.", _name, now);
			return null;
		}

		LightTarget room = Shape(period, house);

		if (_lightCalculators.Count == 0)
			return new AreaTargets(room, NoLightTargets);

		Dictionary<string, LightTarget> lights = new(StringComparer.OrdinalIgnoreCase);

		foreach ((string light, CircadianCalculator calculator) in _lightCalculators)
			if (calculator.GetTarget(now) is { } own)
				lights[light] = Shape(own, house);

		return new AreaTargets(room, lights);
	}

	private static readonly IReadOnlyDictionary<string, LightTarget> NoLightTargets =
		new Dictionary<string, LightTarget>(StringComparer.OrdinalIgnoreCase);

	/// <summary>What this area's fixtures are told to be for <paramref name="target"/>, an off where it is nothing.</summary>
	// Composed here, not per service call: the fixtures were read once when the area resolved. A room whose
	// lights offer no colour at all takes neither field, so it is commanded on brightness alone. The single place
	// a level is turned into a command, room and light alike, and the last one: the curve, the warning dim and
	// the sleep cap have all had their say by the time it runs.
	public LightCommand TargetCommand(LightTarget target, double brightnessFactor)
	{
		double brightness = target.Clamp(target.BrightnessPct * brightnessFactor);

		// A level landing on raw 0 goes out as an off. Home Assistant carries out a turn-on at nothing as a
		// turn-off, so an on-expectation would go unmatched and the area would read its own work as a hand at
		// the switch.
		if (RawBrightness.FromPercent(brightness) <= 0)
			return LightCommand.TurnOff(_transitionSeconds());

		bool equalChannels = _area.CommandsColour && _area.EffectiveColorControl is ColorControl.EqualChannels;

		return new(
			true,
			brightness,
			_area.CommandsKelvin ? target.ColorTempKelvin : null,
			_transitionSeconds(),
			equalChannels);
	}

	/// <summary>What a period named by hand is put on a fixture as.</summary>
	// The curve, and no sleep clamp: a test is somebody asking what a period looks like.
	public LightCommand PeriodCommand(LightTarget target) =>
		TargetCommand(_luxBrightness.Apply(target), brightnessFactor: 1.0);

	/// <summary>What each light that states its own levels is told to be, or <c>null</c> when none does.</summary>
	public IReadOnlyDictionary<string, LightCommand>? LightCommands(AreaTargets targets, double brightnessFactor)
	{
		if (targets.Lights.Count == 0)
			return null;

		Dictionary<string, LightCommand> commands = new(StringComparer.OrdinalIgnoreCase);

		foreach ((string light, LightTarget target) in targets.Lights)
			commands[light] = TargetCommand(target, brightnessFactor);

		return commands;
	}

	/// <summary>The same, for a period named by hand instead of the one in force.</summary>
	public IReadOnlyDictionary<string, LightCommand>? LightCommandsFor(string periodKey)
	{
		if (_lightCalculators.Count == 0)
			return null;

		Dictionary<string, LightCommand> commands = new(StringComparer.OrdinalIgnoreCase);

		foreach ((string light, CircadianCalculator calculator) in _lightCalculators)
			if (calculator.GetPeriodTarget(periodKey) is { } target)
				commands[light] = PeriodCommand(target);

		return commands.Count > 0 ? commands : null;
	}

	private LightTarget Shape(LightTarget target, HouseState house)
	{
		LightTarget adjusted = _luxBrightness.Apply(target);

		return house.ActiveKind == ModeKind.Sleep && _area.Settings.RespectSleepMode
			? ClampToSleepCaps(adjusted, house)
			: adjusted;
	}

	/// <summary>Holds <paramref name="target"/> to the level of the sleep period the active sleep option names.</summary>
	// Somebody up at 03:00 is in the same night as the sleep-clamp period whether or not the clock has rolled
	// over to morning.
	private LightTarget ClampToSleepCaps(LightTarget target, HouseState house)
	{
		// The option actually in force, which an overlay entity can set without the select moving at all. Reading
		// the select's own value instead resolves a different option's clamp chain, and resolves nothing when the
		// select is unavailable, which leaves a bedroom on the evening's level all night.
		string? inForce = house.Forced?.OptionValue is { Length: > 0 } forced ? forced : house.ModeValue;

		HouseModeOptionConfig? option = _global.HouseMode?.OptionFor(inForce);
		TimePeriodConfig? clampPeriod = option is not null ? HouseModeConfig.SleepClampPeriodFor(option, _periods) : null;
		LightTarget? sleepPeriod = clampPeriod is not null ? _circadian.GetPeriodTarget(clampPeriod.Key) : null;
		if (sleepPeriod is null)
		{
			_logger.LogWarning("{Area} respects sleep mode but no clamp period resolves ('{Period}'); leaving the target alone.",
				_name, clampPeriod?.Name ?? "(none)");
			return target;
		}

		// The clamp period's own brightness is the ceiling, and where that period runs the curve the curve owns
		// the number. Reading it unresolved makes the stored percentage, inert everywhere else, the night's cap.
		// The clamp still runs last, on a target the curve has already set; only what it reads for the cap moved.
		double ceiling = _luxBrightness.Apply(sleepPeriod).BrightnessPct;

		return target with { BrightnessPct = target.Clamp(Math.Min(target.BrightnessPct, ceiling)) };
	}
}
