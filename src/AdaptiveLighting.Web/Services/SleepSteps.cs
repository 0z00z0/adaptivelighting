using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>How far a room goes to stay quiet while the house sleeps, as one rising ladder.</summary>
/// <remarks>
///     The document keeps the two booleans this maps onto. Only the control is stepped, so blocking auto-on
///     without the clamp is no longer something a person can pick, while a file already holding that pair still
///     loads and still runs.
/// </remarks>
public enum SleepStep
{
	/// <summary>The room behaves the same at every hour.</summary>
	Normal,

	/// <summary>Held to the sleep clamp period's limits.</summary>
	Dims,

	/// <summary>Held to those limits, and movement no longer lights the room.</summary>
	DimsAndStaysOff
}

/// <summary>The one stepped night control, and the two schema flags it reads and writes.</summary>
public static class SleepSteps
{
	/// <summary>The key the stepped row is filed under, which is also the first flag it writes.</summary>
	public const string Key = nameof(AreaSettings.RespectSleepMode);

	/// <summary>The second flag the same row writes.</summary>
	public const string BlockKey = nameof(AreaSettings.SleepBlocksAutoOn);

	/// <summary>The three steps in rising order, short enough for a segmented control on a phone.</summary>
	public static IReadOnlyList<TokenChoice> Options { get; } = TokenChoices.Of(
		("Normal", nameof(SleepStep.Normal)),
		("Dims", nameof(SleepStep.Dims)),
		("Dims and stays off", nameof(SleepStep.DimsAndStaysOff)));

	/// <summary>What a stored pair of flags means as a step.</summary>
	// Blocking auto-on wins alone: a file holding the block without the clamp has no lower step that covers it,
	// so the control offers the nearest one. Clause reports that pair precisely; this does not.
	public static SleepStep Of(bool respectSleepMode, bool sleepBlocksAutoOn) =>
		sleepBlocksAutoOn ? SleepStep.DimsAndStaysOff
		: respectSleepMode ? SleepStep.Dims
		: SleepStep.Normal;

	public static SleepStep Of(AreaSettings effective)
	{
		ArgumentNullException.ThrowIfNull(effective);

		return Of(effective.RespectSleepMode, effective.SleepBlocksAutoOn);
	}

	/// <summary>The step a room is on, following whatever it inherits from the house.</summary>
	public static SleepStep Of(AreaConfig? room, AreaSettings defaults)
	{
		ArgumentNullException.ThrowIfNull(defaults);

		return Of(
			room?.RespectSleepMode ?? defaults.RespectSleepMode,
			room?.SleepBlocksAutoOn ?? defaults.SleepBlocksAutoOn);
	}

	/// <summary>Writes a step to a room, pinning both flags so the room states the whole rule and not half of it.</summary>
	public static void Set(AreaConfig room, SleepStep step)
	{
		ArgumentNullException.ThrowIfNull(room);

		room.RespectSleepMode = step is not SleepStep.Normal;
		room.SleepBlocksAutoOn = step is SleepStep.DimsAndStaysOff;
	}

	public static void Set(AreaSettings house, SleepStep step)
	{
		ArgumentNullException.ThrowIfNull(house);

		house.RespectSleepMode = step is not SleepStep.Normal;
		house.SleepBlocksAutoOn = step is SleepStep.DimsAndStaysOff;
	}

	/// <summary>How a step is worded, in the same words the buttons carry.</summary>
	public static string Word(SleepStep step) =>
		Options.FirstOrDefault(option => string.Equals(option.Value, step.ToString(), StringComparison.Ordinal))?.Text
		?? step.ToString();

	/// <summary>The clause a sentence uses for a stored pair of flags, or nothing where there is no night rule to report.</summary>
	// Off the flags, not off the step: the engine clamps on RespectSleepMode alone, so a hand-written file holding
	// the block without it would be reported as dimming a room that does not dim.
	public static string? Clause(bool respectSleepMode, bool sleepBlocksAutoOn) =>
		(respectSleepMode, sleepBlocksAutoOn) switch
		{
			(true, true) => "dims and does not come on while the house sleeps",
			(false, true) => "does not come on while the house sleeps",
			(true, false) => "dims while the house sleeps",
			_ => null
		};

	public static string? Clause(AreaSettings effective)
	{
		ArgumentNullException.ThrowIfNull(effective);

		return Clause(effective.RespectSleepMode, effective.SleepBlocksAutoOn);
	}

	public static bool TryParse(string? token, out SleepStep step) => Enum.TryParse(token, out step);
}
