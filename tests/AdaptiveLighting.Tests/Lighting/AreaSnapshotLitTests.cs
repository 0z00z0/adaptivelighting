using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Ha;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Whether a room counts as lit, read off its snapshot.</summary>
[TestClass]
public sealed class AreaSnapshotLitTests
{
	private static readonly Regex LitField = new("\"(is_lit|IsLit)\"\\s*:", RegexOptions.IgnoreCase);

	[TestMethod]
	[DataRow(AreaState.Disabled, false)]
	[DataRow(AreaState.Away, false)]
	[DataRow(AreaState.AutoVacant, false)]
	[DataRow(AreaState.AutoActive, true)]
	[DataRow(AreaState.PreOff, true)]
	[DataRow(AreaState.OverriddenOn, true)]
	[DataRow(AreaState.SuppressedOff, false)]
	[DataRow(AreaState.SceneHold, false)]
	public void Each_State_At_A_Brightness_Above_Zero_Decides_Whether_The_Room_Is_Lit(AreaState state, bool lit)
	{
		Assert.AreEqual(lit, Snapshot(state, 40).IsLit, $"{state} at 40 %");
	}

	[TestMethod]
	[DataRow(AreaState.AutoActive)]
	[DataRow(AreaState.PreOff)]
	[DataRow(AreaState.OverriddenOn)]
	public void A_Lit_State_With_No_Brightness_Is_Not_Lit(AreaState state)
	{
		Assert.IsFalse(Snapshot(state, 0).IsLit, $"{state} at 0 %");
		Assert.IsFalse(Snapshot(state, null).IsLit, $"{state} with no brightness");
	}

	[TestMethod]
	public void Every_State_Is_Covered_By_The_Lit_Rule_Test()
	{
		MethodInfo test = typeof(AreaSnapshotLitTests).GetMethod(nameof(Each_State_At_A_Brightness_Above_Zero_Decides_Whether_The_Room_Is_Lit))!;
		HashSet<AreaState> covered = [.. test.GetCustomAttributes<DataRowAttribute>().Select(row => (AreaState)row.Data[0]!)];

		CollectionAssert.AreEquivalent(Enum.GetValues<AreaState>(), covered.ToArray(), "a new state needs its own row saying whether it is lit");
	}

	// Consumers bind the event by field name, so a computed verdict must never join it.
	[TestMethod]
	public void The_Published_Event_Carries_No_Lit_Field()
	{
		Assert.IsTrue(LitField.IsMatch("{\"is_held_lit\":true,\"is_lit\":true}"), "control: the check finds a lit field when one is there");
		Assert.IsFalse(LitField.IsMatch("{\"is_held_lit\":true,\"held_lit_by\":null}"), "control: the held-lit fields are not mistaken for one");

		FakeHaContext ha = new();
		HaStatePublisher publisher = new(ha, NullLogger.Instance);
		AreaSnapshot snapshot = Snapshot(AreaState.AutoActive, 40);

		publisher.Publish(snapshot);

		string json = JsonSerializer.Serialize(ha.SentEvents.Single().Data);

		Assert.IsTrue(snapshot.IsLit, "the published snapshot is lit, so a leaked field would be present");
		Assert.IsFalse(LitField.IsMatch(json), $"the event carries the state and brightness, never the verdict: {json}");
	}

	private static AreaSnapshot Snapshot(AreaState state, double? brightness) => new(
		"Room", state, TransitionReason.Motion, ModeKind.Normal,
		KillSwitchActive: false, IsDark: true, PeriodName: "evening", BrightnessPct: brightness, ColorTempKelvin: 2700,
		Timestamp: DateTimeOffset.UnixEpoch, LastCommandAt: null, LastMotionAt: null, NextChangeAt: null,
		NextChangeFrom: null);
}
