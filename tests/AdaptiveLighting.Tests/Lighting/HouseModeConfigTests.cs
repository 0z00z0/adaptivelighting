using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary><see cref="HouseModeConfig.OptionFor"/> null-safety, the single reset target, and the sleep-clamp resolution chain.</summary>
[TestClass]
public sealed class HouseModeConfigTests
{
	private static HouseModeConfig Cabin() => new()
	{
		Entity = "input_select.husmodus",
		Options =
		[
			new() { Value = "Normal", Kind = ModeKind.Normal },
			new() { Value = "Borte", Kind = ModeKind.Away },
			new() { Value = "Sover", Kind = ModeKind.Sleep }
		]
	};

	private static List<TimePeriodConfig> Periods() =>
	[
		new() { Name = "day", Start = "07:00" },
		new() { Name = "evening", Start = "18:00" },
		new() { Name = "night", Start = "22:30" }
	];

	// ===================== OptionFor null-safety =====================

	[TestMethod]
	public void OptionFor_Does_Not_Throw_On_An_Option_With_A_Null_Value()
	{
		var config = Cabin();
		config.Options.Add(new HouseModeOptionConfig { Value = null! });   // YAML bound `value:` to null

		// The null-valued option must simply never match, not NullReferenceException while scanning.
		Assert.IsNull(config.OptionFor("anything"));
		Assert.IsNotNull(config.OptionFor("Normal"), "the well-formed options are still reachable past the null one");
	}

	// ===================== NormalOption =====================

	[TestMethod]
	public void NormalOption_IsTheFirstNormalKindOption()
	{
		Assert.AreEqual("Normal", Cabin().NormalOption!.Value, "the first Normal-kind option is the reset target");
	}

	[TestMethod]
	public void NormalOption_IsNull_WhenNoOptionIsNormal_NeverATaggedOption()
	{
		var config = new HouseModeConfig
		{
			Entity = "input_select.husmodus",
			Options = [new() { Value = "Borte", Kind = ModeKind.Away }, new() { Value = "Sover", Kind = ModeKind.Sleep }]
		};

		Assert.IsNull(config.NormalOption,
			"with nothing marked Normal there is no reset target — it must never fall back to an Away/Sleep/Guest option");
	}

	[TestMethod]
	public void NormalOption_IsTheNormalOption_WhenOneIsMarked()
	{
		var config = new HouseModeConfig
		{
			Entity = "input_select.husmodus",
			Options = [new() { Value = "Hjemme", Kind = ModeKind.Normal }, new() { Value = "Sover", Kind = ModeKind.Sleep }]
		};

		Assert.AreEqual("Hjemme", config.NormalOption!.Value, "the marked Normal option is the reset target");
	}

	[TestMethod]
	public void NormalOption_IsNull_WhenNoOptionsAreConfigured()
	{
		Assert.IsNull(new HouseModeConfig().NormalOption, "no options at all → no reset target");
	}

	// ===================== HasResetTrigger =====================

	[TestMethod]
	public void HasResetTrigger_Presence_RequiresTheToggle_NotJustAListedSensor()
	{
		var listedButOff = new HouseModeOptionConfig
		{
			Value = "Borte",
			Kind = ModeKind.Away,
			ResetOnPresence = false,
			ResetPresenceSensors = ["binary_sensor.gang_bevegelse"]
		};
		Assert.IsFalse(listedButOff.HasResetTrigger,
			"sensors listed but ResetOnPresence off is inert everywhere — the toggle is authoritative");

		var toggleOn = new HouseModeOptionConfig { Value = "Borte", Kind = ModeKind.Away, ResetOnPresence = true };
		Assert.IsTrue(toggleOn.HasResetTrigger, "the toggle alone arms the presence reset");
	}

	// ===================== SleepClampPeriodFor =====================

	[TestMethod]
	public void SleepClampPeriodFor_PrefersAnExplicitClampPeriod()
	{
		var option = new HouseModeOptionConfig { Value = "Sover", Kind = ModeKind.Sleep, ClampPeriodId = "evening" };

		Assert.AreEqual("evening", HouseModeConfig.SleepClampPeriodFor(option, Periods())?.Name,
			"an explicit ClampPeriodId wins the chain");
	}

	[TestMethod]
	public void SleepClampPeriodFor_ThenAPeriodThatSetsThisMode()
	{
		var option = new HouseModeOptionConfig { Value = "Sover", Kind = ModeKind.Sleep };
		var periods = Periods();
		periods[0].SetsModeId = "Sover";   // day sets Sover

		Assert.AreEqual("day", HouseModeConfig.SleepClampPeriodFor(option, periods)?.Name,
			"absent an explicit clamp, the first period whose SetsModeId is this option wins");
	}

	[TestMethod]
	public void SleepClampPeriodFor_ThenAPeriodLiterallyNamedNight()
	{
		var option = new HouseModeOptionConfig { Value = "Sover", Kind = ModeKind.Sleep };

		Assert.AreEqual("night", HouseModeConfig.SleepClampPeriodFor(option, Periods())?.Name,
			"absent an explicit clamp and a SetsModeId period, a period named 'night' is the fallback");
	}

	[TestMethod]
	public void SleepClampPeriodFor_IsNull_WhenNothingResolves()
	{
		var option = new HouseModeOptionConfig { Value = "Sover", Kind = ModeKind.Sleep };
		var periods = new List<TimePeriodConfig> { new() { Name = "day", Start = "07:00" }, new() { Name = "evening", Start = "18:00" } };

		Assert.IsNull(HouseModeConfig.SleepClampPeriodFor(option, periods),
			"no clamp, no SetsModeId period, no 'night' → nothing resolves");
	}

	/// <summary>Re-pointing an option's Value at a renamed helper option breaks nothing that referred to it.</summary>
	/// <remarks>A period names a mode by <c>SetsModeId</c>, which is the option's id, so the value underneath can move.</remarks>
	[TestMethod]
	public void Repointing_An_Options_Value_Keeps_Everything_That_Named_It()
	{
		HouseModeOptionConfig option = new()
		{
			Id = "sover-1gz0",
			Value = "Sover",
			Kind = ModeKind.Sleep,
			Scene = "scene.natt",
			ClampPeriodId = "natt-t1mj",
			ResetOnPresence = true,
			ActivateWhileOn = ["input_boolean.sengelys"]
		};

		HouseModeConfig house = new() { Entity = "input_select.house_state", Options = [option] };
		TimePeriodConfig night = new() { Id = "natt-t1mj", Name = "Natt", Start = "22:30", SetsModeId = option.Id };

		Assert.AreEqual("Sover", house.OptionValueFor(night.SetsModeId));

		// What the control does: the household renamed the option in Home Assistant, and moves the row onto it.
		option.Value = "Nattmodus";

		Assert.AreEqual("Nattmodus", house.OptionValueFor(night.SetsModeId),
			"the period still switches this mode on, and now writes the name Home Assistant will accept");

		Assert.AreEqual(ModeKind.Sleep, option.Kind);
		Assert.AreEqual("scene.natt", option.Scene);
		Assert.AreEqual("natt-t1mj", option.ClampPeriodId);
		Assert.IsTrue(option.ResetOnPresence);
		CollectionAssert.AreEqual(new[] { "input_boolean.sengelys" }, option.ActivateWhileOn.ToArray(),
			"a mode option carries far more than a period mapping, which is why rebuilding it by hand was the cost");
	}
}
