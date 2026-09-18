using AdaptiveLighting.Engine;
using AdaptiveLighting.Tests.Common;

namespace AdaptiveLighting.Tests.Lighting;

public sealed partial class AreaControllerTests
{
	[TestMethod]
	public void Leaving_Away_Lights_A_WelcomeHome_Area_When_It_Is_Dark()
	{
		AreaFixture t = Build(s => s.WelcomeHome = true);
		t.House.OnNext(AwayHouse());
		t.Actuator.Clear();

		t.House.OnNext(House());

		Assert.AreEqual(AreaState.AutoActive, t.Area.State);
		Assert.IsTrue(t.Actuator.Last is { On: true, BrightnessPct: 70 });
	}

	[TestMethod]
	public void Leaving_Away_Leaves_An_Ordinary_Area_Dark()
	{
		AreaFixture t = Build();
		t.House.OnNext(AwayHouse());
		t.Actuator.Clear();

		t.House.OnNext(House());

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}

	[TestMethod]
	public void A_WelcomeHome_Area_Stays_Dark_When_It_Is_Not_Dark()
	{
		AreaFixture t = Build(s => s.WelcomeHome = true);
		t.Ha.SetState(Lux, "5000");
		t.House.OnNext(AwayHouse());
		t.Actuator.Clear();

		t.House.OnNext(House());

		Assert.AreEqual(AreaState.AutoVacant, t.Area.State);
		Assert.AreEqual(0, t.Actuator.Applied.Count);
	}
}
