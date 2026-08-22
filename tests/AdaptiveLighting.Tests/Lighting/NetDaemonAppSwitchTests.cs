using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;

using NetDaemon.AppModel;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The master-switch default: the slug <see cref="NetDaemonAppSwitch"/> derives, and the <see cref="GlobalConfig.EffectiveKillSwitchEntity"/> view it feeds.</summary>
[TestClass]
public sealed class NetDaemonAppSwitchTests
{
	[TestMethod]
	public void EntityIdForTypeName_MatchesNetDaemonStateManagerSlug()
	{
		// The ids the state manager emits: NetDaemon becomes net_daemon, PascalCase splits, dots become underscores.
		Assert.AreEqual(
			"input_boolean.netdaemon_example_net_daemon_home_adaptive_lighting_app",
			NetDaemonAppSwitch.EntityIdForTypeName("Example.NetDaemon.Home.AdaptiveLightingApp"));

		Assert.AreEqual(
			"input_boolean.netdaemon_example_net_daemon_site1_generic_trigger",
			NetDaemonAppSwitch.EntityIdForTypeName("Example.NetDaemon.Site1.GenericTrigger"));

		Assert.AreEqual(
			"input_boolean.netdaemon_example_net_daemon_site1_adaptive_lighting_app",
			NetDaemonAppSwitch.EntityIdForTypeName("Example.NetDaemon.Site1.AdaptiveLightingApp"));
	}

	[TestMethod]
	public void EntityIdFor_UsesTheTypesFullName_WhenNoIdPinned()
	{
		Assert.AreEqual(
			NetDaemonAppSwitch.EntityIdForTypeName(typeof(NetDaemonAppSwitchTests).FullName!),
			NetDaemonAppSwitch.EntityIdFor(typeof(NetDaemonAppSwitchTests)));
	}

	[TestMethod]
	public void EntityIdFor_UsesTheExplicitNetDaemonAppId_WhenPinned()
	{
		// An app pinning [NetDaemonApp(Id = "adaptive_lighting")] gets the short id as its enable switch, and with it the master switch.
		Assert.AreEqual(
			"input_boolean.netdaemon_adaptive_lighting",
			NetDaemonAppSwitch.EntityIdFor(typeof(PinnedIdApp)));
	}

	[NetDaemonApp(Id = "adaptive_lighting")]
	private sealed class PinnedIdApp;

	[TestMethod]
	public void EffectiveKillSwitch_PrefersExplicit_ElseDefault()
	{
		var global = new GlobalConfig();
		Assert.IsNull(global.EffectiveKillSwitchEntity, "unset with no default resolves to nothing");

		global.DefaultKillSwitchEntity = "input_boolean.netdaemon_x";
		Assert.AreEqual("input_boolean.netdaemon_x", global.EffectiveKillSwitchEntity, "the default fills in");

		global.KillSwitchEntity = "switch.explicit";
		Assert.AreEqual("switch.explicit", global.EffectiveKillSwitchEntity, "an explicit entity wins over the default");
	}
}
