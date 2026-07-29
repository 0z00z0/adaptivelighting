using System.Globalization;

using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The response curve drawn beside a setting that is a shape rather than a quantity.
/// </summary>
/// <remarks>
///     Geometry generated inside markup is geometry nothing can assert about, and the failure mode here is
///     quiet: a curve drawn upside down is perfectly plausible on screen and says the exact opposite of the
///     number beside it.
/// </remarks>
[TestClass]
public sealed class CurvePathTests
{
	private static (double X, double Y)[] Points(string path) =>
	[
		.. path.Split(new[] { 'M', 'L' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(pair => pair.Trim().Split(' '))
			.Select(pair => (
				double.Parse(pair[0], CultureInfo.InvariantCulture),
				double.Parse(pair[1], CultureInfo.InvariantCulture)))
	];

	/// <summary>The curve spans the box corner to corner: nothing in at the bottom left, everything at the top right.</summary>
	[TestMethod]
	public void The_Curve_Runs_From_Nothing_To_Everything()
	{
		(double X, double Y)[] points = Points(CurvePath.Power(1));

		Assert.AreEqual(0, points[0].X, 1e-9);
		Assert.AreEqual(20, points[0].Y, 1e-9, "SVG counts downward, so the low end is the bottom of the box");
		Assert.AreEqual(40, points[^1].X, 1e-9);
		Assert.AreEqual(0, points[^1].Y, 1e-9);
	}

	/// <summary>A brightening curve never falls, whatever its shape.</summary>
	[TestMethod]
	public void The_Curve_Only_Ever_Rises()
	{
		foreach (double exponent in new[] { 0.4, 1.0, 2.5 })
		{
			(double X, double Y)[] points = Points(CurvePath.Power(exponent));

			for (int step = 1; step < points.Length; step++)
			{
				Assert.IsTrue(points[step].X > points[step - 1].X, "and always moves forward");
				Assert.IsTrue(points[step].Y <= points[step - 1].Y, $"exponent {exponent} dipped");
			}
		}
	}

	/// <summary>
	///     Above one holds back before climbing; below one lifts early. The picture has to match the words.
	/// </summary>
	[TestMethod]
	public void A_High_Exponent_Holds_Back_And_A_Low_One_Lifts_Early()
	{
		double straight = Points(CurvePath.Power(1))[6].Y;

		Assert.IsTrue(Points(CurvePath.Power(3))[6].Y > straight, "holding back means still low in the middle");
		Assert.IsTrue(Points(CurvePath.Power(0.4))[6].Y < straight, "lifting early means already high in the middle");
	}

	/// <summary>
	///     Past a point the shape stops carrying information, so the drawing stops changing.
	/// </summary>
	/// <remarks>
	///     A glyph is a hint at a glance. An exponent of 40 renders as an indistinguishable right angle long
	///     before the arithmetic stops working, and clamping keeps a mistyped number from drawing a lie.
	/// </remarks>
	[TestMethod]
	public void An_Extreme_Exponent_Is_Clamped_To_A_Shape_Still_Worth_Drawing()
	{
		Assert.AreEqual(CurvePath.Power(10), CurvePath.Power(1000));
		Assert.AreEqual(CurvePath.Power(0.1), CurvePath.Power(0.0001));
	}

	/// <summary>A curve needs at least its two ends.</summary>
	[TestMethod]
	public void One_Point_Is_Not_A_Curve()
	{
		Assert.ThrowsException<ArgumentOutOfRangeException>(() => CurvePath.Power(1, samples: 1));
	}

	/// <summary>
	///     The path is invariant, because SVG is: a comma decimal separator would silently break every curve.
	/// </summary>
	[TestMethod]
	public void The_Path_Is_Written_Invariantly_Whatever_The_Machine_Speaks()
	{
		CultureInfo original = CultureInfo.CurrentCulture;

		try
		{
			CultureInfo.CurrentCulture = new CultureInfo("nb-NO");

			Assert.IsFalse(CurvePath.Power(2).Contains(',', StringComparison.Ordinal),
				"a comma in a path attribute is a coordinate separator, not a decimal point");
		}
		finally
		{
			CultureInfo.CurrentCulture = original;
		}
	}

	/// <summary>The curve says what it is, for a reader who cannot see it.</summary>
	[TestMethod]
	public void The_Curve_Describes_Itself_In_Words()
	{
		Assert.AreEqual("a straight, even rise", CurvePath.Describe(1));
		Assert.AreNotEqual(CurvePath.Describe(1), CurvePath.Describe(2.5));
		Assert.AreNotEqual(CurvePath.Describe(2.5), CurvePath.Describe(0.4));
	}
}
