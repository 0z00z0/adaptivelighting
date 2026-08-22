using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The object between the period <c>input_select</c> and the engine: which direction it grants, what it folds to nothing, and how it complains.</summary>
// Only one of the two delegates is ever non-null: an engine writing the select while following it makes a
// dropdown that fights every hand that moves it.
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
				Options = [.. options.Select(row => new PeriodSelectOptionConfig { Value = row.Value, PeriodId = row.Period })]
			}
		};

	/// <summary>A whole day's options, mapping ids no schedule here defines.</summary>
	private static (string Value, string Period)[] NorwegianDropdown() =>
		[("Morgen", "morning"), ("Dag", "day"), ("Kveld", "evening"), ("Natt", "night")];

	private static PeriodSelectReader Reader(
		FakeHaContext ha,
		PeriodAuthority authority = PeriodAuthority.HomeAssistant,
		ILogger? logger = null) =>
		PeriodSelectReader.For(ha, Global(authority, Select, NorwegianDropdown()), logger ?? NullLogger.Instance)!;

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
		GlobalConfig global = Global(PeriodAuthority.HomeAssistant, entity: null, NorwegianDropdown());

		Assert.IsNull(PeriodSelectReader.For(new FakeHaContext(), global, NullLogger.Instance),
			"mappings without an entity name nothing to read them off");
	}

	[TestMethod]
	public void For_TrimsTheEntityId()
	{
		GlobalConfig global = Global(PeriodAuthority.HomeAssistant, "  " + Select + "  ", NorwegianDropdown());

		Assert.AreEqual(Select, PeriodSelectReader.For(new FakeHaContext(), global, NullLogger.Instance)!.Entity);
	}

	// ---- one direction only, decided once ------------------------------------------------------

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

		Assert.AreEqual("evening", Reader(ha).CurrentPeriodId());
	}

	[TestMethod]
	public void CurrentPeriodName_MatchesTrimmedAndCaseInsensitively()
	{
		FakeHaContext ha = new();
		ha.SetState(Select, "  kVELd  ");

		Assert.AreEqual("evening", Reader(ha).CurrentPeriodId(),
			"the options are display strings somebody typed into a helper; a stray space must not stop the house");
	}

	[TestMethod]
	public void CurrentPeriodName_FoldsUnknownAndUnavailableToNothing()
	{
		FakeHaContext ha = new();

		foreach (string state in new[] { "unknown", "unavailable" })
		{
			ha.SetState(Select, state);

			Assert.IsNull(Reader(ha).CurrentPeriodId(),
				$"'{state}' is not an opinion — after a Home Assistant restart a helper sits like this for a while");
		}
	}

	[TestMethod]
	public void CurrentPeriodName_MissingEntity_IsNothing()
	{
		Assert.IsNull(Reader(new FakeHaContext()).CurrentPeriodId(), "a select that does not exist decides nothing");
	}

	[TestMethod]
	public void CurrentPeriodName_UnmappedOption_IsNothing()
	{
		FakeHaContext ha = new();
		ha.SetState(Select, "Siesta");

		Assert.IsNull(Reader(ha).CurrentPeriodId(),
			"an option nothing maps leaves the rooms on the schedule rather than guessing at one");
	}

	// Read once per area per tick. Without the tripwire an unmapped option writes a line per area per minute.
	[TestMethod]
	public void CurrentPeriodName_WarnsOncePerDistinctUnmappedValue()
	{
		FakeHaContext ha = new();
		WarningRecorder logger = new();
		PeriodSelectReader reader = PeriodSelectReader.For(ha, Global(PeriodAuthority.HomeAssistant, Select, NorwegianDropdown()), logger)!;

		ha.SetState(Select, "Siesta");
		for (int i = 0; i < 20; i++)
			reader.CurrentPeriodId();

		Assert.AreEqual(1, logger.Warnings, "one line for the value, not one per read");

		ha.SetState(Select, "Ettermiddag");
		reader.CurrentPeriodId();
		reader.CurrentPeriodId();

		Assert.AreEqual(2, logger.Warnings, "a different value is different news, and gets its own line");

		ha.SetState(Select, "Kveld");
		reader.CurrentPeriodId();

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

	/// <summary>Counts warnings, so the once-per-value tripwire is asserted without matching on text.</summary>
	// Warning and above: with an equality test, promoting the line to an error would read as the tripwire gone quiet.
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
