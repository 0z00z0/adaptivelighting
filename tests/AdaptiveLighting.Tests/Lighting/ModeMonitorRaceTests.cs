using AdaptiveLighting.Configuration;
using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Reactive.Testing;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>What the mode monitor does when Home Assistant's thread and its own timers land at the awkward moment.</summary>
[TestClass]
public sealed class ModeMonitorRaceTests
{
	private const string Select = "input_select.house_mode";
	private const string Hall = "binary_sensor.hall_motion";
	private const string Kitchen = "binary_sensor.kitchen_motion";
	private static readonly DateTimeOffset Evening = new(2026, 1, 15, 20, 0, 0, TimeSpan.Zero);

	private sealed record Rig(TestScheduler Scheduler, InterleavingHaContext Ha, CapturingScheduler Timers, ModeMonitor Monitor);

	private static HouseModeConfig Mode(int awayGraceMinutes, bool sleepResetsOnKitchen) => new()
	{
		Entity = Select,
		Options =
		[
			new() { Value = "Home", Kind = ModeKind.Normal },
			new()
			{
				Value = "Sleep",
				Kind = ModeKind.Sleep,
				ResetOnPresence = sleepResetsOnKitchen,
				ResetPresenceSensors = sleepResetsOnKitchen ? [Kitchen] : [],
				ResetPresenceGraceMinutes = 0
			},
			new()
			{
				Value = "Away",
				Kind = ModeKind.Away,
				ResetOnPresence = true,
				ResetPresenceSensors = [Hall],
				ResetPresenceGraceMinutes = awayGraceMinutes
			}
		]
	};

	private static Rig Started(HouseModeConfig mode)
	{
		TestScheduler scheduler = new();
		scheduler.AdvanceTo(Evening.Ticks);

		FakeHaContext inner = new();
		inner.SetState(Select, "Home");
		inner.SetState(Hall, "off");
		inner.SetState(Kitchen, "off");

		InterleavingHaContext ha = new(inner);
		CapturingScheduler timers = new(scheduler);

		List<TimePeriodConfig> periods =
		[
			new() { Name = "morning", Start = "06:30", BrightnessPct = 60, ColorTempKelvin = 3000 },
			new() { Name = "evening", Start = "18:00", BrightnessPct = 60, ColorTempKelvin = 2700 }
		];

		ModeMonitor monitor = new(
			ha, new GlobalConfig { CircadianTickSeconds = 60, HouseMode = mode }, NullLogger.Instance, timers,
			periods, () => SunTimes.Unknown, [], zone: TimeZoneInfo.Utc);

		monitor.Start();
		return new Rig(scheduler, ha, timers, monitor);
	}

	private static int SelectCalls(Rig rig, string option) =>
		rig.Ha.Inner.Calls.Count(call =>
			call.Domain == "input_select"
			&& call.Service == "select_option"
			&& call.Option() == option);

	[TestMethod]
	public void A_Grace_Expiry_Already_In_Flight_Writes_Nothing_Once_The_Monitor_Is_Disposed()
	{
		Rig rig = Started(Mode(awayGraceMinutes: 15, sleepResetsOnKitchen: false));
		rig.Ha.Inner.Trigger(Select, "Away");

		rig.Scheduler.AdvanceBy(TimeSpan.FromMinutes(5).Ticks);
		rig.Ha.Inner.Trigger(Hall, "on");

		Action graceEnds = rig.Timers.LastScheduled(TimeSpan.FromMinutes(15));

		rig.Monitor.Dispose();

		Exception? thrown = null;
		try
		{
			graceEnds();
		}
		catch (ObjectDisposedException exception)
		{
			thrown = exception;
		}

		Assert.AreEqual(0, SelectCalls(rig, "Home"), "a discarded monitor wrote the house-mode select");
		Assert.IsNull(thrown, "a discarded monitor announced a change on a disposed stream");
	}

	[TestMethod]
	public void A_Presence_Arrival_Already_Delivered_Writes_Nothing_Once_The_Monitor_Is_Disposed()
	{
		Rig rig = Started(Mode(awayGraceMinutes: 15, sleepResetsOnKitchen: false));
		rig.Ha.Inner.Trigger(Select, "Away");
		rig.Scheduler.AdvanceBy(TimeSpan.FromMinutes(16).Ticks);

		// The arrival's handler is running when the save disposes the monitor: its first read of the select is the moment.
		rig.Ha.BeforeNextRead(Select, rig.Monitor.Dispose);
		rig.Ha.Inner.Trigger(Hall, "on");

		Assert.AreEqual(0, SelectCalls(rig, "Home"), "a discarded monitor wrote the house-mode select");
	}

	[TestMethod]
	public void A_Reset_Landing_While_The_Tick_Expires_An_Older_One_Is_Not_Wiped()
	{
		Rig rig = Started(Mode(awayGraceMinutes: 0, sleepResetsOnKitchen: true));
		rig.Ha.Inner.Trigger(Select, "Away");

		// A reset whose write the select never takes: the tick is owed a look at it.
		rig.Ha.Inner.Trigger(Hall, "on");
		rig.Ha.Inner.Trigger(Hall, "off");
		Assert.AreEqual("Home", rig.Monitor.CurrentModeValue, "the reset is acted on before its echo");

		// Between the tick reading the assumption and clearing it: somebody picks Sleep, and an arrival resets that too.
		rig.Ha.BeforeNextRead(Select, () =>
		{
			rig.Ha.Inner.Trigger(Select, "Sleep");
			rig.Ha.Inner.Trigger(Kitchen, "on");
			rig.Ha.Inner.Trigger(Kitchen, "off");
		});

		rig.Scheduler.AdvanceBy(TimeSpan.FromSeconds(60).Ticks);

		Assert.AreEqual(2, SelectCalls(rig, "Home"), "the second reset has to have landed inside the tick");
		Assert.AreEqual("Home", rig.Monitor.CurrentModeValue, "the fresh reset was wiped by the tick expiring the old one");
	}
}
