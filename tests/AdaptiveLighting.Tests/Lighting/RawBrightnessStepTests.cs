using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The satellite handle's 8-bit arithmetic: a raw byte in, a shown percentage out, and a nudge that
/// cannot push either end of the byte out of range.</summary>
[TestClass]
public sealed class RawBrightnessStepTests
{
	[TestMethod]
	[DataRow(0, 0.0)]
	[DataRow(255, 100.0)]
	[DataRow(128, 50.19607843137255)]
	public void ToPercent_Reports_The_Raw_Byte_As_A_Fraction_Of_255(int raw, double expected) =>
		Assert.AreEqual(expected, RawBrightnessStep.ToPercent(raw), 1e-9);

	[TestMethod]
	[DataRow(0.0, 0)]
	[DataRow(100.0, 255)]
	[DataRow(50.0, 128)]
	public void FromPercent_Rounds_To_The_Nearest_Raw_Byte(double percent, int expected) =>
		Assert.AreEqual(expected, RawBrightnessStep.FromPercent(percent));

	/// <summary>One raw step is the whole point of the handle: it must move the shown value, not round away to
	/// nothing, and it must be reachable at both ends of the byte.</summary>
	[TestMethod]
	public void One_Raw_Step_Is_About_A_Third_Of_A_Percentage_Point()
	{
		double lowest = RawBrightnessStep.ToPercent(1);
		double highest = RawBrightnessStep.ToPercent(254);

		Assert.AreEqual(100.0 / 255.0, lowest, 1e-9);
		Assert.AreEqual(100.0 * 254 / 255, highest, 1e-9);
	}

	/// <summary>A raw value of exactly 0 must not let the satellite push it negative.</summary>
	[TestMethod]
	public void Nudge_Cannot_Push_Below_Zero()
	{
		Assert.AreEqual(0, RawBrightnessStep.Nudge(0, -1));
		Assert.AreEqual(0, RawBrightnessStep.Nudge(0, -50));
		Assert.AreEqual(0, RawBrightnessStep.Nudge(3, -10));
	}

	/// <summary>A raw value of exactly 255 (100 %) must not let the satellite push it past a real byte.</summary>
	[TestMethod]
	public void Nudge_Cannot_Push_Above_255()
	{
		Assert.AreEqual(255, RawBrightnessStep.Nudge(255, 1));
		Assert.AreEqual(255, RawBrightnessStep.Nudge(255, 50));
		Assert.AreEqual(255, RawBrightnessStep.Nudge(250, 10));
	}

	/// <summary>Away from either boundary a nudge moves by exactly the steps asked for, in either direction.</summary>
	[TestMethod]
	public void Nudge_Moves_By_Exactly_The_Requested_Steps_Away_From_The_Boundary()
	{
		Assert.AreEqual(130, RawBrightnessStep.Nudge(128, 2));
		Assert.AreEqual(126, RawBrightnessStep.Nudge(128, -2));
	}

	/// <summary>A raw value handed in out of range (a stored 0-100 value converted with rounding drift) is
	/// clamped rather than trusted, so a caller cannot report a byte that never existed.</summary>
	[TestMethod]
	public void ToPercent_Clamps_An_Out_Of_Range_Raw_Value()
	{
		Assert.AreEqual(0.0, RawBrightnessStep.ToPercent(-5));
		Assert.AreEqual(100.0, RawBrightnessStep.ToPercent(300));
	}
}
