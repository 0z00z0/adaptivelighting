using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.AppModel;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The read-only enrichment of the modes page: the per-mode period/target preview and its area-effect counts
///     (<see cref="ModeService.ComputePreview"/>), and the derived <see cref="ModeKind"/> the hero shows, taken from
///     the current option (<see cref="ModeService.GetHouseState"/>).
/// </summary>
/// <remarks>
///     The preview is a pure function of (config, kind, instant, sun times) — the same premise the engine's
///     <see cref="AdaptiveLighting.Engine.CircadianCalculator"/> makes — so these need no clock and
///     no fakes for the compute. There is one shared table now, so every kind but Away resolves the same baseline
///     period. The derived-state tests go through the whole service against <see cref="FakeHaContext"/>, because
///     that is the code path the page runs.
/// </remarks>
[TestClass]
public sealed class ModeServicePreviewTests
{
	private sealed class FakeAppConfig(AdaptiveLightingConfig value) : IAppConfig<AdaptiveLightingConfig>
	{
		public AdaptiveLightingConfig Value { get; } = value;
	}

	private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 1, 15, hour, minute, 0, TimeSpan.Zero);

	private static readonly SunTimes NoSun = SunTimes.Unknown;

	private static AdaptiveLightingConfig CabinConfig() => new()
	{
		Periods =
		[
			new() { Name = "day", Start = "07:00", BrightnessPct = 90, ColorTempKelvin = 4500 },
			new() { Name = "evening", Start = "18:00", BrightnessPct = 70, ColorTempKelvin = 2700 },
			new() { Name = "night", Start = "22:30", BrightnessPct = 15, ColorTempKelvin = 2200 }
		],
		Defaults = new AreaSettings(),
		Areas =
		[
			// living room: swept away, respects sleep
			new() { Name = "stue", AreaId = "stue", RespectSleepMode = true },
			// hallway: opts out of the sweep, blocks auto-on while asleep
			new() { Name = "gang", AreaId = "gang", SkipAwaySweep = true, RespectSleepMode = true, SleepBlocksAutoOn = true },
			// outdoor: opts out of the sweep
			new() { Name = "ute", AreaId = "ute", SkipAwaySweep = true },
			// a disabled area must be counted by neither the sweep nor the clamp
			new() { Name = "loft", AreaId = "loft", RespectSleepMode = true, Enabled = false }
		]
	};

	// ===================== active-period resolution =====================

	[TestMethod]
	public void A_Normal_Mode_Resolves_The_Baseline_Period_For_Now()
	{
		var preview = ModeService.ComputePreview(CabinConfig(), ModeKind.Normal, At(20), NoSun);

		Assert.AreEqual("evening", preview.ActivePeriodName, "20:00 is the evening period on the baseline");
		Assert.AreEqual(70d, preview.PreviewBrightness);
		Assert.AreEqual(2700, preview.PreviewKelvin);
		Assert.IsFalse(preview.IsOffPreview);
	}

	[TestMethod]
	public void A_Sleep_Mode_Resolves_The_Shared_Table()
	{
		// There is one shared table now: sleep resolves the same period the baseline would.
		var preview = ModeService.ComputePreview(CabinConfig(), ModeKind.Sleep, At(14), NoSun);

		Assert.AreEqual("day", preview.ActivePeriodName, "14:00 is the day period on the shared table");
		Assert.AreEqual(90d, preview.PreviewBrightness);
		Assert.AreEqual(4500, preview.PreviewKelvin);
		Assert.IsFalse(preview.IsOffPreview);
	}

	[TestMethod]
	public void A_Guest_Mode_Resolves_The_Shared_Table_Not_An_Off_Preview()
	{
		var preview = ModeService.ComputePreview(CabinConfig(), ModeKind.Guest, At(20), NoSun);

		Assert.IsFalse(preview.IsOffPreview, "guest holds a scene or follows the schedule — it is not the away sweep");
		Assert.AreEqual("evening", preview.ActivePeriodName);
	}

	[TestMethod]
	public void An_Away_Mode_Shows_An_Off_Preview_Not_A_Period_Colour()
	{
		var preview = ModeService.ComputePreview(CabinConfig(), ModeKind.Away, At(20), NoSun);

		Assert.IsTrue(preview.IsOffPreview, "an away mode pauses/sweeps the areas, so the swatch is dark");
		Assert.IsNull(preview.ActivePeriodName);
		Assert.IsNull(preview.PreviewBrightness);
		Assert.IsNull(preview.PreviewKelvin);
	}

	[TestMethod]
	public void An_Empty_Table_Resolves_No_Period_Rather_Than_Guessing()
	{
		var config = new AdaptiveLightingConfig();   // no periods at all

		var preview = ModeService.ComputePreview(config, ModeKind.Normal, At(20), NoSun);

		Assert.IsNull(preview.ActivePeriodName, "no period can be placed, so nothing is asserted");
		Assert.IsNull(preview.PreviewBrightness);
		Assert.IsFalse(preview.IsOffPreview);
	}

	// ===================== area-effect counts =====================

	[TestMethod]
	public void The_Away_Effect_Counts_Swept_And_Kept_Areas_Ignoring_Disabled_Ones()
	{
		var preview = ModeService.ComputePreview(CabinConfig(), ModeKind.Away, At(20), NoSun);

		// Three enabled areas: stue is swept; gang and ute opt out. The disabled loft is counted by neither.
		Assert.AreEqual("turns 1 of 3 rooms off, keeps 2 on", preview.EffectSummary);
	}

	[TestMethod]
	public void The_Away_Effect_Reads_Cleanly_When_No_Area_Opts_Out()
	{
		var config = new AdaptiveLightingConfig
		{
			Areas = [new() { Name = "a", AreaId = "a" }, new() { Name = "b", AreaId = "b" }]
		};

		var preview = ModeService.ComputePreview(config, ModeKind.Away, At(20), NoSun);

		Assert.AreEqual("turns all 2 rooms off", preview.EffectSummary);
	}

	[TestMethod]
	public void The_Sleep_Effect_Counts_Clamped_And_Blocked_Areas_Ignoring_Disabled_Ones()
	{
		var preview = ModeService.ComputePreview(CabinConfig(), ModeKind.Sleep, At(2), NoSun);

		// stue + gang respect sleep (loft is disabled, so not counted); only gang blocks auto-on.
		Assert.AreEqual("night levels in 2 rooms, 1 never turn on by themselves", preview.EffectSummary);
	}

	[TestMethod]
	public void The_Normal_Effect_Names_The_Period_It_Uses()
	{
		var preview = ModeService.ComputePreview(CabinConfig(), ModeKind.Normal, At(20), NoSun);

		Assert.AreEqual("everyday lighting — the \"evening\" period right now", preview.EffectSummary);
	}

	// ===================== derived kind from the current option =====================

	[TestMethod]
	public void Derived_State_Comes_From_The_Current_Options_Kind()
	{
		var config = new AdaptiveLightingConfig
		{
			Global = new GlobalConfig
			{
				HouseMode = new HouseModeConfig
				{
					Entity = "input_select.husmodus",
					Options =
					[
						new() { Value = "Normal", Kind = ModeKind.Normal },
						new() { Value = "Sover", Kind = ModeKind.Sleep },
						new() { Value = "Borte", Kind = ModeKind.Away }
					]
				}
			}
		};

		var ha = new FakeHaContext();
		ha.SetState("input_select.husmodus", "Sover");

		var state = Service(ha, config).GetHouseState();

		Assert.AreEqual(ModeKind.Sleep, state.ActiveKind, "the current option is sleep-kind");
		Assert.IsTrue(state.IsAvailable);
	}

	[TestMethod]
	public void Derived_State_Reads_A_Guest_Kind_Current_Option()
	{
		var config = new AdaptiveLightingConfig
		{
			Global = new GlobalConfig
			{
				HouseMode = new HouseModeConfig
				{
					Entity = "input_select.husmodus",
					Options =
					[
						new() { Value = "Normal", Kind = ModeKind.Normal },
						new() { Value = "Gjester", Kind = ModeKind.Guest, Scene = "scene.gjest" }
					]
				}
			}
		};

		var ha = new FakeHaContext();
		ha.SetState("input_select.husmodus", "Gjester");

		var state = Service(ha, config).GetHouseState();

		Assert.AreEqual(ModeKind.Guest, state.ActiveKind, "a guest-kind current option drives the guest pill");
	}

	[TestMethod]
	public void Derived_State_With_No_Select_Is_Normal()
	{
		// No house-mode select: the hero has nothing to assert, so it reads Normal.
		var config = new AdaptiveLightingConfig { Global = new GlobalConfig() };

		var state = Service(new FakeHaContext(), config).GetHouseState();

		Assert.AreEqual(ModeKind.Normal, state.ActiveKind);
		Assert.IsTrue(state.IsAvailable, "no select to probe means no disconnection was discovered");
	}

	// ===================== master-switch default (09 §7) =====================

	[TestMethod]
	public void The_Master_Switch_Toggle_Renders_From_The_Built_In_Default_When_Unset()
	{
		// KillSwitchEntity is blank; the host provides the app's own enable switch as the in-memory default.
		var config = new AdaptiveLightingConfig { Global = new GlobalConfig() };
		var builtIn = "input_boolean.netdaemon_test_app";

		var ha = new FakeHaContext();
		ha.SetState(builtIn, "on");

		var toggles = Service(ha, config, builtIn).GetToggles();

		Assert.AreEqual(1, toggles.Count, "the master switch always renders now, via the default");
		Assert.AreEqual(builtIn, toggles[0].EntityId);
		Assert.AreEqual("Adaptive lighting", toggles[0].Label, "the master switch is labelled plainly now");
	}

	[TestMethod]
	public void The_Master_Switch_Appears_After_Attach_WithoutReconstructingTheService()
	{
		// The scoped ModeService can be built before the singleton engine host is attached to Home Assistant. The
		// built-in switch must appear once the connection lands, on the same service instance — so it is read live,
		// not copied once at construction.
		var config = new AdaptiveLightingConfig { Global = new GlobalConfig() };
		var builtIn = "input_boolean.netdaemon_test_app";

		var ha = new FakeHaContext();
		ha.SetState(builtIn, "on");

		var catalog = new HaCatalog(ha, new FakeHaRegistry(), NullLoggerFactory.Instance);
		var host = new LightingEngineHost(
			new LightingConfigStore(
				System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"modeservice-{Guid.NewGuid():N}.yaml"),
				NullLogger<LightingConfigStore>.Instance),
			NullLoggerFactory.Instance);

		var service = new ModeService(ha, new FakeAppConfig(config), catalog, host, NullLogger<ModeService>.Instance);

		Assert.AreEqual(0, service.GetToggles().Count, "before Attach the built-in switch is unknown, so nothing renders");

		host.Attach(ha, new FakeHaRegistry(), System.Reactive.Concurrency.CurrentThreadScheduler.Instance, builtIn);

		var toggles = service.GetToggles();
		Assert.AreEqual(1, toggles.Count, "after Attach the master switch appears on the same service instance");
		Assert.AreEqual(builtIn, toggles[0].EntityId);
	}

	// ===================== master-switch view (dashboard) =====================

	[TestMethod]
	public void The_Master_Switch_View_Reads_On_When_The_Enabled_Flag_Is_On()
	{
		var config = new AdaptiveLightingConfig { Global = new GlobalConfig() };
		var builtIn = "input_boolean.netdaemon_test_app";

		var ha = new FakeHaContext();
		ha.SetState(builtIn, "on");

		var view = Service(ha, config, builtIn).GetMasterSwitch();

		Assert.IsNotNull(view, "the master switch resolves from the built-in default once attached");
		Assert.AreEqual(builtIn, view.Toggle.EntityId);
		Assert.IsTrue(view.AdaptiveLightingOn, "a defaulted enabled-flag reading on means the engine is commanding");
		Assert.IsTrue(view.IsAvailable);
		Assert.IsTrue(view.IsReady);
	}

	[TestMethod]
	public void The_Master_Switch_View_Reads_Off_When_The_Enabled_Flag_Is_Off()
	{
		var config = new AdaptiveLightingConfig { Global = new GlobalConfig() };
		var builtIn = "input_boolean.netdaemon_test_app";

		var ha = new FakeHaContext();
		ha.SetState(builtIn, "off");

		var view = Service(ha, config, builtIn).GetMasterSwitch();

		Assert.IsNotNull(view);
		Assert.IsFalse(view.AdaptiveLightingOn, "an enabled-flag reading off means the engine is paused");
		Assert.IsTrue(view.IsAvailable);
	}

	[TestMethod]
	public void The_Master_Switch_View_Folds_In_An_Explicit_Kill_Switchs_Inverted_Polarity()
	{
		// An explicit kill switch read inverted: on muzzles the engine, so AdaptiveLightingOn is the inverse of IsOn.
		var config = new AdaptiveLightingConfig
		{
			Global = new GlobalConfig { KillSwitchEntity = "switch.kill", KillSwitchActiveWhenOff = false }
		};

		var ha = new FakeHaContext();
		ha.SetState("switch.kill", "on");

		var view = Service(ha, config).GetMasterSwitch();

		Assert.IsNotNull(view);
		Assert.AreEqual("switch.kill", view.Toggle.EntityId);
		Assert.IsFalse(view.AdaptiveLightingOn, "an on kill switch means the engine is muzzled");
	}

	[TestMethod]
	public void The_Master_Switch_View_Is_Null_Before_The_Default_Resolves()
	{
		// KillSwitchEntity is blank and no Attach has provided the built-in default, so there is nothing to show.
		var config = new AdaptiveLightingConfig { Global = new GlobalConfig() };

		var view = Service(new FakeHaContext(), config).GetMasterSwitch();

		Assert.IsNull(view, "no switch resolves before Attach, so the dashboard shows nothing rather than a phantom");
	}

	[TestMethod]
	public void The_Master_Switch_View_Reports_Unavailable_When_HA_Does_Not_Know_The_Entity()
	{
		var config = new AdaptiveLightingConfig { Global = new GlobalConfig { KillSwitchEntity = "switch.kill" } };

		// No state set for switch.kill: HA answers (does not throw) but has no such entity.
		var view = Service(new FakeHaContext(), config).GetMasterSwitch();

		Assert.IsNotNull(view, "the entity is configured, so the control still renders");
		Assert.IsFalse(view.IsAvailable, "Home Assistant does not know the entity");
	}

	// ===================== who's-home (GetPeople) =====================

	[TestMethod]
	public void GetPeople_Discovers_Every_Person_Entity_When_None_Are_Configured()
	{
		// No configured Persons list, so the panel falls back to the person domain — mirroring PresenceMonitor.
		var config = new AdaptiveLightingConfig { Global = new GlobalConfig() };

		var ha = new FakeHaContext();
		ha.SetState("person.alex", "home", new() { ["friendly_name"] = "Alex" });
		ha.SetState("person.kari", "not_home", new() { ["friendly_name"] = "Kari" });

		var people = Service(ha, config).GetPeople();

		Assert.AreEqual(2, people.Count, "both discovered person entities are shown");

		var alex = people.Single(p => p.EntityId == "person.alex");
		Assert.AreEqual("Alex", alex.Name, "the friendly name is used");
		Assert.IsTrue(alex.IsHome);
		Assert.IsTrue(alex.IsAvailable);

		var kari = people.Single(p => p.EntityId == "person.kari");
		Assert.IsFalse(kari.IsHome, "not_home is away, not home");
		Assert.IsTrue(kari.IsAvailable);
	}

	[TestMethod]
	public void GetPeople_Uses_The_Configured_List_When_One_Is_Set()
	{
		// A configured list wins over discovery, and it may name a device_tracker — exactly as the engine watches it.
		var config = new AdaptiveLightingConfig
		{
			Global = new GlobalConfig { Persons = ["person.alex", "device_tracker.kari_phone"] }
		};

		var ha = new FakeHaContext();
		ha.SetState("person.alex", "home", new() { ["friendly_name"] = "Alex" });
		ha.SetState("device_tracker.kari_phone", "home");
		// A person entity NOT on the configured list must be ignored.
		ha.SetState("person.guest", "home", new() { ["friendly_name"] = "Guest" });

		var people = Service(ha, config).GetPeople();

		Assert.AreEqual(2, people.Count, "only the configured entities are watched");
		Assert.IsTrue(people.Any(p => p.EntityId == "device_tracker.kari_phone"), "a configured device_tracker is included");
		Assert.IsFalse(people.Any(p => p.EntityId == "person.guest"), "an unlisted person entity is left out");
	}

	[TestMethod]
	public void GetPeople_Reports_An_Unknown_Entity_As_Unavailable_Not_Away()
	{
		// A configured id Home Assistant does not know reads "unknown" (IsAvailable false), never "away".
		var config = new AdaptiveLightingConfig
		{
			Global = new GlobalConfig { Persons = ["person.ghost"] }
		};

		var people = Service(new FakeHaContext(), config).GetPeople();

		Assert.AreEqual(1, people.Count);
		Assert.IsFalse(people[0].IsAvailable, "Home Assistant does not know the entity");
		Assert.IsFalse(people[0].IsHome, "unknown is not home");
		Assert.AreEqual("person.ghost", people[0].Name, "with no friendly name the id stands in");
	}

	private static ModeService Service(FakeHaContext ha, AdaptiveLightingConfig config, string? defaultKillSwitch = null)
	{
		var catalog = new HaCatalog(ha, new FakeHaRegistry(), NullLoggerFactory.Instance);
		var host = new LightingEngineHost(
			new LightingConfigStore(
				System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"modeservice-{Guid.NewGuid():N}.yaml"),
				NullLogger<LightingConfigStore>.Instance),
			NullLoggerFactory.Instance);

		if (defaultKillSwitch is not null)
			host.Attach(ha, new FakeHaRegistry(), System.Reactive.Concurrency.CurrentThreadScheduler.Instance, defaultKillSwitch);

		return new ModeService(ha, new FakeAppConfig(config), catalog, host, NullLogger<ModeService>.Instance);
	}
}
