using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.HassModel;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Who the activity log says made a change. A wrong name sends somebody after the wrong automation.</summary>
[TestClass]
public sealed class ChangeOriginNamesTests
{
	[TestMethod]
	public void An_Automation_Is_Named_After_Its_Run_Or_Called_An_Automation_When_The_Run_Was_Not_Seen()
	{
		FakeHaContext ha = new();
		using ChangeOriginNames names = new(ha, NullLogger.Instance);

		ha.RaiseEvent(
			ChangeOriginNames.AutomationTriggeredEvent,
			new { name = "Evening lights", entity_id = "automation.evening_lights" },
			new Context { Id = "run" });

		Assert.AreEqual("By automation: Evening lights",
			names.Describe(ChangeOrigin.Automation, new Context { Id = "run", ParentId = "trigger" }));
		Assert.AreEqual("By automation: Evening lights",
			names.Describe(ChangeOrigin.Automation, new Context { Id = "script-run", ParentId = "run" }),
			"a script the automation called is named after the automation, one level up as the logbook looks");
		Assert.AreEqual("By an automation",
			names.Describe(ChangeOrigin.Automation, new Context { Id = "other-run", ParentId = "other-trigger" }),
			"a run the engine never saw still names its kind of cause, never the wrong automation");
	}

	[TestMethod]
	public void A_Person_Is_Named_After_Their_Person_Entity_Or_Called_A_Home_Assistant_User_Without_One_And_A_Change_With_Neither_Is_An_Unknown_Source()
	{
		FakeHaContext ha = new();
		ha.SetState("person.alex", "home", new() { ["user_id"] = "user-1", ["friendly_name"] = "Alex" });
		using ChangeOriginNames names = new(ha, NullLogger.Instance);

		Assert.AreEqual("By Alex", names.Describe(ChangeOrigin.HaUser, new Context { Id = "c", UserId = "user-1" }));
		Assert.AreEqual("By a Home Assistant user",
			names.Describe(ChangeOrigin.HaUser, new Context { Id = "c", UserId = "user-with-no-person" }));
		Assert.AreEqual("Unknown source", names.Describe(ChangeOrigin.PhysicalDevice, new Context { Id = "c" }));
	}
}
