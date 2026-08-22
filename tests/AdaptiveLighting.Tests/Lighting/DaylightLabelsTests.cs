using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Where the daylight chart's right-edge period labels end up when two boundaries are close together.</summary>
[TestClass]
public sealed class DaylightLabelsTests
{
	private static IReadOnlyList<(string Label, double LabelY)> Spread(params double[] ys) =>
		DaylightLabels.Spread(ys.Select((y, index) => ($"p{index}", y)));

	private static void AssertNoneOverlap(IReadOnlyList<(string Label, double LabelY)> spread)
	{
		for (int i = 1; i < spread.Count; i++)
		{
			double gap = spread[i].LabelY - spread[i - 1].LabelY;

			Assert.IsTrue(
				gap >= DaylightLabels.MinGap - 0.001,
				$"{spread[i - 1].Label} and {spread[i].Label} are {gap:0.#} apart, under the {DaylightLabels.MinGap:0.#} a label box needs");
		}
	}

	[TestMethod]
	public void Labels_Already_Clear_Of_Each_Other_Are_Left_Where_They_Are()
	{
		IReadOnlyList<(string Label, double LabelY)> spread = Spread(55, 100, 160, 210);

		CollectionAssert.AreEqual(new[] { 55d, 100, 160, 210 }, spread.Select(label => label.LabelY).ToArray());
	}

	[TestMethod]
	public void A_Close_Pair_Is_Pushed_Apart()
	{
		IReadOnlyList<(string Label, double LabelY)> spread = Spread(55, 60);

		AssertNoneOverlap(spread);
		Assert.AreEqual(55, spread[0].LabelY, 0.001, "the first label had room and should not have moved");
	}

	/// <summary>Three boundaries inside half an hour at the bottom of the plot, where pushing down alone clamps every one onto the floor.</summary>
	[TestMethod]
	public void A_Run_That_Would_Overshoot_The_Floor_Moves_Up_Instead_Of_Piling_On_It()
	{
		IReadOnlyList<(string Label, double LabelY)> spread = Spread(228, 230.3, 232.3);

		AssertNoneOverlap(spread);
		Assert.AreEqual(DaylightLabels.LabelFloor, spread[^1].LabelY, 0.001, "the last label should sit on the floor, not past it");
		Assert.IsTrue(spread[0].LabelY < 228, "the run has to move up the plot to make room");
	}

	[TestMethod]
	public void A_Run_Against_The_Ceiling_Moves_Down()
	{
		IReadOnlyList<(string Label, double LabelY)> spread = Spread(6, 7, 8);

		AssertNoneOverlap(spread);
		Assert.AreEqual(DaylightLabels.LabelCeiling, spread[0].LabelY, 0.001);
	}

	[TestMethod]
	public void Every_Label_Stays_Inside_The_Plot()
	{
		IReadOnlyList<(string Label, double LabelY)> spread = Spread(232, 233, 233.5, 234, 234, 234);

		Assert.IsTrue(spread.All(label => label.LabelY >= DaylightLabels.LabelCeiling - 0.001));
		Assert.IsTrue(spread.All(label => label.LabelY <= DaylightLabels.LabelFloor + 0.001));
		AssertNoneOverlap(spread);
	}

	/// <summary>More labels than the plot can hold: an even comb spread across it instead of a stack at one end.</summary>
	[TestMethod]
	public void A_Document_With_More_Periods_Than_Fit_Spreads_Them_Evenly()
	{
		int count = (int)((DaylightLabels.LabelFloor - DaylightLabels.LabelCeiling) / DaylightLabels.MinGap) + 3;
		IReadOnlyList<(string Label, double LabelY)> spread = Spread([.. Enumerable.Repeat(120d, count)]);

		Assert.AreEqual(DaylightLabels.LabelCeiling, spread[0].LabelY, 0.001);
		Assert.AreEqual(DaylightLabels.LabelFloor, spread[^1].LabelY, 0.001);

		double step = spread[1].LabelY - spread[0].LabelY;
		for (int i = 1; i < spread.Count; i++)
			Assert.AreEqual(step, spread[i].LabelY - spread[i - 1].LabelY, 0.001);
	}

	[TestMethod]
	public void One_Label_Is_Left_Alone_And_None_Is_Not_An_Error()
	{
		Assert.AreEqual(120, Spread(120).Single().LabelY, 0.001);
		Assert.AreEqual(0, Spread().Count);
	}

	/// <summary>The gutter has to clear a period label sitting on the floor, or December meets the last period's label as either grows.</summary>
	[TestMethod]
	public void The_Month_Gutter_Clears_A_Period_Label_On_The_Floor()
	{
		double ascent = DaylightLabels.LabelAscent * DaylightLabels.MaxLabelUnits;
		double descent = DaylightLabels.LabelDescent * DaylightLabels.MaxLabelUnits;

		Assert.IsTrue(
			DaylightLabels.MonthBaseline - ascent > DaylightLabels.LabelFloor + descent,
			"the top of the month row must sit below the bottom of a period label on the floor");

		Assert.IsTrue(
			DaylightLabels.MonthBaseline - ascent > DaylightLabels.PlotHeight,
			"the month row belongs in the gutter, not over the foot of the plot");

		Assert.IsTrue(
			DaylightLabels.ChartHeight > DaylightLabels.MonthBaseline + descent,
			"the drawing has to hold the month row's descenders");
	}
}
