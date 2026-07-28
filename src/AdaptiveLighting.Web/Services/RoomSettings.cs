using System.Reflection;

using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     What a setting's detail row draws — and therefore how its value is written, bounded and carried.
/// </summary>
/// <remarks>
///     The kind is the contract between the metadata and the one row renderer. Every control in this design is
///     constrained (a stepper in the field's own grain, a segmented choice, a switch), which is what makes
///     applying an edit the moment it is made safe: no interaction here can produce a document the validator
///     would refuse.
/// </remarks>
public enum RoomControl
{
	/// <summary>A span the schema stores in whole seconds. Written "30 s", "10 min".</summary>
	Seconds,

	/// <summary>A span the schema stores in whole minutes. Written the same way; the row converts.</summary>
	Minutes,

	/// <summary>A proportion the schema stores as a 0-1 factor. Shown and stepped as a percentage.</summary>
	Fraction,

	/// <summary>A quantity with a unit — lux, degrees, percent, seconds of fade.</summary>
	Number,

	/// <summary>A yes/no. Drawn as a switch, because the shape is the meaning.</summary>
	Flag,

	/// <summary>One of a fixed set. Drawn as a segmented control, so every option is visible without a tap.</summary>
	Choice,

	/// <summary>A Home Assistant entity id, picked from what the registry knows.</summary>
	Entity
}

/// <summary>
///     One overridable per-room setting, as the detail view needs it.
/// </summary>
/// <remarks>
///     <para>
///         The labels and help lines are <c>area-restructure.md</c> §3's, which were written and reviewed for a
///         reader who does not know the vocabulary. They are data rather than markup so the same words can be
///         asserted on, and so the sentence layer and the detail layer can never call one setting two things.
///     </para>
///     <para>
///         <see cref="AppliesWhen"/> is the design's <i>When</i> rule: a setting that cannot take effect is not
///         drawn at all rather than greyed out. Greying still spends the reader's attention, still invites the
///         tap, and still has to explain itself; the row simply comes back on the tap that turns its gate on.
///     </para>
/// </remarks>
/// <param name="Key">The <see cref="AreaSettings"/> property name — the same key the sentence tokens carry.</param>
/// <param name="Label">The setting's name in the words the sentences use.</param>
/// <param name="Help">One line on what it changes. Says what happens, not what the field is.</param>
/// <param name="Control">How the row draws it.</param>
/// <param name="Unit">The unit written after the value, for <see cref="RoomControl.Number"/>.</param>
/// <param name="Step">How far one press of the stepper moves it, in the shown unit.</param>
/// <param name="Min">The lowest value the stepper will reach, in the shown unit.</param>
/// <param name="Max">The highest, or <c>null</c> for unbounded above.</param>
/// <param name="AppliesWhen">
///     Whether this setting can take effect at all, read off the room's effective settings. <c>null</c> means
///     always.
/// </param>
public sealed record RoomSetting(
	string Key,
	string Label,
	string Help,
	RoomControl Control,
	string Unit = "",
	double Step = 1,
	double Min = 0,
	double? Max = null,
	Func<AreaSettings, bool>? AppliesWhen = null)
{
	/// <summary>Whether this setting can take effect given what the room is otherwise set to.</summary>
	/// <param name="effective">The room's settings with its inheritance already resolved.</param>
	/// <exception cref="ArgumentNullException"><paramref name="effective"/> is <c>null</c>.</exception>
	public bool AppliesTo(AreaSettings effective)
	{
		ArgumentNullException.ThrowIfNull(effective);

		return AppliesWhen is null || AppliesWhen(effective);
	}
}

/// <summary>
///     What reading a typed number produced: the value to apply, or the sentence saying why nothing was.
/// </summary>
/// <remarks>
///     Both fields can be absent at once only in principle — a reading either yields a number or explains itself,
///     so the reader is never left with a field that changed nothing and said nothing about it.
/// </remarks>
/// <param name="Value">The number to apply, or <c>null</c> when nothing should change.</param>
/// <param name="Refusal">
///     Why nothing changed, in words meant for the person who typed it, or <c>null</c> when a value came through.
/// </param>
public sealed record TypedNumber(double? Value, string? Refusal);

/// <summary>
///     One named section of the detail view: the unit a person navigates by.
/// </summary>
/// <param name="Title">What the section is called.</param>
/// <param name="Note">One line naming what is inside, so a collapsed section still says what it holds.</param>
/// <param name="Settings">Its settings, in reading order.</param>
/// <param name="StartsOpen">
///     Whether it is open when the detail view is first revealed. The rare sections start closed — the design's
///     one sanctioned answer to a long page is to put the rare things behind a fold.
/// </param>
public sealed record RoomSettingGroup(string Title, string Note, IReadOnlyList<RoomSetting> Settings, bool StartsOpen);

/// <summary>
///     The model of the settings one room can override: which they are, what they are called, how they are
///     grouped, and how one is read, written and sent back to following the house.
/// </summary>
/// <remarks>
///     <para>
///         Two surfaces read it. The room page renders these settings against one <see cref="AreaConfig"/>; the
///         House tab renders the very same list against the document's <see cref="AreaSettings"/> defaults —
///         which is why every reader takes a nullable room and every writer has a twin that takes the house
///         instead. Splitting the two into separate classes would duplicate the key-to-property mapping and the
///         unit conversions, and a house that wrote a warning dim as 50 while a room wrote it as 0.5 is exactly
///         the drift this class exists to prevent.
///     </para>
///     <para>
///         <b>The set of settings is derived, not listed.</b> <see cref="Keys"/> is reflection over the nullable
///         twins <see cref="AreaConfig"/> declares for <see cref="AreaSettings"/> properties, so a setting added
///         to the schema is a setting this page counts from the moment it exists. A hand-written list is how the
///         shipped editor came to say "n of 16" about a document with twenty-one overridable settings, quietly
///         under-reporting five of them.
///     </para>
///     <para>
///         <b>Provenance is read off <c>null</c>, never guessed by comparing values.</b> A room that pins 10 min
///         while the house also says 10 min has made a decision — one taken precisely so a later change to the
///         house leaves this room alone — and the amber dot must say so.
///     </para>
///     <para>
///         Pure, because this repo has no Razor render harness and does not gain one: the grouping, the count,
///         the inherited-versus-own determination and every value format are asserted here rather than
///         screenshotted.
///     </para>
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
			// Enabled is deliberately not one of them: the room's power switch owns it, so it is not a setting the
			// detail view offers and must not be counted among the ones it does.
			if (!house.CanRead || !house.CanWrite || string.Equals(house.Name, nameof(AreaSettings.Enabled), StringComparison.Ordinal))
				continue;

			PropertyInfo? room = typeof(AreaConfig).GetProperty(house.Name, BindingFlags.Public | BindingFlags.Instance);

			// A twin, or nothing. AreaConfig carries members of its own — the explicit entity lists, the area id —
			// and those are per-room facts rather than overrides of a house setting.
			if (room is null || !room.CanRead || !room.CanWrite)
				continue;

			if ((Nullable.GetUnderlyingType(room.PropertyType) ?? room.PropertyType) != house.PropertyType)
				continue;

			RoomProperties[house.Name] = room;
			HouseProperties[house.Name] = house;
			keys.Add(house.Name);
		}

		Keys = keys;
		ByKey = Groups.SelectMany(group => group.Settings).ToDictionary(setting => setting.Key, StringComparer.Ordinal);
	}

	/// <summary>
	///     The most a light-level setting takes: the largest illuminance a 16-bit sensor reading can carry.
	/// </summary>
	/// <remarks>
	///     A bound taken from the hardware rather than from taste. Real sensors report 0–65 535 lx, so a number
	///     above it is not a preference this UI should quietly reshape — it is a value nothing will ever produce,
	///     and saying so is more use than clamping it to something the reader did not type.
	/// </remarks>
	public const double MaxLux = 65535;

	/// <summary>
	///     Every setting a room can state for itself, derived from the schema.
	/// </summary>
	/// <remarks>
	///     The denominator in "n of 21 settings are this room's own". Never write the number down anywhere:
	///     <see cref="AreaView.OverridableSettingCount"/> and this list are two readings of one fact, and a test
	///     holds them together.
	/// </remarks>
	public static IReadOnlyList<string> Keys { get; }

	/// <summary>
	///     How a room decides it is dark, worded for a segmented control rather than for a sentence.
	/// </summary>
	/// <remarks>
	///     Shorter words than <see cref="AreaSentences.DarknessChoices"/> on purpose: a segment is a button and
	///     "either the sensor or the sun" does not fit on one. The sentence and the row therefore read differently
	///     and mean identically, which is the right way round — the sentence is prose and the row is a control.
	/// </remarks>
	public static IReadOnlyList<TokenChoice> DarknessOptions { get; } = TokenChoices.Of(
		("Sensor", nameof(DarknessSource.Lux)),
		("Sun", nameof(DarknessSource.Sun)),
		("Either", nameof(DarknessSource.Either)),
		("Always dark", nameof(DarknessSource.Always)));

	/// <summary>
	///     The detail view, in sections, in the order somebody looks for them.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The grouping is <c>area-restructure.md</c> §3's — movement &amp; timing, darkness, room behaviour —
	///         plus two the schema has grown since: the daylight-brightness curve, which is one idea and belongs
	///         together, and the fades and sun entity, which are the rare things §3 already folds away.
	///     </para>
	///     <para>
	///         A section is what a person navigates by. Somebody looking for "how long the lights stay on" is
	///         thinking about movement and time, not about which of twenty-one rows it is, so the sections are
	///         named after what they change and the rare two start closed.
	///     </para>
	/// </remarks>
	public static IReadOnlyList<RoomSettingGroup> Groups { get; } =
	[
		new RoomSettingGroup(
			"Movement & timing",
			"How long the lights stay on, and what a hand change is worth",
			[
				new RoomSetting(
					nameof(AreaSettings.VacancyTimeoutSeconds),
					"Lights stay on for",
					"After the last movement, how long the lights stay on before the warning dim. Longer for rooms where people sit still.",
					RoomControl.Seconds, Step: 60, Min: 1),
				new RoomSetting(
					nameof(AreaSettings.PreOffBrightnessFactor),
					"Warning dim level",
					"How deep the warning dim is. 50 % is half the brightness the room was holding.",
					RoomControl.Fraction, Step: 5, Min: 0, Max: 100),
				new RoomSetting(
					nameof(AreaSettings.PreOffSeconds),
					"Warning dim lasts",
					"Before going out, the lights dim for this long. Any movement brings them straight back.",
					RoomControl.Seconds, Step: 5, Min: 0),
				new RoomSetting(
					nameof(AreaSettings.OverrideDurationMinutes),
					"Hand changes hold for",
					"When someone adjusts a light by hand, their choice is left alone for this long.",
					RoomControl.Minutes, Step: 15, Min: 0),
				new RoomSetting(
					nameof(AreaSettings.VacancyResetMinutes),
					"After a manual off, wait",
					"After someone turns the lights off by hand, movement won't turn them back on until the room has been empty this long.",
					RoomControl.Minutes, Step: 5, Min: 0)
			],
			StartsOpen: true),

		new RoomSettingGroup(
			"Darkness",
			"What has to be true outside before movement lights the room",
			[
				new RoomSetting(
					nameof(AreaSettings.Darkness),
					"How the room decides it's dark",
					"Which signal decides the room is dark enough to light.",
					RoomControl.Choice),
				new RoomSetting(
					nameof(AreaSettings.LuxThreshold),
					"Dark below",
					"At or below this many lux the room counts as dark. Readings run from a few lux at night to tens of thousands at midday, so pick the decade before the number.",
					RoomControl.Number, Unit: "lx", Step: 1, Min: 0, Max: MaxLux,
					AppliesWhen: settings => settings.Darkness is DarknessSource.Lux or DarknessSource.Either),
				new RoomSetting(
					nameof(AreaSettings.LuxHysteresis),
					"Bright again above",
					"The extra light needed to count as bright again, so a sensor sitting on the threshold cannot flap. Scale it with the threshold: 10 lx is a quarter of 40, and inside the sensor's own noise at 1000.",
					RoomControl.Number, Unit: "lx", Step: 1, Min: 0, Max: MaxLux,
					AppliesWhen: settings => settings.Darkness is DarknessSource.Lux or DarknessSource.Either),
				new RoomSetting(
					nameof(AreaSettings.SunElevationThreshold),
					"Dark when the sun is below",
					"Sun elevation below which the room counts as dark. Also the fallback when a room has no light sensor.",
					RoomControl.Number, Unit: "°", Step: 1, Min: -90, Max: 90,
					AppliesWhen: settings => settings.Darkness is not DarknessSource.Always)
			],
			StartsOpen: true),

		new RoomSettingGroup(
			"Brightness from daylight",
			"Lifting the room above the schedule when it is bright outside",
			[
				new RoomSetting(
					nameof(AreaSettings.LuxBrightnessEnabled),
					"Brighten with daylight",
					"On a bright day the room is lifted above the schedule's brightness, so it doesn't look gloomy against a bright window.",
					RoomControl.Flag),
				new RoomSetting(
					nameof(AreaSettings.LuxBrightnessStartLux),
					"Daylight level where brightening starts",
					"At or below this reading outside, the schedule's brightness is used unchanged.",
					RoomControl.Number, Unit: "lx", Step: 50, Min: 1,
					AppliesWhen: settings => settings.LuxBrightnessEnabled),
				new RoomSetting(
					nameof(AreaSettings.LuxBrightnessFullLux),
					"Daylight level for full brightness",
					"At or above this reading the room holds the brightest it goes. 10 000 lx is a bright overcast day.",
					RoomControl.Number, Unit: "lx", Step: 1000, Min: 1,
					AppliesWhen: settings => settings.LuxBrightnessEnabled),
				new RoomSetting(
					nameof(AreaSettings.LuxBrightnessMaxPct),
					"Brightest it goes",
					"The brightness the room is raised toward. It can only add light — a period already brighter than this is left alone.",
					RoomControl.Number, Unit: "%", Step: 5, Min: 0, Max: 100,
					AppliesWhen: settings => settings.LuxBrightnessEnabled),
				new RoomSetting(
					nameof(AreaSettings.LuxBrightnessGamma),
					"Curve shape",
					"1 rises steadily. Above 1 holds back until it is properly bright out; below 1 lifts the room as soon as the light outside starts climbing.",
					RoomControl.Number, Step: 0.1, Min: 0.1, Max: 5,
					AppliesWhen: settings => settings.LuxBrightnessEnabled)
			],
			StartsOpen: true),

		new RoomSettingGroup(
			"Room behaviour",
			"What this room does when the house sleeps, empties or fills again",
			[
				new RoomSetting(
					nameof(AreaSettings.RespectSleepMode),
					"Gentle while the house sleeps",
					"Held to the night period's limits, so a 03:00 glass of water gets a dim light.",
					RoomControl.Flag),
				new RoomSetting(
					nameof(AreaSettings.SleepBlocksAutoOn),
					"Never comes on by itself while the house sleeps",
					"For the bedroom itself. The wall switch still works.",
					RoomControl.Flag),
				new RoomSetting(
					nameof(AreaSettings.SkipAwaySweep),
					"Stays on when everyone leaves",
					"Porch and security lights are wanted precisely when nobody's home.",
					RoomControl.Flag),
				new RoomSetting(
					nameof(AreaSettings.WelcomeHome),
					"Welcome home",
					"Lights up when the first person arrives in the dark.",
					RoomControl.Flag)
			],
			StartsOpen: true),

		new RoomSettingGroup(
			"Rarely needed",
			"Fade lengths and the sun entity",
			[
				new RoomSetting(
					nameof(AreaSettings.DayTransitionSeconds),
					"Fade when it's light out",
					"How long the lights take to reach a new level while the room is not dark.",
					RoomControl.Number, Unit: "s", Step: 0.5, Min: 0),
				new RoomSetting(
					nameof(AreaSettings.NightTransitionSeconds),
					"Fade when it's dark out",
					"Gentler, because eyes are dark-adapted.",
					RoomControl.Number, Unit: "s", Step: 0.5, Min: 0),
				new RoomSetting(
					nameof(AreaSettings.SunEntity),
					"Sun entity",
					"A house normally has exactly one, so there is usually no reason to change this.",
					RoomControl.Entity)
			],
			StartsOpen: false)
	];

	/// <summary>
	///     Whether a setting's range spans so many decades that no single step can serve it.
	/// </summary>
	/// <remarks>
	///     <para>
	///         Derived from the bounds rather than flagged setting by setting, because it is arithmetic and not
	///         taste. A control running 0–65 535 lx with a five-lux step needs 7 992 presses to get from 40 to
	///         40 000, and no other step rescues it: 5 is absurd at the top of the range and 500 is unusable at
	///         the bottom. The instrument is wrong, not its calibration.
	///     </para>
	///     <para>
	///         So a setting shaped like this is typed rather than stepped, with the sentence's shortlist for the
	///         decade and the box for the number. Illuminance is the case that raised it; the rule is general, so
	///         a future setting with the same shape gets the same control without anyone remembering to ask.
	///     </para>
	/// </remarks>
	/// <param name="min">The setting's floor, in the unit its control shows.</param>
	/// <param name="max">Its ceiling, or <c>null</c> for unbounded above.</param>
	public static bool SpansDecades(double min, double? max) =>
		max is { } ceiling && ceiling / Math.Max(min, 1) >= 1000;

	/// <summary>
	///     Reads a number somebody typed, in the unit the control shows.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>Refuses rather than clamps, and says which.</b> A box that silently turns 70 000 into 65 535 has
	///         answered a question nobody asked, and the person who typed it is left looking at a number they did
	///         not choose, unable to tell a rejected entry from a typo of their own.
	///     </para>
	///     <para>
	///         Invariant first, then the machine's own culture. A browser hands <c>type="number"</c> back in HTML
	///         number syntax, but a paste, an autofill or a locale-aware field can arrive written the way the desk
	///         writes numbers, and on an <c>nb-NO</c> host that is "62,5". The invariant pass deliberately refuses
	///         thousands separators: allowing them would read "1,5" as fifteen before the Norwegian pass ever saw
	///         it, which is the same class of bug as writing <c>value="62,5"</c> into an HTML attribute.
	///     </para>
	/// </remarks>
	/// <param name="typed">What was in the box.</param>
	/// <param name="min">The lowest value the setting takes, in the shown unit.</param>
	/// <param name="max">The highest, or <c>null</c> for unbounded above.</param>
	/// <param name="unit">The unit, so a refusal names the bound the way the readout does.</param>
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

	/// <summary>The setting a key names.</summary>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	/// <exception cref="KeyNotFoundException">No group holds that key.</exception>
	public static RoomSetting Of(string key) => ByKey[key];

	/// <summary>Whether a key names a setting the detail view knows about.</summary>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	public static bool Knows(string key) => key is not null && ByKey.ContainsKey(key);

	/// <summary>
	///     Whether this room states its own value for a setting rather than following the house.
	/// </summary>
	/// <remarks>
	///     Read straight off the schema's <c>null</c>, which is what "inherit" is written as. Never a comparison
	///     against the house's number: two values being equal today says nothing about whether somebody chose one.
	/// </remarks>
	/// <param name="room">The room.</param>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	/// <exception cref="ArgumentNullException"><paramref name="room"/> is <c>null</c>.</exception>
	public static bool IsOwn(AreaConfig room, string key)
	{
		ArgumentNullException.ThrowIfNull(room);

		return RoomProperties.TryGetValue(key, out PropertyInfo? property) && property.GetValue(room) is not null;
	}

	/// <summary>How many of the overridable settings this room states for itself.</summary>
	/// <param name="room">The room.</param>
	/// <exception cref="ArgumentNullException"><paramref name="room"/> is <c>null</c>.</exception>
	public static int OwnCount(AreaConfig room)
	{
		ArgumentNullException.ThrowIfNull(room);

		return Keys.Count(key => IsOwn(room, key));
	}

	/// <summary>
	///     Sends one setting back to following the house.
	/// </summary>
	/// <remarks>
	///     Clears the room's property rather than copying the house's current number into it, and the difference
	///     is the whole point: a room that follows the house keeps following it the next time the house changes.
	///     Writing the number in would silently pin the room to today's value.
	/// </remarks>
	/// <param name="room">The room to change, in place.</param>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	/// <returns>Whether the key named an overridable setting.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="room"/> is <c>null</c>.</exception>
	public static bool Clear(AreaConfig room, string key)
	{
		ArgumentNullException.ThrowIfNull(room);

		if (!RoomProperties.TryGetValue(key, out PropertyInfo? property))
			return false;

		property.SetValue(room, null);

		return true;
	}

	/// <summary>
	///     A numeric setting's value in the unit its control shows, following the room's inheritance.
	/// </summary>
	/// <remarks>
	///     The shown unit is not always the stored one: the schema keeps the warning dim as a 0-1 factor, and a
	///     stepper offering 0.05 steps of an unnamed fraction is a control nobody can read. The conversion lives
	///     here, once, so the stepper and the row's readout can never disagree about what a number means.
	/// </remarks>
	/// <param name="room">The room, or <c>null</c> to read the house's own value.</param>
	/// <param name="defaults">The document's all-rooms settings.</param>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	/// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <c>null</c>.</exception>
	public static double Shown(AreaConfig? room, AreaSettings defaults, string key)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		double raw = Convert.ToDouble(Effective(room, defaults, key), CultureInfo.InvariantCulture);

		return Of(key).Control == RoomControl.Fraction ? raw * 100 : raw;
	}

	/// <summary>
	///     Writes a numeric setting from a value given in the unit its control shows.
	/// </summary>
	/// <remarks>
	///     Bounded here rather than by the control, so a value typed into the stepper's keyboard escape hatch is
	///     held to the same limits as one reached by pressing the arrows. Whole-number settings are rounded rather
	///     than truncated: 0.5 steps of a second-valued setting must land somewhere, and away-from-zero is what a
	///     person watching the readout expects.
	/// </remarks>
	/// <param name="room">The room to change, in place.</param>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	/// <param name="shown">The new value, in the control's own unit.</param>
	/// <exception cref="ArgumentNullException"><paramref name="room"/> is <c>null</c>.</exception>
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

	/// <summary>A yes/no setting's value, following the room's inheritance.</summary>
	/// <param name="room">The room, or <c>null</c> to read the house's own value.</param>
	/// <param name="defaults">The document's all-rooms settings.</param>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	/// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <c>null</c>.</exception>
	public static bool Flag(AreaConfig? room, AreaSettings defaults, string key)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return Effective(room, defaults, key) is true;
	}

	/// <summary>Writes a yes/no setting as this room's own.</summary>
	/// <param name="room">The room to change, in place.</param>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	/// <param name="value">The new value.</param>
	/// <exception cref="ArgumentNullException"><paramref name="room"/> is <c>null</c>.</exception>
	public static void SetFlag(AreaConfig room, string key, bool value)
	{
		ArgumentNullException.ThrowIfNull(room);

		RoomProperties[key].SetValue(room, value);
	}

	/// <summary>An entity-valued setting, following the room's inheritance.</summary>
	/// <param name="room">The room, or <c>null</c> to read the house's own value.</param>
	/// <param name="defaults">The document's all-rooms settings.</param>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	/// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <c>null</c>.</exception>
	public static string Entity(AreaConfig? room, AreaSettings defaults, string key)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return Effective(room, defaults, key) as string ?? string.Empty;
	}

	/// <summary>Writes an entity-valued setting as this room's own. An empty pick clears it back to the house.</summary>
	/// <param name="room">The room to change, in place.</param>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	/// <param name="value">The entity id, or <c>null</c>/empty to follow the house again.</param>
	/// <exception cref="ArgumentNullException"><paramref name="room"/> is <c>null</c>.</exception>
	public static void SetEntity(AreaConfig room, string key, string? value)
	{
		ArgumentNullException.ThrowIfNull(room);

		RoomProperties[key].SetValue(room, string.IsNullOrWhiteSpace(value) ? null : value);
	}

	/// <summary>
	///     A setting's value as it is written, whatever its kind: the stepper's readout, and the number inside
	///     <i>Use house setting (10 min)</i>.
	/// </summary>
	/// <param name="room">The room, or <c>null</c> to read the house's own value.</param>
	/// <param name="defaults">The document's all-rooms settings.</param>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	/// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <c>null</c>.</exception>
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
			RoomControl.Choice => Word(Choice(room, defaults, key)),
			_ => Entity(room, defaults, key) is { Length: > 0 } entity ? entity : "none"
		};
	}

	/// <summary>The value of the one enum-valued setting, following the room's inheritance.</summary>
	/// <param name="room">The room, or <c>null</c> to read the house's own value.</param>
	/// <param name="defaults">The document's all-rooms settings.</param>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	/// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <c>null</c>.</exception>
	public static DarknessSource Choice(AreaConfig? room, AreaSettings defaults, string key)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return Effective(room, defaults, key) is DarknessSource source ? source : DarknessSource.Either;
	}

	/// <summary>What a darkness rule is called on a segment.</summary>
	/// <param name="source">The rule.</param>
	public static string Word(DarknessSource source) =>
		DarknessOptions.FirstOrDefault(option => string.Equals(option.Value, source.ToString(), StringComparison.Ordinal))?.Text
		?? source.ToString();

	/// <summary>
	///     Applies one sentence-token edit to a room.
	/// </summary>
	/// <remarks>
	///     A typed switch rather than the reflection the rest of this class uses, because the conversions genuinely
	///     differ: a duration token carries seconds while the schema stores some of them in minutes, and a
	///     percentage token carries 0-100 while the schema stores a 0-1 factor. A generic path would have to guess,
	///     and a wrong guess writes a ten-second timeout where ten minutes was asked for with nothing on screen
	///     looking wrong.
	/// </remarks>
	/// <param name="room">The room to change, in place. Nothing here writes anything to disk.</param>
	/// <param name="edit">The edit the sentence handed back.</param>
	/// <returns>Whether the key was one this page knows how to apply.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
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

			default:
				return false;
		}
	}

	// ===================== the house's own copy of the same settings =====================
	//
	// The House tab edits AreaSettings directly: these values ARE the house, so there is no null to mean
	// "inherit" and no road back to offer. Everything else — which keys exist, what each is called, how a shown
	// value converts to a stored one — is the same metadata the room page reads, so a change to a bound or a
	// unit lands on both surfaces at once.

	/// <summary>
	///     Writes a numeric house default from a value given in the unit its control shows.
	/// </summary>
	/// <remarks>
	///     Bounded and converted by exactly <see cref="SetShown(AreaConfig, string, double)"/>'s rules. The house
	///     has no nullable twin, so this always writes a value — there is nothing above it to fall back to.
	/// </remarks>
	/// <param name="house">The document's all-rooms settings, changed in place.</param>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	/// <param name="shown">The new value, in the control's own unit.</param>
	/// <exception cref="ArgumentNullException"><paramref name="house"/> is <c>null</c>.</exception>
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

	/// <summary>Writes a yes/no house default.</summary>
	/// <param name="house">The document's all-rooms settings, changed in place.</param>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	/// <param name="value">The new value.</param>
	/// <exception cref="ArgumentNullException"><paramref name="house"/> is <c>null</c>.</exception>
	public static void SetFlag(AreaSettings house, string key, bool value)
	{
		ArgumentNullException.ThrowIfNull(house);

		HouseProperties[key].SetValue(house, value);
	}

	/// <summary>
	///     Writes an entity-valued house default.
	/// </summary>
	/// <remarks>
	///     An empty pick is stored as an empty string rather than <c>null</c>: the house's own properties are not
	///     nullable, and empty is already what the engine reads as "nothing chosen".
	/// </remarks>
	/// <param name="house">The document's all-rooms settings, changed in place.</param>
	/// <param name="key">The <see cref="AreaSettings"/> property name.</param>
	/// <param name="value">The entity id, or <c>null</c>/empty for none.</param>
	/// <exception cref="ArgumentNullException"><paramref name="house"/> is <c>null</c>.</exception>
	public static void SetEntity(AreaSettings house, string key, string? value)
	{
		ArgumentNullException.ThrowIfNull(house);

		HouseProperties[key].SetValue(house, value ?? string.Empty);
	}

	/// <summary>
	///     Applies one sentence-token edit to the house's defaults.
	/// </summary>
	/// <remarks>
	///     The same typed conversions <see cref="Apply(AreaConfig, SentenceEdit)"/> makes, against the
	///     non-nullable twins — so a value picked in the House tab's sentence and the same value picked in a
	///     room's sentence cannot land in the document meaning different things.
	/// </remarks>
	/// <param name="house">The document's all-rooms settings, changed in place. Nothing here writes to disk.</param>
	/// <param name="edit">The edit the sentence handed back.</param>
	/// <returns>Whether the key was one this class knows how to apply.</returns>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
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
