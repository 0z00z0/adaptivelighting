using AdaptiveLighting.Engine;

using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.HassModel;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Who the activity log says made a change. A wrong name sends somebody after the wrong automation.</summary>
[TestClass]
public sealed class ChangeOriginNamesTests
{
	[TestMethod]
	public void An_Automation_Is_Named_After_The_Run_Whose_Context_The_Change_Carries()
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
		Assert.IsNull(names.Describe(ChangeOrigin.Automation, new Context { Id = "other-run", ParentId = "other-trigger" }),
			"a run the engine never saw has no name, so the row keeps its old wording rather than naming the wrong one");
	}

	[TestMethod]
	public void A_Person_Is_Named_After_The_Person_Entity_Carrying_Their_User_Id()
	{
		FakeHaContext ha = new();
		ha.SetState("person.alex", "home", new() { ["user_id"] = "user-1", ["friendly_name"] = "Alex" });
		using ChangeOriginNames names = new(ha, NullLogger.Instance);

		Assert.AreEqual("By Alex", names.Describe(ChangeOrigin.HaUser, new Context { Id = "c", UserId = "user-1" }));
		Assert.IsNull(names.Describe(ChangeOrigin.HaUser, new Context { Id = "c", UserId = "user-with-no-person" }));
		Assert.AreEqual("At the device or wall switch", names.Describe(ChangeOrigin.PhysicalDevice, new Context { Id = "c" }));
	}
}
