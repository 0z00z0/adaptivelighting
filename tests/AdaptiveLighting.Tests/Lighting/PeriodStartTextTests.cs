using System.Globalization;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Every string the picker composes parses back through the engine's own <see cref="PeriodStart.TryParse"/>.</summary>
// The header lines the same helper composes are read, never parsed, so those are asserted verbatim.
[TestClass]
public sealed class PeriodStartTextTests
{
	[TestMethod]
	public void A_Clock_Boundary_Round_Trips_Through_The_Engines_Parser()
	{
		foreach (var (hour, minute) in new[] { (0, 0), (6, 30), (12, 0), (22, 30), (23, 59) })
		{
			var text = PeriodStartText.Clock(new TimeOnly(hour, minute));

			Assert.IsTrue(PeriodStart.TryParse(text, out var parsed), $"'{text}' must parse");
			Assert.AreEqual(new TimeOnly(hour, minute), parsed!.FixedTime);
			Assert.AreEqual(SunEvent.None, parsed.SunEvent);
		}
	}

	[TestMethod]
	public void A_Bare_Sun_Anchor_Round_Trips()
	{
		Assert.AreEqual("sunrise", PeriodStartText.Sun(SunEvent.Sunrise, 0));
		Assert.AreEqual("sunset", PeriodStartText.Sun(SunEvent.Sunset, 0));

		Assert.IsTrue(PeriodStart.TryParse("sunrise", out var parsed));
		Assert.AreEqual(SunEvent.Sunrise, parsed!.SunEvent);
		Assert.AreEqual(TimeSpan.Zero, parsed.Offset);
	}

	[TestMethod]
	public void Every_Offset_The_Picker_Can_Produce_Round_Trips_Exactly()
	{
		// The control offers 0-720 minutes either side of either anchor, walked in the number input's steps.
		foreach (var anchor in new[] { SunEvent.Sunrise, SunEvent.Sunset })
		{
			foreach (var minutes in new[] { 1, 5, 15, 30, 45, 59, 60, 61, 90, 119, 120, 240, 719, 720 })
			{
				foreach (var sign in new[] { 1, -1 })
				{
					var signed = sign * minutes;
					var text = PeriodStartText.Sun(anchor, signed);

					Assert.IsTrue(PeriodStart.TryParse(text, out var parsed), $"'{text}' must parse");
					Assert.AreEqual(anchor, parsed!.SunEvent, text);
					Assert.AreEqual(TimeSpan.FromMinutes(signed), parsed.Offset, text);
					Assert.IsNull(parsed.FixedTime, text);
				}
			}
		}
	}

	[TestMethod]
	public void The_Composed_Strings_Match_The_Documented_Grammar_Verbatim()
	{
		// The strings the docs taught: a composer emitting "sunrise+0:45" would parse, and churn the file on every save.
		Assert.AreEqual("sunrise+00:45", PeriodStartText.Sun(SunEvent.Sunrise, 45));
		Assert.AreEqual("sunset-01:00", PeriodStartText.Sun(SunEvent.Sunset, -60));
		Assert.AreEqual("sunset+02:05", PeriodStartText.Sun(SunEvent.Sunset, 125));
		Assert.AreEqual("22:30", PeriodStartText.Clock(new TimeOnly(22, 30)));
	}

	[TestMethod]
	public void An_Unnamed_Room_List_Is_Left_For_The_Caller_To_Call_Any_Room()
	{
		Assert.IsNull(PeriodStartText.MotionRooms(null));
		Assert.IsNull(PeriodStartText.MotionRooms([]));
		Assert.IsNull(PeriodStartText.MotionRooms(["", "  "]), "a blank name is not a room the header can quote");
	}

	[TestMethod]
	public void Named_Rooms_Are_Listed_Until_There_Are_Too_Many_To_Read_In_A_Header()
	{
		Assert.AreEqual("Kitchen", PeriodStartText.MotionRooms(["Kitchen"]));
		Assert.AreEqual("Kitchen or Hall", PeriodStartText.MotionRooms([" Kitchen ", "Hall"]));
		Assert.AreEqual("Kitchen or 2 other rooms", PeriodStartText.MotionRooms(["Kitchen", "Hall", "Bath"]));
	}

	[TestMethod]
	public void The_Header_Says_A_Period_Waits_For_Movement_Without_Losing_Its_Boundary()
	{
		// The boundary stays on the line whatever shape it has: movement before it starts nothing.
		Assert.AreEqual("06:30 · waits for movement", PeriodStartText.WaitsForMovement("06:30", []));
		Assert.AreEqual("06:30 · waits for movement in Kitchen", PeriodStartText.WaitsForMovement("06:30", ["Kitchen"]));
		Assert.AreEqual("sunrise → 04:12 · waits for movement in Kitchen or Hall",
			PeriodStartText.WaitsForMovement("sunrise → 04:12", ["Kitchen", "Hall"]));
	}

	[TestMethod]
	public void Describe_Uses_The_Engines_Parser_Not_A_Lookalike()
	{
		Assert.AreEqual("every day at 22:30", PeriodStartText.Describe("22:30"));
		Assert.AreEqual("at sunrise", PeriodStartText.Describe("sunrise"));
		Assert.AreEqual("00:45 after sunrise", PeriodStartText.Describe("sunrise+00:45"));
		Assert.AreEqual("01:00 before sunset", PeriodStartText.Describe("sunset-01:00"));
		Assert.IsNull(PeriodStartText.Describe("half past tea"), "what the engine refuses, the note must call unparseable");
		Assert.IsNull(PeriodStartText.Describe(""));
	}

	/// <summary>The levels row names the period, so the boundary beside it carries no prose.</summary>
	[TestMethod]
	public void Short_Drops_The_Prose_And_Keeps_The_Boundary()
	{
		Assert.AreEqual("22:30", PeriodStartText.Short("22:30"));
		Assert.AreEqual("06:45", PeriodStartText.Short("06:45"));
		Assert.AreEqual("sunrise", PeriodStartText.Short("sunrise"));
		Assert.AreEqual("sunset", PeriodStartText.Short("sunset"));
		Assert.AreEqual("sunrise +00:45", PeriodStartText.Short("sunrise+00:45"));
		Assert.AreEqual("sunset -01:00", PeriodStartText.Short("sunset-01:00"));

		Assert.IsNull(PeriodStartText.Short("half past tea"), "what the engine refuses, the row must not print");
		Assert.IsNull(PeriodStartText.Short(""));
	}

	/// <summary>A comma for the decimal separator would render as an empty clock time.</summary>
	[TestMethod]
	public void Short_Writes_The_Clock_Invariantly()
	{
		CultureInfo restore = CultureInfo.CurrentCulture;

		try
		{
			CultureInfo.CurrentCulture = new CultureInfo("nb-NO");

			Assert.AreEqual("06:45", PeriodStartText.Short("06:45"));
			Assert.AreEqual("sunset -01:30", PeriodStartText.Short("sunset-01:30"));
		}
		finally
		{
			CultureInfo.CurrentCulture = restore;
		}
	}
}
