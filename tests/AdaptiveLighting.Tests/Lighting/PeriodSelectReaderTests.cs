using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The object standing between the period <c>input_select</c> and the engine: which direction it grants, what
///     it folds to nothing, and how loudly it complains about an option nobody mapped.
/// </summary>
/// <remarks>
///     The direction tests are the load-bearing ones. Exactly one of the two delegates is ever non-null, because
///     the failure the other way — the engine writing the select while also following it — is the one nobody could
///     debug from the outside: the dropdown would fight every hand that moved it.
/// </remarks>
[TestClass]
public sealed class PeriodSelectReaderTests
{
	private const string Select = "input_select.tid_pa_dagen";

	private static GlobalConfig Global(
		PeriodAuthority authority = PeriodAuthority.AdaptiveLighting,
		string? entity = Select,
		params (string Value, string Period)[] options) =>
		new()
		{
			PeriodSelect = new PeriodSelectConfig
			{
				Entity = entity,
				Authority = authority,
				Options = [.. options.Select(row => new PeriodSelectOptionConfig { Value = row.Value, Period = row.Period })]
			}
		};

	private static (string Value, string Period)[] Norwegian() =>
		[("Morgen", "morning"), ("Dag", "day"), ("Kveld", "evening"), ("Natt", "night")];

	private static PeriodSelectReader Reader(
		FakeHaContext ha,
		PeriodAuthority authority = PeriodAuthority.HomeAssistant,
		ILogger? logger = null) =>
		PeriodSelectReader.For(ha, Global(authority, Select, Norwegian()), logger ?? NullLogger.Instance)!;

	// ---- whether there is a reader at all ------------------------------------------------------

	[TestMethod]
	public void For_NoPeriodSelectConfigured_IsNull()
	{
		Assert.IsNull(PeriodSelectReader.For(new FakeHaContext(), new GlobalConfig(), NullLogger.Instance),
			"every house today has no period select, and must build no reader for one");
	}

	[TestMethod]
	public void For_BlockWithNoEntity_IsNull()
	{
		GlobalConfig global = Global(PeriodAuthority.HomeAssistant, entity: null, Norwegian());

		Assert.IsNull(PeriodSelectReader.For(new FakeHaContext(), global, NullLogger.Instance),
			"mappings without an entity name nothing to read them off");
	}

	[TestMethod]
	public void For_TrimsTheEntityId()
	{
		GlobalConfig global = Global(PeriodAuthority.HomeAssistant, "  " + Select + "  ", Norwegian());

		Assert.AreEqual(Select, PeriodSelectReader.For(new FakeHaContext(), global, NullLogger.Instance)!.Entity);
	}

	// ---- exactly one direction, decided once ---------------------------------------------------

	[TestMethod]
	public void HomeAssistantAuthority_GrantsTheReadAndNothingElse()
	{
		PeriodSelectReader reader = Reader(new FakeHaContext(), PeriodAuthority.HomeAssistant);

		Assert.IsNotNull(reader.ReadPeriod, "Home Assistant decides, so the calculators install the override");
		Assert.IsNull(reader.OptionForPeriod, "and the engine has no business writing the select it is following");
	}

	[TestMethod]
	public void AdaptiveLightingAuthority_GrantsTheWriteAndNothingElse()
	{
		PeriodSelectReader reader = Reader(new FakeHaContext(), PeriodAuthority.AdaptiveLighting);

		Assert.IsNull(reader.ReadPeriod, "the engine owns the periods, so no override is installed at all");
		Assert.IsNotNull(reader.OptionForPeriod, "and the select mirrors what the schedule resolved");
	}

	[TestMethod]
	public void TheTwoDirectionsAreNeverBothLive()
	{
		foreach (PeriodAuthority authority in Enum.GetValues<PeriodAuthority>())
		{
			PeriodSelectReader reader = Reader(new FakeHaContext(), authority);

			Assert.AreNotEqual(reader.ReadPeriod is null, reader.OptionForPeriod is null,
				$"under {authority} exactly one direction must be live — both would have the engine chasing its own tail");
		}
	}

	[TestMethod]
	public void OptionForPeriod_MapsBackToTheSelectOption()
	{
		PeriodSelectReader reader = Reader(new FakeHaContext(), PeriodAuthority.AdaptiveLighting);

		Assert.AreEqual("Kveld", reader.OptionForPeriod!("evening"));
		Assert.AreEqual("Natt", reader.OptionForPeriod("NIGHT"), "period names match case-insensitively");
		Assert.IsNull(reader.OptionForPeriod("siesta"), "a period no row names has no option to write");
	}

	// ---- reading ------------------------------------------------------------------------------

	[TestMethod]
	public void CurrentPeriodName_MapsTheSelectedOption()
	{
		FakeHaContext ha = new();
		ha.SetState(Select, "Kveld");

		Assert.AreEqual("evening", Reader(ha).CurrentPeriodName());
	}

	[TestMethod]
	public void CurrentPeriodName_MatchesTrimmedAndCaseInsensitively()
	{
		FakeHaContext ha = new();
		ha.SetState(Select, "  kVELd  ");

		Assert.AreEqual("evening", Reader(ha).CurrentPeriodName(),
			"the options are display strings somebody typed into a helper; a stray space must not stop the house");
	}

	[TestMethod]
	public void CurrentPeriodName_FoldsUnknownAndUnavailableToNothing()
	{
		FakeHaContext ha = new();

		foreach (string state in new[] { "unknown", "unavailable" })
		{
			ha.SetState(Select, state);

			Assert.IsNull(Reader(ha).CurrentPeriodName(),
				$"'{state}' is not an opinion — after a Home Assistant restart a helper sits like this for a while");
		}
	}

	[TestMethod]
	public void CurrentPeriodName_MissingEntity_IsNothing()
	{
		Assert.IsNull(Reader(new FakeHaContext()).CurrentPeriodName(), "a select that does not exist decides nothing");
	}

	[TestMethod]
	public void CurrentPeriodName_UnmappedOption_IsNothing()
	{
		FakeHaContext ha = new();
		ha.SetState(Select, "Siesta");

		Assert.IsNull(Reader(ha).CurrentPeriodName(),
			"an option nothing maps leaves the rooms on the schedule rather than guessing at one");
	}

	/// <summary>
	///     Read once per area per tick, so an unmapped option would otherwise write a line per area per minute for
	///     as long as somebody left the dropdown there.
	/// </summary>
	[TestMethod]
	public void CurrentPeriodName_WarnsOncePerDistinctUnmappedValue()
	{
		FakeHaContext ha = new();
		WarningRecorder logger = new();
		PeriodSelectReader reader = PeriodSelectReader.For(ha, Global(PeriodAuthority.HomeAssistant, Select, Norwegian()), logger)!;

		ha.SetState(Select, "Siesta");
		for (int i = 0; i < 20; i++)
			reader.CurrentPeriodName();

		Assert.AreEqual(1, logger.Warnings, "one line for the value, not one per read");

		ha.SetState(Select, "Ettermiddag");
		reader.CurrentPeriodName();
		reader.CurrentPeriodName();

		Assert.AreEqual(2, logger.Warnings, "a different value is different news, and gets its own line");

		ha.SetState(Select, "Kveld");
		reader.CurrentPeriodName();

		Assert.AreEqual(2, logger.Warnings, "a mapped option says nothing at all");
	}

	[TestMethod]
	public void CurrentValue_IsTheRawOption_AndIsReadableInBothDirections()
	{
		FakeHaContext ha = new();
		ha.SetState(Select, "Dag");

		Assert.AreEqual("Dag", Reader(ha, PeriodAuthority.HomeAssistant).CurrentValue());
		Assert.AreEqual("Dag", Reader(ha, PeriodAuthority.AdaptiveLighting).CurrentValue(),
			"the writing direction needs it too, or its mirror write could never be idempotent");
	}

	/// <summary>Counts warnings, so the once-per-value tripwire can be asserted without matching on text.</summary>
	/// <remarks>
	///     Counts <c>Warning</c> <i>and above</i>. An equality test would let an error through uncounted, so a
	///     regression that promoted the unmapped-value line from a warning to an error would read here as the
	///     tripwire having gone quiet — the assertion passing for the opposite of the reason it was written.
	/// </remarks>
	private sealed class WarningRecorder : ILogger
	{
		public int Warnings { get; private set; }

		public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (logLevel >= LogLevel.Warning)
				Warnings++;
		}

		private sealed class NullScope : IDisposable
		{
			public static readonly NullScope Instance = new();

			public void Dispose()
			{
			}
		}
	}
}
