using System.Reflection;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>
///     The Areas section's decisions: which colour a room's edge takes, what the floor bulk action writes, and
///     how many settings the override count is counting.
/// </summary>
[TestClass]
public sealed class AreaViewTests
{
	private static AreaConfig Room(string areaId, bool? enabled = null) =>
		new() { Name = areaId, AreaId = areaId, Enabled = enabled };

	// ===================== the override count =====================

	// Counted off the model, never written as a literal, so a property added to AreaSettings fails here instead
	// of quietly making the editor's "n of N" and the re-setup warning both wrong.
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

		// And every one really is overridable on the room, or the editor counts settings it cannot change.
		foreach (string name in overridable)
		{
			Assert.IsNotNull(typeof(AreaConfig).GetProperty(name),
				$"AreaConfig must carry a nullable {name} for the override row to exist");
		}

		Assert.IsNotNull(typeof(AreaConfig).GetProperty(nameof(AreaConfig.Enabled)),
			"Enabled stays on the room — it moved to the header toggle, it did not leave the model");
	}

	// ===================== the edge colour =====================

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

	// The page opens before the first report arrives.
	[TestMethod]
	public void A_Room_With_No_Snapshot_Yet_Renders_Idle()
	{
		Assert.AreEqual("family-idle", AreaView.EdgeClass(true, null));
	}

	[TestMethod]
	public void A_Switched_Off_Room_Is_Flat_Grey_Whatever_It_Last_Did()
	{
		foreach (AreaState state in Enum.GetValues<AreaState>())
			Assert.AreEqual("family-off", AreaView.EdgeClass(false, state), $"{state} with the switch off");

		Assert.AreEqual("family-off", AreaView.EdgeClass(false, null));
	}

	// ===================== enablement =====================

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

	// The bulk action writes explicit values, never null. Left inheriting, a room would silently follow a later
	// change to the all-rooms default.
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

	// A house with no floors is one unnamed group, so "Other rooms" is never the only heading.
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
