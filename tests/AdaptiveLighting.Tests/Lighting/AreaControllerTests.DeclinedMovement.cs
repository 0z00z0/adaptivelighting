using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Fixture = AdaptiveLighting.Tests.Common.AreaFixture;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	// Movement into a blocked room publishes a report, and the report is bounded: the comparison is on the
	// refusing gate, so the count over any interval follows gate changes, never footfall.

	[TestMethod]
	public void Forty_Walks_Under_One_Unchanged_Block_Produce_One_Report()
	{
		Fixture t = Build();
		t.Ha.SetState(Lux, "5000");
		t.Publisher.Snapshots.Clear();

		for (int walk = 0; walk < 40; walk++)
		{
			t.Ha.Trigger(Motion, "off");
			t.Ha.Trigger(Motion, "on");
		}

		AreaSnapshot[] declined = [.. t.Publisher.Snapshots.Where(s => s.Reason == TransitionReason.Motion)];

		Assert.AreEqual(1, declined.Length, "one refusal reported, not forty — the reason never changed");
		Assert.AreEqual(AutoOnBlock.NotDark, declined[0].AutoOnBlockedBy);
		Assert.AreEqual(AreaState.AutoVacant, declined[0].State);
		Assert.AreEqual(0, t.Actuator.Applied.Count, "and still no light, which is the point of the row");
	}

	// AutoOnBlockedBy is kept out of HasSameMeaningAs, so a drifting lux reading cannot republish every area.
	[TestMethod]
	public void A_Drifting_Reading_Under_One_Unchanged_Reason_Adds_No_Row()
	{
		Fixture t = Build();
		t.Ha.SetState(Lux, "5000");
		t.Ha.Trigger(Motion, "on");
		t.Publisher.Snapshots.Clear();

		foreach (string reading in new[] { "5100", "5300", "4900", "6000" })
		{
			t.Ha.SetState(Lux, reading);
			t.Ha.Trigger(Motion, "off");
			t.Ha.Trigger(Motion, "on");
		}

		Assert.AreEqual(0, t.Publisher.Snapshots.Count(s => s.Reason == TransitionReason.Motion),
			"four different readings, one unchanged verdict, nothing new to say");
	}

	// The bound must not swallow a second spell: blocked, then lit, then blocked by the same gate is two reports.
	[TestMethod]
	public void A_Changed_Reason_Reports_Again_And_So_Does_A_Block_That_Returns()
	{
		Fixture t = Build(ignoreWhenOn: [Blocker]);
		t.Ha.SetState(Lux, "5000");
		t.Publisher.Snapshots.Clear();

		t.Ha.Trigger(Motion, "on");
		t.Ha.Trigger(Motion, "off");

		// Same room, different gate: the television goes on and outranks the darkness verdict.
		t.Ha.SetState(Blocker, "on");
		t.Ha.Trigger(Motion, "on");

		AutoOnBlock?[] reasons = [.. t.Publisher.Snapshots
			.Where(s => s.Reason == TransitionReason.Motion)
			.Select(s => s.AutoOnBlockedBy)];

		CollectionAssert.AreEqual(new AutoOnBlock?[] { AutoOnBlock.NotDark, AutoOnBlock.EntityOn }, reasons,
			"two different refusals are two rows, and the second names the entity rather than the sensor");

		Assert.AreEqual(Blocker, t.Publisher.Snapshots.Last(s => s.Reason == TransitionReason.Motion).AutoOnBlockingEntity);

		// Everything clears, the room lights, and then the same block returns.
		t.Ha.SetState(Blocker, "off");
		t.Ha.SetState(Lux, "5");
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");
		Assert.AreEqual(AreaState.AutoActive, t.Area.State, "nothing is in the way now, so the room lights");

		t.Ha.SetState(Lux, "5000");
		Advance(t, TimeSpan.FromMinutes(11));   // vacancy, pre-off, back to AutoVacant
		t.Publisher.Snapshots.Clear();
		t.Ha.Trigger(Motion, "off");
		t.Ha.Trigger(Motion, "on");

		Assert.AreEqual(1, t.Publisher.Snapshots.Count(s => s.Reason == TransitionReason.Motion),
			"the room lit in between, so the refusal that came back is news again rather than a repeat");
	}

	/// <summary>Every gate that can refuse movement produces a report naming itself.</summary>
	[TestMethod]
	public void Every_Refusal_Names_Itself_On_A_Declined_Movement()
	{
		Assert.AreEqual(AutoOnBlock.NotDark, FirstDeclined(Build(), t => t.Ha.SetState(Lux, "5000")));

		Assert.AreEqual(AutoOnBlock.Disabled, FirstDeclined(Build(s => s.Enabled = false), _ => { }));

		Assert.AreEqual(AutoOnBlock.KillSwitch, FirstDeclined(Build(), t => t.House.OnNext(House(killed: true))));

		Assert.AreEqual(AutoOnBlock.Away, FirstDeclined(Build(), t => t.House.OnNext(AwayHouse())));

		Assert.AreEqual(AutoOnBlock.Sleep,
			FirstDeclined(Build(s => s.SleepBlocksAutoOn = true), t => t.House.OnNext(House(kind: ModeKind.Sleep))));

		Assert.AreEqual(AutoOnBlock.EntityOn,
			FirstDeclined(Build(ignoreWhenOn: [Blocker]), t => t.Ha.SetState(Blocker, "on")));

		Assert.AreEqual(AutoOnBlock.SceneHold,
			FirstDeclined(Build(), t => t.House.OnNext(House(kind: ModeKind.Guest, modeValue: "Gjester", scene: "scene.gjest"))),
			"the fourth silent refusal: a guest scene holds the room, and saying 'not dark enough' would be a lie");
	}

	/// <summary>Arranges a block, walks through the room once, and hands back the gate the report named.</summary>
	private static AutoOnBlock? FirstDeclined(Fixture fixture, Action<Fixture> block)
	{
		block(fixture);
		fixture.Publisher.Snapshots.Clear();
		fixture.Ha.Trigger(Motion, "on");

		return fixture.Publisher.Snapshots.Single(s => s.Reason == TransitionReason.Motion).AutoOnBlockedBy;
	}
}
