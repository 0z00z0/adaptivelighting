using System.Reflection;

using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>What a setting's detail row draws, and so how its value is written, bounded and carried.</summary>
/// <remarks>
///     Every control here is constrained, so applying an edit the moment it is made is safe: no interaction can
///     produce a document the validator would refuse.
/// </remarks>
public enum RoomControl
{
	/// <summary>A span the schema stores in whole seconds.</summary>
	Seconds,

	/// <summary>A span the schema stores in whole minutes; the row converts.</summary>
	Minutes,

	/// <summary>A proportion the schema stores as a 0-1 factor, shown and stepped as a percentage.</summary>
	Fraction,

	Number,

	Flag,

	Choice,

	/// <summary>A rising ladder of named steps, where each step writes more than one flag at once.</summary>
	Steps,

	Entity
}

/// <summary>One overridable per-room setting, as the detail view needs it.</summary>
/// <remarks>
///     The labels and help lines are data, not markup, so the sentence layer and the detail layer cannot call one
///     setting two things. A setting <c>AppliesWhen</c> excludes is not drawn at all, never greyed out.
/// </remarks>
public sealed record RoomSetting(
	string Key,
	string Label,
	string Help,
	RoomControl Control,
	string Unit = "",
	double Step = 1,
	double Min = 0,
	double? Max = null,
	Func<AreaSettings, bool>? AppliesWhen = null,
	IReadOnlyList<string>? Folds = null)
{
	/// <summary>Every schema key this row writes: its own, then whatever its steps fold in.</summary>
	// A folded key has no row of its own, so this is what holds the sections and the schema to the same set.
	public IReadOnlyList<string> AllKeys => Folds is null ? [Key] : [Key, .. Folds];

	public bool AppliesTo(AreaSettings effective)
	{
		ArgumentNullException.ThrowIfNull(effective);

		return AppliesWhen is null || AppliesWhen(effective);
	}
}

/// <summary>What reading a typed number produced: the value to apply, or the sentence saying why nothing was.</summary>
/// <remarks>One of the two is always set, never both.</remarks>
public sealed record TypedNumber(double? Value, string? Refusal);

public sealed record RoomSettingGroup(string Title, string Note, IReadOnlyList<RoomSetting> Settings);

/// <summary>The model of the settings one room can override: which they are, what they are called, and how one is read and written.</summary>
/// <remarks>
///     Two surfaces read it: the room page against one <see cref="AreaConfig"/>, the House tab against the
///     document's <see cref="AreaSettings"/> defaults, hence every reader taking a nullable room and every writer
///     having a twin for the house. One key-to-property mapping and one set of unit conversions serve both, so a
///     warning dim cannot be stored as 50 by one surface and 0.5 by the other. The set of settings is derived by
///     reflection, never listed. Provenance is read off <c>null</c>, never guessed by comparing values: a room that
///     pins 10 min while the house also says 10 min has made a decision.
/// </remarks>
public static class RoomSettings
{
	private static readonly Dictionary<string, PropertyInfo> RoomProperties;
	private static readonly Dictionary<string, PropertyInfo> HouseProperties;
	private static readonly Dictionary<string, RoomSetting> ByKey;

	static RoomSettings()
	{
		RoomProperties = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
		HouseProperties = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

		List<string> keys = [];

		foreach (PropertyInfo house in typeof(AreaSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			// Enabled is not one of them: the room's power switch owns it, so it is never counted among the settings
			// the detail view offers.
			if (!house.CanRead || !house.CanWrite || string.Equals(house.Name, nameof(AreaSettings.Enabled), StringComparison.Ordinal))
				continue;

			PropertyInfo? room = typeof(AreaConfig).GetProperty(house.Name, BindingFlags.Public | BindingFlags.Instance);

			// A nullable twin, or nothing. AreaConfig's own members, such as the entity lists and the area id, are
			// per-room facts and never overrides.
			if (room is null || !room.CanRead || !room.CanWrite)
				continue;

			if ((Nullable.GetUnderlyingType(room.PropertyType) ?? room.PropertyType) != house.PropertyType)
				continue;

			RoomProperties[house.Name] = room;
			HouseProperties[house.Name] = house;
			keys.Add(house.Name);
		}

		Keys = keys;

		ByKey = Groups
			.SelectMany(group => group.Settings)
			.SelectMany(setting => setting.AllKeys.Select(key => (Key: key, Setting: setting)))
			.ToDictionary(pair => pair.Key, pair => pair.Setting, StringComparer.Ordinal);
	}

	/// <summary>The most a light-level setting takes: the largest illuminance a 16-bit sensor reading can carry.</summary>
	/// <remarks>Above it is a value no real sensor produces, so the field refuses it instead of clamping.</remarks>
	public const double MaxLux = 65535;

	/// <summary>What the movement section is called; the room page appends its "Blocked while on" control to it.</summary>
	/// <remarks>Matched by constant, never by literal, so a rename cannot drop the control.</remarks>
	public const string MovementSection = "Movement & timing";

	/// <summary>Every setting a room can state for itself, derived from the schema.</summary>
	/// <remarks>
	///     The denominator in "n of 22 are this room's own". Never write that number down:
	///     <see cref="AreaView.OverridableSettingCount"/> and this list are two readings of one fact, held together
	///     by a test.
	/// </remarks>
	public static IReadOnlyList<string> Keys { get; }

	/// <summary>How a room decides it is dark, worded for a segmented control.</summary>
	/// <remarks>The same thing <see cref="AreaSentences.DarknessChoices"/> says as prose.</remarks>
	public static IReadOnlyList<TokenChoice> DarknessOptions { get; } = TokenChoices.Of(
		("Sensor", nameof(DarknessSource.Lux)),
		("Sun", nameof(DarknessSource.Sun)),
		("Always dark", nameof(DarknessSource.Always)));

	/// <summary>What ends a manual hold: a clock, or the room going quiet.</summary>
	// Carried as bool.TrueString / bool.FalseString, which is what ChoiceName reads back off a boolean property,
	// so the tick, the readout and the write all compare the same words.
	public static IReadOnlyList<TokenChoice> HoldModeOptions { get; } = TokenChoices.Of(
		("For a set time", bool.FalseString),
		("Until the room empties", bool.TrueString));

	/// <summary>How the room's warmth is commanded; <c>Auto</c> reads the fixtures and needs no answer.</summary>
	public static IReadOnlyList<TokenChoice> ColorControlOptions { get; } = TokenChoices.Of(
		("Detect from the lights", nameof(ColorControl.Auto)),
		("Colour temperature", nameof(ColorControl.Kelvin)),
		("No colour temperature", nameof(ColorControl.EqualChannels)));

	public static IReadOnlyList<RoomSettingGroup> Groups { get; } =
	[
		new RoomSettingGroup(
			MovementSection,
			"How long the lights stay on, and what stops or overrules them",
			[
				new RoomSetting(
					nameof(AreaSettings.VacancyTimeoutSeconds),
					"Lights stay on for",
					"How long after the last movement the lights hold, before the warning dim starts. Raise it for rooms where people sit still; 10 min suits most.",
					RoomControl.Seconds, Step: 60, Min: 1),
				new RoomSetting(
					nameof(AreaSettings.PreOffBrightnessFactor),
					"Warning dim level",
					"How far the lights drop for the warning. 50 % is half the brightness the room was holding; lower makes the warning harder to miss.",
					RoomControl.Fraction, Step: 5, Min: 0, Max: 100),
				new RoomSetting(
					nameof(AreaSettings.PreOffSeconds),
					"Warning dim lasts",
					"How long the room sits dimmed before the lights go out. Any movement in that time brings them straight back. Must be shorter than the time above.",
					RoomControl.Seconds, Step: 5, Min: 0),
				new RoomSetting(
					nameof(AreaSettings.OverrideUntilVacant),
					"Manual changes hold",
					"What hands a light somebody set by hand back to the room. Until the room empties waits for the same quiet as “Lights stay on for”, so the setting stands while anyone is still there and goes as soon as they leave. For a set time hands it back on a clock instead, whoever is in the room.",
					RoomControl.Choice),
				new RoomSetting(
					nameof(AreaSettings.OverrideDurationMinutes),
					"Manual changes hold for",
					"How long a light somebody set manually is left alone before the room takes it back. Zero hands it back at the next re-check.",
					RoomControl.Minutes, Step: 15, Min: 0,
					AppliesWhen: settings => !settings.OverrideUntilVacant),
				new RoomSetting(
					nameof(AreaSettings.VacancyResetMinutes),
					"After switching off manually, wait",
					"How long the room must stay empty before movement lights it again. Without it, switching the lights off and walking out turns them straight back on.",
					RoomControl.Minutes, Step: 5, Min: 0)
			]),

		new RoomSettingGroup(
			"Darkness",
			"What counts as dark enough for movement to light the room",
			[
				new RoomSetting(
					nameof(AreaSettings.Darkness),
					"How the room decides it's dark",
					"What has to say dark before movement lights the room. Sensor never looks at the sun, and a room with no light-level sensor, or whose sensors have all stopped reporting, counts as dark and lights on movement. Sun reads the sun's height instead. Always dark skips the check, for a windowless room.",
					RoomControl.Choice),
				new RoomSetting(
					nameof(AreaSettings.LuxThreshold),
					"Dark below",
					"Under this many lux the room counts as dark. A room with no light-level sensor never reaches this test and simply counts as dark. A shaded outdoor sensor reads 1–3 lx at night and a few thousand by day, an indoor one far less — pick the decade first, then the number.",
					RoomControl.Number, Unit: "lx", Step: 1, Min: 0, Max: MaxLux,
					AppliesWhen: settings => settings.Darkness is DarknessSource.Lux),
				new RoomSetting(
					nameof(AreaSettings.LuxHysteresis),
					"Bright again needs another",
					"Added on top of Dark below: at 1000 lx and 10 lx the room counts as bright again only above 1010, so a sensor sitting on the line cannot flap. Scale it with the threshold — 10 lx is lost in the noise against 1000.",
					RoomControl.Number, Unit: "lx", Step: 1, Min: 0, Max: MaxLux,
					AppliesWhen: settings => settings.Darkness is DarknessSource.Lux),
				new RoomSetting(
					nameof(AreaSettings.SunElevationThreshold),
					"Dark when the sun is below",
					"How high the sun may stand and the room still count as dark, in degrees above the horizon. 0° is sunset, −6° is dusk.",
					RoomControl.Number, Unit: "°", Step: 1, Min: -90, Max: 90,

					// Only the rule that reads the sun: a Sensor room with no reading counts as dark outright, so
					// there is no sun fallback for this row to serve.
					AppliesWhen: settings => settings.Darkness is DarknessSource.Sun)
			]),

		new RoomSettingGroup(
			"Daylight curve",
			"The brightness the periods that follow the light outside are given",
			[
				new RoomSetting(
					nameof(AreaSettings.LuxBrightnessStartLux),
					"Dark end",
					"At or under this reading outside, the room holds the level below. 100 lx is a dull room.",
					RoomControl.Number, Unit: "lx", Step: 50, Min: 1),
				new RoomSetting(
					nameof(AreaSettings.LuxBrightnessMinPct),
					"Brightness at the dark end",
					"How bright the room is while it is dark outside. Nothing about the schedule limits it.",
					RoomControl.Number, Unit: "%", Step: 5, Min: 0, Max: 100),
				new RoomSetting(
					nameof(AreaSettings.LuxBrightnessFullLux),
					"Bright end",
					"At or over this reading the room is as bright as the curve takes it. 10 000 lx is a bright overcast day. Must be above the dark end.",
					RoomControl.Number, Unit: "lx", Step: 1000, Min: 1),
				new RoomSetting(
					nameof(AreaSettings.LuxBrightnessMaxPct),
					"Brightness at the bright end",
					"How bright the room is on a bright day. Set it under the dark end's level and the curve falls instead of rising.",
					RoomControl.Number, Unit: "%", Step: 5, Min: 0, Max: 100),
				new RoomSetting(
					nameof(AreaSettings.LuxBrightnessGamma),
					"Curve shape",
					"How the climb between the two ends is shaped. 1 rises steadily; above 1 holds back until it is properly bright out; below 1 moves the room as soon as the light outside starts climbing.",
					RoomControl.Number, Step: 0.1, Min: 0.1, Max: 5)
			]),

		new RoomSettingGroup(
			"Room behaviour",
			"What this room does when the house sleeps, empties or fills again",
			[
				new RoomSetting(
					nameof(AreaSettings.ColorControl),
					"How warmth reaches these lights",
					"Most lights take a colour temperature in kelvin. Plain dimmers and colour strips do not, and those are driven with every channel at one value, which is neutral white; the schedule's kelvin figure does nothing for them. Left to detect, this reads the room's own lights and needs no answer from you.",
					RoomControl.Choice),
				new RoomSetting(
					SleepSteps.Key,
					"While the house sleeps",
					"Normal treats the small hours like any other. Dims holds the room to the night period's limits, so a 03:00 glass of water gets a dim light. Dims and stays off also leaves movement unanswered — for the bedroom itself. The wall switch works at every step.",
					RoomControl.Steps,
					Folds: [SleepSteps.BlockKey]),
				new RoomSetting(
					nameof(AreaSettings.SkipAwaySweep),
					"Stays on when everyone leaves",
					"The lights-off sweep skips this room when the house empties. For porch and security lights, which are wanted when nobody is home.",
					RoomControl.Flag),
				new RoomSetting(
					nameof(AreaSettings.WelcomeHome),
					"Lights up when the first person comes home",
					"If the house is dark when somebody arrives, this room comes on to meet them instead of waiting for a motion sensor to catch them.",
					RoomControl.Flag)
			]),

		new RoomSettingGroup(
			"Rarely needed",
			"Fade lengths and the sun entity",
			[
				new RoomSetting(
					nameof(AreaSettings.DayTransitionSeconds),
					"Fade when it's light out",
					"How many seconds the lights take to reach a new level while the room is not dark. 1 s is brisk; 0 snaps.",
					RoomControl.Number, Unit: "s", Step: 0.5, Min: 0),
				new RoomSetting(
					nameof(AreaSettings.NightTransitionSeconds),
					"Fade when it's dark out",
					"The same, for a dark room. Longer than the daytime fade, because dark-adapted eyes notice a step. 15 s is easy on them.",
					RoomControl.Number, Unit: "s", Step: 0.5, Min: 0),
				new RoomSetting(
					nameof(AreaSettings.SunEntity),
					"Sun entity",
					"Which entity the sun's height is read from. A house has exactly one, so there is normally no reason to change this.",
					RoomControl.Entity)
			])
	];

	/// <summary>Whether a setting's range spans so many decades that no single step can serve it, so it is typed instead of stepped.</summary>
	public static bool SpansDecades(double min, double? max) =>
		max is { } ceiling && ceiling / Math.Max(min, 1) >= 1000;

	/// <summary>Reads a number somebody typed, in the unit the control shows.</summary>
	/// <remarks>
	///     Refuses out-of-range values instead of clamping them, so a rejected entry is distinguishable from a
	///     typo. Invariant first, then the machine's culture: a browser hands <c>type="number"</c> back in HTML
	///     syntax, while a paste or an autofill can arrive as "62,5" on an <c>nb-NO</c> host. The invariant pass
	///     must refuse thousands separators, or it reads "1,5" as fifteen before the Norwegian pass sees it.
	/// </remarks>
	public static TypedNumber ReadNumber(string? typed, double min, double? max, string unit = "")
	{
		string entered = typed?.Trim() ?? string.Empty;

		if (entered.Length == 0)
			return new TypedNumber(null, "Nothing was entered, so nothing changed.");

		if (!double.TryParse(entered, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
			&& !double.TryParse(entered, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value))
		{
			return new TypedNumber(null, $"“{entered}” is not a number, so nothing changed.");
		}

		if (value < min)
			return new TypedNumber(null, $"{TokenFormat.Number(min, unit)} is the least this takes, so {TokenFormat.Number(value, unit)} was not applied.");

		if (max is { } ceiling && value > ceiling)
			return new TypedNumber(null, $"{TokenFormat.Number(ceiling, unit)} is the most this takes, so {TokenFormat.Number(value, unit)} was not applied.");

		return new TypedNumber(value, null);
	}

	/// <exception cref="KeyNotFoundException">No group holds that key.</exception>
	public static RoomSetting Of(string key) => ByKey[key];

	public static bool Knows(string key) => key is not null && ByKey.ContainsKey(key);

	/// <summary>Whether this room states its own value for a setting instead of following the house.</summary>
	/// <remarks>
	///     Read off the schema's <c>null</c>, never by comparing against the house's number: two values being equal
	///     today says nothing about whether either was chosen.
	/// </remarks>
	public static bool IsOwn(AreaConfig room, string key)
	{
		ArgumentNullException.ThrowIfNull(room);

		return RoomProperties.TryGetValue(key, out PropertyInfo? property) && property.GetValue(room) is not null;
	}

	/// <summary>Whether a row is this room's own, counting every key the row writes.</summary>
	// The per-key reading stays the counting one: a stepped row owning two keys is two of the denominator, the
	// same as the two switches it replaced.
	public static bool IsOwn(AreaConfig room, RoomSetting setting)
	{
		ArgumentNullException.ThrowIfNull(setting);

		return setting.AllKeys.Any(key => IsOwn(room, key));
	}

	public static int OwnCount(AreaConfig room)
	{
		ArgumentNullException.ThrowIfNull(room);

		return Keys.Count(key => IsOwn(room, key));
	}

	/// <summary>Sends one setting back to following the house, clearing its property and any key its row folds in.</summary>
	/// <remarks>Never copies the house's current number in, which would pin the room to today's value.</remarks>
	public static bool Clear(AreaConfig room, string key)
	{
		ArgumentNullException.ThrowIfNull(room);

		if (!RoomProperties.TryGetValue(key, out PropertyInfo? property))
			return false;

		property.SetValue(room, null);

		// A stepped row leaving one of its flags pinned would read as the room still deciding its own night rule.
		if (ByKey.TryGetValue(key, out RoomSetting? setting))
		{
			foreach (string folded in setting.AllKeys)
			{
				if (RoomProperties.TryGetValue(folded, out PropertyInfo? twin))
					twin.SetValue(room, null);
			}
		}

		return true;
	}

	/// <summary>A numeric setting's value in the unit its control shows, following the room's inheritance.</summary>
	/// <remarks>
	///     The shown unit is not always the stored one: the schema keeps the warning dim as a 0-1 factor. The
	///     conversion lives here, once, so the stepper and the readout cannot disagree about what a number means.
	/// </remarks>
	public static double Shown(AreaConfig? room, AreaSettings defaults, string key)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		double raw = Convert.ToDouble(Effective(room, defaults, key), CultureInfo.InvariantCulture);

		return Of(key).Control == RoomControl.Fraction ? raw * 100 : raw;
	}

	/// <summary>Writes a numeric setting from a value given in the unit its control shows.</summary>
	/// <remarks>
	///     Bounded here, never by the control, so a value typed into the stepper's keyboard escape hatch is held to
	///     the same limits as one reached by pressing the arrows. Whole-number settings round away from zero.
	/// </remarks>
	public static void SetShown(AreaConfig room, string key, double shown)
	{
		ArgumentNullException.ThrowIfNull(room);

		RoomSetting setting = Of(key);
		double bounded = Math.Clamp(shown, setting.Min, setting.Max ?? double.MaxValue);
		double stored = setting.Control == RoomControl.Fraction ? bounded / 100 : bounded;

		PropertyInfo property = RoomProperties[key];
		Type underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

		property.SetValue(
			room,
			underlying == typeof(int)
				? (int)Math.Round(stored, MidpointRounding.AwayFromZero)
				: Convert.ChangeType(stored, underlying, CultureInfo.InvariantCulture));
	}

	public static bool Flag(AreaConfig? room, AreaSettings defaults, string key)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return Effective(room, defaults, key) is true;
	}

	public static void SetFlag(AreaConfig room, string key, bool value)
	{
		ArgumentNullException.ThrowIfNull(room);

		RoomProperties[key].SetValue(room, value);
	}

	public static string Entity(AreaConfig? room, AreaSettings defaults, string key)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return Effective(room, defaults, key) as string ?? string.Empty;
	}

	/// <summary>An empty pick clears the setting back to the house.</summary>
	public static void SetEntity(AreaConfig room, string key, string? value)
	{
		ArgumentNullException.ThrowIfNull(room);

		RoomProperties[key].SetValue(room, string.IsNullOrWhiteSpace(value) ? null : value);
	}

	public static string Describe(AreaConfig? room, AreaSettings defaults, string key)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		RoomSetting setting = Of(key);

		return setting.Control switch
		{
			RoomControl.Seconds => TokenFormat.Duration((int)Shown(room, defaults, key)),
			RoomControl.Minutes => TokenFormat.DurationFromMinutes((int)Shown(room, defaults, key)),
			RoomControl.Fraction => TokenFormat.Percent(Shown(room, defaults, key)),
			RoomControl.Number => TokenFormat.Number(Shown(room, defaults, key), setting.Unit),
			RoomControl.Flag => Flag(room, defaults, key) ? "yes" : "no",
			RoomControl.Steps => SleepSteps.Word(SleepSteps.Of(room, defaults)),
			RoomControl.Choice => Word(key, ChoiceName(room, defaults, key)),
			_ => Entity(room, defaults, key) is { Length: > 0 } entity ? entity : "none"
		};
	}

	/// <summary>The shortlist a choice-typed setting offers, keyed so each setting gets its own words.</summary>
	public static IReadOnlyList<TokenChoice> ChoicesFor(string key) =>
		key switch
		{
			nameof(AreaSettings.ColorControl) => ColorControlOptions,
			nameof(AreaSettings.OverrideUntilVacant) => HoldModeOptions,
			_ => DarknessOptions
		};

	/// <summary>The chosen member of an enum-valued setting, by name, following the room's inheritance.</summary>
	/// <remarks>The name, never the typed value, so one signature serves every enum-valued setting.</remarks>
	public static string ChoiceName(AreaConfig? room, AreaSettings defaults, string key)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return Effective(room, defaults, key)?.ToString() ?? "";
	}

	/// <summary>How a choice-typed setting's current value is worded.</summary>
	public static string Word(string key, string value) =>
		ChoicesFor(key).FirstOrDefault(option => string.Equals(option.Value, value, StringComparison.Ordinal))?.Text
		?? value;

	/// <summary>Applies one sentence-token edit to a room, in place.</summary>
	/// <remarks>
	///     A typed switch, never the reflection the rest of this class uses, because the conversions differ per key:
	///     a duration token carries seconds while the schema stores some in minutes, and a percentage token carries
	///     0-100 against a stored 0-1 factor. A generic path would write a ten-second timeout where ten minutes was
	///     asked for, with nothing on screen looking wrong.
	/// </remarks>
	public static bool Apply(AreaConfig room, SentenceEdit edit)
	{
		ArgumentNullException.ThrowIfNull(room);
		ArgumentNullException.ThrowIfNull(edit);

		switch (edit.Key)
		{
			case nameof(AreaSettings.VacancyTimeoutSeconds):
				room.VacancyTimeoutSeconds = edit.Seconds;
				return true;

			case nameof(AreaSettings.PreOffSeconds):
				room.PreOffSeconds = edit.Seconds;
				return true;

			case nameof(AreaSettings.PreOffBrightnessFactor):
				room.PreOffBrightnessFactor = edit.Fraction;
				return true;

			case nameof(AreaSettings.OverrideDurationMinutes):
				room.OverrideDurationMinutes = edit.Minutes;
				return true;

			case nameof(AreaSettings.OverrideUntilVacant):
				room.OverrideUntilVacant = edit.Flag;
				return true;

			case nameof(AreaSettings.VacancyResetMinutes):
				room.VacancyResetMinutes = edit.Minutes;
				return true;

			case nameof(AreaSettings.LuxThreshold):
				room.LuxThreshold = edit.Number;
				return true;

			case nameof(AreaSettings.SunElevationThreshold):
				room.SunElevationThreshold = edit.Number;
				return true;

			case nameof(AreaSettings.Darkness):
				if (!edit.TryEnum(out DarknessSource source))
					return false;

				room.Darkness = source;
				return true;

			case nameof(AreaSettings.ColorControl):
				if (!edit.TryEnum(out ColorControl roomColor))
					return false;

				room.ColorControl = roomColor;
				return true;

			default:
				return false;
		}
	}

	// The house's own copy of the same settings. The House tab edits AreaSettings directly: these values ARE the
	// house, so there is no null meaning "inherit" and no road back. The metadata is the same the room page reads,
	// so a change to a bound or a unit lands on both surfaces at once.

	/// <summary>By <see cref="SetShown(AreaConfig, string, double)"/>'s rules, always writing a value: the house has nothing above it to fall back to.</summary>
	public static void SetShown(AreaSettings house, string key, double shown)
	{
		ArgumentNullException.ThrowIfNull(house);

		RoomSetting setting = Of(key);
		double bounded = Math.Clamp(shown, setting.Min, setting.Max ?? double.MaxValue);
		double stored = setting.Control == RoomControl.Fraction ? bounded / 100 : bounded;

		PropertyInfo property = HouseProperties[key];

		property.SetValue(
			house,
			property.PropertyType == typeof(int)
				? (int)Math.Round(stored, MidpointRounding.AwayFromZero)
				: Convert.ChangeType(stored, property.PropertyType, CultureInfo.InvariantCulture));
	}

	public static void SetFlag(AreaSettings house, string key, bool value)
	{
		ArgumentNullException.ThrowIfNull(house);

		HouseProperties[key].SetValue(house, value);
	}

	/// <summary>Sets a choice-typed setting from the token a choice button carries.</summary>
	/// <remarks>Parsed against the property's own type, never a named enum, so each setting writes its own kind.</remarks>
	// The boolean arm is not optional tidiness: Enum.TryParse throws on a non-enum type, so a two-option choice
	// stored as a bool would take the whole House tab down on the first click.
	public static void SetChoice(AreaSettings house, string key, string value)
	{
		ArgumentNullException.ThrowIfNull(house);

		PropertyInfo property = HouseProperties[key];

		if (property.PropertyType == typeof(bool))
		{
			if (bool.TryParse(value, out bool flag))
				property.SetValue(house, flag);

			return;
		}

		if (Enum.TryParse(property.PropertyType, value, out object? parsed))
			property.SetValue(house, parsed);
	}

	/// <summary>An empty pick is stored as an empty string, which is what the engine reads as "nothing chosen".</summary>
	public static void SetEntity(AreaSettings house, string key, string? value)
	{
		ArgumentNullException.ThrowIfNull(house);

		HouseProperties[key].SetValue(house, value ?? string.Empty);
	}

	/// <summary>The same typed conversions <see cref="Apply(AreaConfig, SentenceEdit)"/> makes.</summary>
	/// <remarks>The two must stay in step, or one value picked in two places lands in the document meaning different things.</remarks>
	public static bool Apply(AreaSettings house, SentenceEdit edit)
	{
		ArgumentNullException.ThrowIfNull(house);
		ArgumentNullException.ThrowIfNull(edit);

		switch (edit.Key)
		{
			case nameof(AreaSettings.VacancyTimeoutSeconds):
				house.VacancyTimeoutSeconds = edit.Seconds;
				return true;

			case nameof(AreaSettings.PreOffSeconds):
				house.PreOffSeconds = edit.Seconds;
				return true;

			case nameof(AreaSettings.PreOffBrightnessFactor):
				house.PreOffBrightnessFactor = edit.Fraction;
				return true;

			case nameof(AreaSettings.OverrideDurationMinutes):
				house.OverrideDurationMinutes = edit.Minutes;
				return true;

			case nameof(AreaSettings.OverrideUntilVacant):
				house.OverrideUntilVacant = edit.Flag;
				return true;

			case nameof(AreaSettings.VacancyResetMinutes):
				house.VacancyResetMinutes = edit.Minutes;
				return true;

			case nameof(AreaSettings.LuxThreshold):
				house.LuxThreshold = edit.Number;
				return true;

			case nameof(AreaSettings.SunElevationThreshold):
				house.SunElevationThreshold = edit.Number;
				return true;

			case nameof(AreaSettings.Darkness):
				if (!edit.TryEnum(out DarknessSource source))
					return false;

				house.Darkness = source;
				return true;

			case nameof(AreaSettings.ColorControl):
				if (!edit.TryEnum(out ColorControl houseColor))
					return false;

				house.ColorControl = houseColor;
				return true;

			default:
				return false;
		}
	}

	private static object? Effective(AreaConfig? room, AreaSettings defaults, string key)
	{
		object? own = room is not null && RoomProperties.TryGetValue(key, out PropertyInfo? property)
			? property.GetValue(room)
			: null;

		return own ?? HouseProperties[key].GetValue(defaults);
	}
}
