using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The editing rule that seeds the curve's dark end from the level of the period that first claims it.</summary>
[TestClass]
public sealed class DaylightCurveModeTests
{
	private static AdaptiveLightingConfig House(params double[] brightnessPct)
	{
		AdaptiveLightingConfig config = new()
		{
			Defaults = new AreaSettings(),
			Periods =
			[
				.. brightnessPct.Select((double percent, int index) => new TimePeriodConfig
				{
					Id = $"p{index}",
					Name = $"period {index}",
					BrightnessPct = percent
				})
			]
		};

		return config;
	}

	private static bool Set(AdaptiveLightingConfig config, int index, bool useCurve) =>
		DaylightCurveMode.Set(config.Periods, config.Defaults, config.Periods[index], useCurve);

	[TestMethod]
	public void FirstPeriodOnTheCurveSeedsHalfItsOwnLevel()
	{
		AdaptiveLightingConfig night = House(15, 90);

		Assert.AreNotEqual(7.5d, night.Defaults.LuxBrightnessMinPct);
		Assert.IsTrue(Set(night, 0, true));
		Assert.AreEqual(7.5d, night.Defaults.LuxBrightnessMinPct);

		AdaptiveLightingConfig day = House(15, 90);

		Assert.AreNotEqual(45d, day.Defaults.LuxBrightnessMinPct);
		Assert.IsTrue(Set(day, 1, true));
		Assert.AreEqual(45d, day.Defaults.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void SecondPeriodOnTheCurveLeavesTheDarkEndAlone()
	{
		AdaptiveLightingConfig config = House(15, 90);

		Set(config, 0, true);
		Assert.AreEqual(7.5d, config.Defaults.LuxBrightnessMinPct);

		Assert.IsFalse(Set(config, 1, true));
		Assert.AreEqual(7.5d, config.Defaults.LuxBrightnessMinPct);
		Assert.AreNotEqual(45d, config.Defaults.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void AHandDraggedDarkEndSurvivesTheSecondPeriodJoining()
	{
		AdaptiveLightingConfig config = House(15, 90);

		Set(config, 0, true);
		config.Defaults.LuxBrightnessMinPct = 22;

		Assert.IsFalse(Set(config, 1, true));
		Assert.AreEqual(22d, config.Defaults.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void ReclaimingTheCurveOnAPeriodAlreadyOnItSeedsNothing()
	{
		AdaptiveLightingConfig config = House(15, 90);

		Set(config, 0, true);
		config.Defaults.LuxBrightnessMinPct = 22;

		Assert.IsFalse(Set(config, 0, true));
		Assert.AreEqual(22d, config.Defaults.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void ARoomsOwnDarkEndIsNeverTouched()
	{
		AdaptiveLightingConfig config = House(15, 90);
		AreaConfig stated = new() { AreaId = "kitchen", LuxBrightnessMinPct = 30 };
		AreaConfig silent = new() { AreaId = "hall" };
		config.Areas.AddRange([stated, silent]);

		Assert.IsTrue(Set(config, 0, true));

		Assert.AreEqual(30d, stated.LuxBrightnessMinPct);
		Assert.AreEqual(30d, stated.Effective(config.Defaults).LuxBrightnessMinPct);

		// The room that states nothing does follow the seeded house value, which is what makes the check above mean
		// something.
		Assert.IsNull(silent.LuxBrightnessMinPct);
		Assert.AreEqual(7.5d, silent.Effective(config.Defaults).LuxBrightnessMinPct);
	}

	[TestMethod]
	public void OffEverywhereAndOnAgainSeedsAfresh()
	{
		AdaptiveLightingConfig config = House(15, 90);

		Set(config, 0, true);
		Assert.AreEqual(7.5d, config.Defaults.LuxBrightnessMinPct);

		Set(config, 1, true);
		Assert.IsFalse(Set(config, 0, false));
		Assert.IsFalse(Set(config, 1, false));
		Assert.IsFalse(DaylightCurveMode.InUse(config.Periods));
		Assert.AreEqual(7.5d, config.Defaults.LuxBrightnessMinPct);

		Assert.IsTrue(Set(config, 1, true));
		Assert.AreEqual(45d, config.Defaults.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void ADocumentWhereTheCurveIsAlreadyRunningIsLeftAsItIs()
	{
		AdaptiveLightingConfig config = House(15, 90);
		config.Periods[0].UseDaylightCurve = true;
		config.Defaults.LuxBrightnessMinPct = 18;
		AreaConfig room = new() { AreaId = "kitchen", LuxBrightnessMinPct = 30 };
		config.Areas.Add(room);

		Assert.IsFalse(Set(config, 1, true));

		Assert.AreEqual(18d, config.Defaults.LuxBrightnessMinPct);
		Assert.AreEqual(30d, room.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void LeavingTheCurveSeedsNothing()
	{
		AdaptiveLightingConfig config = House(15, 90);

		Set(config, 0, true);
		config.Defaults.LuxBrightnessMinPct = 22;

		Assert.IsFalse(Set(config, 0, false));
		Assert.IsFalse(config.Periods[0].UseDaylightCurve);
		Assert.AreEqual(22d, config.Defaults.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void TheEndsOfTheRangeAreClamped()
	{
		AdaptiveLightingConfig dark = House(0);
		Assert.IsTrue(Set(dark, 0, true));
		Assert.AreEqual(0d, dark.Defaults.LuxBrightnessMinPct);

		AdaptiveLightingConfig full = House(100);
		Assert.IsTrue(Set(full, 0, true));
		Assert.AreEqual(50d, full.Defaults.LuxBrightnessMinPct);

		// A document edited past the validator's range still seeds inside it.
		AdaptiveLightingConfig over = House(400);
		Assert.IsTrue(Set(over, 0, true));
		Assert.AreEqual(100d, over.Defaults.LuxBrightnessMinPct);

		AdaptiveLightingConfig under = House(-40);
		Assert.IsTrue(Set(under, 0, true));
		Assert.AreEqual(0d, under.Defaults.LuxBrightnessMinPct);
	}

	[TestMethod]
	public void TheSeededValueCarriesOneDecimal()
	{
		Assert.AreEqual(7.5d, DaylightCurveMode.DarkEndFor(15));
		Assert.AreEqual(12.3d, DaylightCurveMode.DarkEndFor(24.66));
		Assert.AreEqual(0.1d, DaylightCurveMode.DarkEndFor(0.11));
	}

	[TestMethod]
	public void EverySeededValuePassesValidation()
	{
		foreach (double percent in new[] { 0d, 15d, 50d, 90d, 100d })
		{
			AdaptiveLightingConfig config = House(percent);
			config.Global.OutdoorLuxSensor = "sensor.outside";
			Set(config, 0, true);

			Assert.IsTrue(
				config.Defaults.LuxBrightnessMinPct is >= 0 and <= 100,
				$"{percent} seeded {config.Defaults.LuxBrightnessMinPct}");
			Assert.IsTrue(config.Defaults.LuxBrightnessMaxPct > config.Defaults.LuxBrightnessMinPct);
		}
	}
}
