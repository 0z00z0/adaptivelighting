using System.Reflection;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The Areas section's decisions, tested where they live rather than in markup.
/// </summary>
/// <remarks>
///     This repo has no Razor render-test harness and deliberately does not gain one, so the parts of the settings
///     list worth being sure about were extracted as pure functions: which colour a room's edge takes, what the
///     floor bulk action does to the document, and how many settings "n of 16" is counting. Each of those is a
///     thing that would be wrong silently — a wrong edge colour reads as a lie about the room, a wrong bulk
///     mutation switches lights on in rooms nobody chose.
/// </remarks>
[TestClass]
public sealed class AreaViewTests
{
	private static AreaConfig Room(string areaId, bool? enabled = null) =>
		new() { Name = areaId, AreaId = areaId, Enabled = enabled };

	// ===================== the override count =====================

	/// <summary>
	///     Sixteen, and sixteen for a reason: every setting a room can override except <c>Enabled</c>, which the
	///     header switch took over. Counted from the model rather than asserted as a bare number, so a setting
	///     added to <see cref="AreaSettings"/> tomorrow fails this test instead of quietly making the editor's
	///     "n of 16" and the re-setup warning both wrong.
	/// </summary>
	[TestMethod]
	public void The_Override_Count_Is_Every_Room_Setting_But_The_Switch()
	{
		string[] overridable =
		[
			.. typeof(AreaSettings)
				.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Select(property => property.Name)
				.Where(name => !string.Equals(name, nameof(AreaSettings.Enabled), StringComparison.Ordinal))
		];

		Assert.AreEqual(overridable.Length, AreaView.OverridableSettingCount,
			"the denominator in 'n of 16 changed' is every per-room setting except the header switch");

		// And every one of them really is overridable on the room, or the editor would be offering a count of
		// settings it cannot actually change.
		foreach (string name in overridable)
		{
			Assert.IsNotNull(typeof(AreaConfig).GetProperty(name),
				$"AreaConfig must carry a nullable {name} for the override row to exist");
		}

		Assert.IsNotNull(typeof(AreaConfig).GetProperty(nameof(AreaConfig.Enabled)),
			"Enabled stays on the room — it moved to the header toggle, it did not leave the model");
	}

	// ===================== the edge colour =====================

	/// <summary>
	///     The settings list borrows the dashboard's families so both pages describe one room one way.
	/// </summary>
	[TestMethod]
	public void The_Edge_Follows_The_Dashboards_Colour_Families()
	{
		Assert.AreEqual("family-machine", AreaView.EdgeClass(true, AreaState.AutoActive));
		Assert.AreEqual("family-machine", AreaView.EdgeClass(true, AreaState.AutoVacant));
		Assert.AreEqual("family-machine", AreaView.EdgeClass(true, AreaState.PreOff));
		Assert.AreEqual("family-human", AreaView.EdgeClass(true, AreaState.OverriddenOn));
		Assert.AreEqual("family-human", AreaView.EdgeClass(true, AreaState.SuppressedOff));
		Assert.AreEqual("family-idle", AreaView.EdgeClass(true, AreaState.Away));
		Assert.AreEqual("family-idle", AreaView.EdgeClass(true, AreaState.SceneHold));
	}

	/// <summary>A room nothing has been heard about is idle, not blank: the page opens before the first report.</summary>
	[TestMethod]
	public void A_Room_With_No_Snapshot_Yet_Renders_Idle()
	{
		Assert.AreEqual("family-idle", AreaView.EdgeClass(true, null));
	}

	/// <summary>
	///     A switched-off room is flat grey whatever the engine last said about it. The edge must never contradict
	///     the switch beside it — "the engine is acting here" next to an off switch is two answers to one question.
	/// </summary>
	[TestMethod]
	public void A_Switched_Off_Room_Is_Flat_Grey_Whatever_It_Last_Did()
	{
		foreach (AreaState state in Enum.GetValues<AreaState>())
			Assert.AreEqual("family-off", AreaView.EdgeClass(false, state), $"{state} with the switch off");

		Assert.AreEqual("family-off", AreaView.EdgeClass(false, null));
	}

	// ===================== enablement =====================

	/// <summary>Inheritance still reads: a room that states nothing follows the all-rooms setting.</summary>
	[TestMethod]
	public void A_Room_That_States_Nothing_Follows_All_Rooms()
	{
		AreaSettings on = new() { Enabled = true };
		AreaSettings off = new() { Enabled = false };

		Assert.IsTrue(AreaView.IsEnabled(Room("stue"), on));
		Assert.IsFalse(AreaView.IsEnabled(Room("stue"), off));
		Assert.IsFalse(AreaView.IsEnabled(Room("stue", enabled: false), on), "an explicit no beats the default yes");
		Assert.IsTrue(AreaView.IsEnabled(Room("stue", enabled: true), off));
	}

	/// <summary>
	///     The bulk action writes explicit values on every room it touches. Never <c>null</c>: inheritance stays
	///     for documents that predate the switch, but a decision somebody just made with a button belongs in the
	///     file as itself — left inheriting, it would silently follow a later change to the all-rooms default.
	/// </summary>
	[TestMethod]
	public void Switching_A_Floor_Writes_Explicit_Values_Everywhere()
	{
		AreaConfig[] floor = [Room("stue"), Room("kjokken", enabled: false), Room("gang", enabled: true)];

		int changed = AreaView.SwitchAll(floor, true);

		Assert.AreEqual(2, changed, "the room already explicitly on did not change");
		Assert.IsTrue(floor.All(area => area.Enabled == true));
		Assert.IsFalse(floor.Any(area => area.Enabled is null), "inheriting is never the answer a button writes");

		AreaView.SwitchAll(floor, false);
		Assert.IsTrue(floor.All(area => area.Enabled == false));
	}

	/// <summary>
	///     What the floor's action offers next. An empty group is never "all on", or a floor with no rooms would
	///     offer to switch nothing off.
	/// </summary>
	[TestMethod]
	public void A_Floor_Offers_Switching_Off_Only_When_Every_Room_Is_On()
	{
		AreaSettings defaults = new() { Enabled = true };

		Assert.IsTrue(AreaView.AllEnabled([Room("stue"), Room("gang", enabled: true)], defaults),
			"one inherits the default yes, the other says yes");
		Assert.IsFalse(AreaView.AllEnabled([Room("stue"), Room("gang", enabled: false)], defaults));
		Assert.IsFalse(AreaView.AllEnabled([], defaults), "nothing to switch off");
	}

	// ===================== floor headers =====================

	/// <summary>
	///     The degradation rule, from the renderer's side: a house with no floors is one unnamed group and gets no
	///     headers at all, so it never learns the feature exists. "Other rooms" is therefore never the only heading.
	/// </summary>
	[TestMethod]
	public void A_House_With_No_Floors_Gets_No_Headers()
	{
		Assert.IsFalse(AreaView.ShowsHeader(1, null), "one unnamed group is exactly today's flat list");
		Assert.IsTrue(AreaView.ShowsHeader(1, new AreaFloor("ground", "Ground floor", 0)));
		Assert.IsTrue(AreaView.ShowsHeader(2, null), "floorless rooms beside a floored one earn their heading");

		Assert.AreEqual("Other rooms", AreaView.FloorTitle(null));
		Assert.AreEqual("Loftet", AreaView.FloorTitle(new AreaFloor("loft", "Loftet", 2)));
	}
}
