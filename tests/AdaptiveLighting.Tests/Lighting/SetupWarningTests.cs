using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The words the re-setup warning shows, computed from the plan the engine will actually carry out.
/// </summary>
/// <remarks>
///     <para>
///         The dialog's whole claim is that its per-room lines are concrete: hand-picked entities, changed
///         settings and a custom name, counted, because those are the three things a rebuild destroys. "Are you
///         sure?" without stakes is noise, and a warning that under-counts is worse than no warning at all — so
///         every line here is asserted against a plan made by <see cref="AreaSetupService.Plan"/> rather than one
///         written by hand. If the rebuild's losses ever stop matching its warning, that is what fails.
///     </para>
/// </remarks>
[TestClass]
public sealed class SetupWarningTests
{
	private sealed record House(FakeHaContext Ha, FakeAreaRegistry Registry, AreaEntityResolver Resolver);

	/// <summary>A house whose named areas each have a light and a motion sensor, so each one qualifies.</summary>
	private static House Build(params string[] qualifyingAreaIds)
	{
		FakeHaContext ha = new();
		FakeAreaRegistry registry = new();

		foreach (string areaId in qualifyingAreaIds)
		{
			ha.SetState($"light.{areaId}_tak", "off");
			ha.SetState($"binary_sensor.{areaId}_motion", "off", new() { ["device_class"] = "motion" });
			registry.Areas[areaId] = [$"light.{areaId}_tak", $"binary_sensor.{areaId}_motion"];
		}

		return new House(ha, registry, new AreaEntityResolver(ha, registry, new GlobalConfig(), NullLogger.Instance));
	}

	private static AdaptiveLightingConfig Document(params AreaConfig[] areas) => new()
	{
		Periods = [new TimePeriodConfig { Name = "day", Start = "06:00", BrightnessPct = 80, ColorTempKelvin = 3500 }],
		Areas = [.. areas]
	};

	private static SetupPlan Plan(AdaptiveLightingConfig config, House house, params string[] ticked) =>
		AreaSetupService.Plan(config, house.Registry, house.Resolver, ticked);

	// ===================== per-room lines =====================

	/// <summary>Everything a rebuild destroys, named and counted, in one readable sentence.</summary>
	[TestMethod]
	public void A_Room_Is_Told_Exactly_What_It_Loses()
	{
		House house = Build("stue");
		AdaptiveLightingConfig config = Document(new AreaConfig
		{
			AreaId = "stue",
			Name = "Stua med krok",
			Lights = ["light.a", "light.b"],
			VacancyTimeoutSeconds = 900,
			WelcomeHome = true,
			RespectSleepMode = true
		});

		IReadOnlyList<SetupWarningLine> lines = SetupWarning.Lines(Plan(config, house, "stue"), config);

		Assert.AreEqual(1, lines.Count);
		Assert.AreEqual("Stua med krok", lines[0].Name);
		StringAssert.Contains(lines[0].Consequence, "its custom name (“Stua med krok”)");
		StringAssert.Contains(lines[0].Consequence, "2 hand-picked entities");
		StringAssert.Contains(lines[0].Consequence, "3 changed settings");
		Assert.IsNull(lines[0].Note);
	}

	/// <summary>
	///     A room with nothing to lose says so. The alternative — a generic "everything you changed is lost" over
	///     a room where nothing was changed — teaches people to click through warnings without reading them.
	/// </summary>
	[TestMethod]
	public void A_Room_With_Nothing_To_Lose_Says_So()
	{
		House house = Build("gang");
		AdaptiveLightingConfig config = Document(new AreaConfig { AreaId = "gang" });

		IReadOnlyList<SetupWarningLine> lines = SetupWarning.Lines(Plan(config, house, "gang"), config);

		Assert.AreEqual("gang", lines[0].Name, "with no custom name the room is called by its area id");
		StringAssert.Contains(lines[0].Consequence, "nothing to lose");
	}

	/// <summary>Singulars are singular. "1 changed settings" is the tell of a warning nobody read before shipping.</summary>
	[TestMethod]
	public void One_Of_Anything_Reads_As_One()
	{
		House house = Build("bad");
		AdaptiveLightingConfig config = Document(new AreaConfig
		{
			AreaId = "bad",
			LuxSensor = "sensor.bad_lux",
			WelcomeHome = true
		});

		IReadOnlyList<SetupWarningLine> lines = SetupWarning.Lines(Plan(config, house, "bad"), config);

		StringAssert.Contains(lines[0].Consequence, "1 hand-picked entity and 1 changed setting");
	}

	/// <summary>
	///     Ticked means rebuilt, with no exceptions — a room the house has changed underneath is still rebuilt, and
	///     earns an extra line saying why it will come back empty rather than being silently skipped.
	/// </summary>
	[TestMethod]
	public void A_Room_That_No_Longer_Qualifies_Is_Still_A_Rebuild_Line_With_A_Note()
	{
		// "bod" is in the document but has nothing in Home Assistant any more.
		House house = Build("stue");
		AdaptiveLightingConfig config = Document(
			new AreaConfig { AreaId = "stue" },
			new AreaConfig { AreaId = "bod", Name = "Boden" });

		SetupPlan plan = Plan(config, house, "stue", "bod");

		CollectionAssert.Contains(plan.NoLongerQualifying.ToArray(), "bod");

		IReadOnlyList<SetupWarningLine> lines = SetupWarning.Lines(plan, config);
		SetupWarningLine bod = lines.Single(line => line.Name == "Boden");

		StringAssert.Contains(bod.Consequence, "loses its custom name");
		Assert.IsNotNull(bod.Note, "the room is rebuilt anyway, so the dialog has to say the house changed under it");
		StringAssert.Contains(bod.Note, "no longer shows both a light and a motion sensor");
	}

	// ===================== the rest of the dialog =====================

	/// <summary>New rooms are named, and named as switched off: adding a room nobody switched on cannot hurt.</summary>
	[TestMethod]
	public void New_Rooms_Are_Named_And_Said_To_Arrive_Switched_Off()
	{
		House house = Build("stue", "loftstue");
		AdaptiveLightingConfig config = Document(new AreaConfig { AreaId = "stue" });

		SetupPlan plan = Plan(config, house, "stue");
		string? sentence = SetupWarning.NewRooms(plan, areaId => areaId == "loftstue" ? "Loftstue" : areaId);

		Assert.IsNotNull(sentence);
		StringAssert.Contains(sentence, "1 new room will be added, switched off");
		StringAssert.Contains(sentence, "Loftstue");

		Assert.IsNull(SetupWarning.NewRooms(new SetupPlan([], [], []), null),
			"nothing to add is nothing to say");
	}

	/// <summary>The question the dialog asks is sized to what it is about to do.</summary>
	[TestMethod]
	public void The_Title_Counts_What_Is_Actually_Happening()
	{
		House house = Build("stue", "gang");
		AdaptiveLightingConfig config = Document(
			new AreaConfig { AreaId = "stue" },
			new AreaConfig { AreaId = "gang" });

		Assert.AreEqual("Set up 2 rooms again?", SetupWarning.Title(Plan(config, house, "stue", "gang")));
		Assert.AreEqual("Set up 1 room again?", SetupWarning.Title(Plan(config, house, "stue")));

		// Nothing ticked, but the house has grown: the run is still worth confirming, and says what it is.
		AdaptiveLightingConfig empty = Document();
		Assert.AreEqual("Add 2 new rooms?", SetupWarning.Title(Plan(empty, house)));
		Assert.AreEqual("Nothing to set up", SetupWarning.Title(new SetupPlan([], [], [])));
	}

	/// <summary>
	///     The counts in the warning are the counts of the rebuild that follows it. Asserted end to end, because
	///     the two drifting apart is the failure the whole design is arranged to prevent.
	/// </summary>
	[TestMethod]
	public void What_The_Warning_Counts_Is_What_The_Rebuild_Destroys()
	{
		House house = Build("stue");
		AdaptiveLightingConfig config = Document(new AreaConfig
		{
			AreaId = "stue",
			Name = "Stua",
			MotionSensors = ["binary_sensor.one"],
			LuxThreshold = 12,
			Enabled = true
		});

		SetupPlan plan = Plan(config, house, "stue");
		IReadOnlyList<SetupWarningLine> lines = SetupWarning.Lines(plan, config);

		StringAssert.Contains(lines[0].Consequence, "1 hand-picked entity");
		StringAssert.Contains(lines[0].Consequence, "1 changed setting");

		AreaSetupService.Apply(config, plan);
		AreaConfig rebuilt = config.Areas.Single(area => area.AreaId == "stue");

		Assert.IsNull(rebuilt.Name);
		Assert.IsNull(rebuilt.MotionSensors);
		Assert.IsNull(rebuilt.LuxThreshold);
		Assert.AreEqual(true, rebuilt.Enabled, "the switch survives, which is why the warning never counted it");
	}
}
