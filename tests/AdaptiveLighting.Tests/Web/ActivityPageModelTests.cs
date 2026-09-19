using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;
using AdaptiveLighting.Web.Presentation;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Web;

/// <summary>The activity page's room and category filters, moved off the component into <see cref="ActivityPageModel"/>.</summary>
/// <remarks>The page model is a plain class precisely so this can be asked directly, with no renderer.</remarks>
[TestClass]
public sealed class ActivityPageModelTests
{
	[TestMethod]
	public void RoomAndCategoryFiltersNarrowTheSameEntriesTheChipsCount()
	{
		ActivityLog log = new();

		log.Record(Snapshot("Stue", "stue", AreaState.AutoActive, TransitionReason.Motion));
		log.Record(Snapshot("Kjøkken", "kjokken", AreaState.OverriddenOn, TransitionReason.ManualOn));

		ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
		AreaSnapshotCache cache = new(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AreaSnapshotCache>.Instance, log);

		ActivityPageModel model = new(log, cache, NullLogger.Instance, work =>
		{
			work();

			return Task.CompletedTask;
		});

		model.Start();

		Assert.AreEqual(2, model.Entries.Count, "both rooms' reports are read in");
		Assert.AreEqual(2, model.RoomOptions.Count);

		model.SelectRoom("stue");

		Assert.AreEqual("stue", model.Room);
		Assert.AreEqual("Stue", model.RoomName);
		Assert.AreEqual(1, model.Rows.Count, "the room filter leaves only Stue's report");

		ActivityFilterChip movement = model.Chips.Single(chip => chip.Category == ActivityCategory.Movement);
		Assert.AreEqual(1, movement.Count, "chip counts are within the chosen room, not the house");
		Assert.IsTrue(movement.IsOn);

		ActivityFilterChip manual = model.Chips.Single(chip => chip.Category == ActivityCategory.ManualChange);
		Assert.AreEqual(0, manual.Count, "Kjøkken's report is filtered out by the room, so it counts on no chip");

		model.Toggle(ActivityCategory.Movement);

		Assert.AreEqual(0, model.Rows.Count, "switching off the only category left in this room hides every row");
		Assert.IsNotNull(model.HiddenNote, "the note explains why the room now shows nothing");

		model.ShowEverything();

		Assert.AreEqual(ActivityView.AllRooms, model.Room);
		Assert.AreEqual(2, model.Rows.Count, "every room and every category is back on");
	}

	private static AreaSnapshot Snapshot(string areaName, string areaId, AreaState state, TransitionReason reason) => new(
		areaName, state, reason, ModeKind.Normal,
		KillSwitchActive: false, IsDark: true, PeriodName: "Kveld", BrightnessPct: 62.5, ColorTempKelvin: 2700,
		Timestamp: DateTimeOffset.UnixEpoch, LastCommandAt: null, LastMotionAt: null, NextChangeAt: null,
		NextChangeFrom: null, AreaId: areaId);
}
