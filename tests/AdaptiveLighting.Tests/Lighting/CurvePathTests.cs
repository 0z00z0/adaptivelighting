using System.Globalization;

using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The response curve drawn beside a setting whose value is a shape, not a quantity.</summary>
/// <remarks>A curve drawn upside down looks plausible on screen, so the geometry lives outside the markup where it can be asserted.</remarks>
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

	[TestMethod]
	public void The_Curve_Runs_From_Nothing_To_Everything()
	{
		(double X, double Y)[] points = Points(CurvePath.Power(1));

		Assert.AreEqual(0, points[0].X, 1e-9);
		Assert.AreEqual(20, points[0].Y, 1e-9, "SVG counts downward, so the low end is the bottom of the box");
		Assert.AreEqual(40, points[^1].X, 1e-9);
		Assert.AreEqual(0, points[^1].Y, 1e-9);
	}

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

	[TestMethod]
	public void A_High_Exponent_Holds_Back_And_A_Low_One_Lifts_Early()
	{
		double straight = Points(CurvePath.Power(1))[6].Y;

		Assert.IsTrue(Points(CurvePath.Power(3))[6].Y > straight, "holding back means still low in the middle");
		Assert.IsTrue(Points(CurvePath.Power(0.4))[6].Y < straight, "lifting early means already high in the middle");
	}

	// An exponent of 40 draws as a right angle long before the arithmetic stops working, so it is clamped.
	[TestMethod]
	public void An_Extreme_Exponent_Is_Clamped_To_A_Shape_Still_Worth_Drawing()
	{
		Assert.AreEqual(CurvePath.Power(10), CurvePath.Power(1000));
		Assert.AreEqual(CurvePath.Power(0.1), CurvePath.Power(0.0001));
	}

	[TestMethod]
	public void One_Point_Is_Not_A_Curve()
	{
		Assert.ThrowsException<ArgumentOutOfRangeException>(() => CurvePath.Power(1, samples: 1));
	}

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

	[TestMethod]
	public void The_Curve_Describes_Itself_In_Words()
	{
		Assert.AreEqual("a straight, even rise", CurvePath.Describe(1));
		Assert.AreNotEqual(CurvePath.Describe(1), CurvePath.Describe(2.5));
		Assert.AreNotEqual(CurvePath.Describe(2.5), CurvePath.Describe(0.4));
	}
}
