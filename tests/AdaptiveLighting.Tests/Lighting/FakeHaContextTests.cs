namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The fixture's own recursion-budget guard, independent of any code that uses it.</summary>
[TestClass]
public sealed class FakeHaContextTests
{
	[TestMethod]
	public void A_Fresh_Context_Already_Carries_The_Default_Budget()
	{
		FakeHaContext ha = new();

		Assert.AreEqual(FakeHaContext.DefaultStateReadBudget, ha.StateReadBudget,
			"unset by default used to mean unguarded; a future self-referencing walk must be caught without opting in");
	}

	// Stands in for a walk with no visited set. Nothing here sets StateReadBudget, so this is what proves the
	// default alone stops an unbounded read pattern rather than hanging the run.
	[TestMethod]
	public void An_Unbounded_Read_Pattern_Is_Caught_By_The_Default_Budget_Alone()
	{
		FakeHaContext ha = new();
		ha.SetState("light.a", "off");

		InvalidOperationException thrown = Assert.ThrowsException<InvalidOperationException>(() =>
		{
			for (int i = 0; i < FakeHaContext.DefaultStateReadBudget + 1; i++)
				ha.GetState("light.a");
		});

		StringAssert.Contains(thrown.Message, FakeHaContext.DefaultStateReadBudget.ToString());
		Assert.AreEqual(FakeHaContext.DefaultStateReadBudget + 1, ha.StateReads,
			"it must throw on the read one past the budget, not before and not after");
	}
}
