using System.Reactive.Subjects;
using System.Text.Json;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

using NetDaemon.AppModel;
using NetDaemon.HassModel;
using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>Ordering: <see cref="ModeService.GetHouseMode"/> discovers a disconnection and <see cref="ModeService.GetToggles"/> runs next on the same render without clobbering it.</summary>
[TestClass]
public sealed class ModeServiceReadinessTests
{
	/// <summary>An <see cref="IHaContext"/> whose state cache is not up yet, so <see cref="GetState"/> throws.</summary>
	private sealed class DisconnectedHaContext : IHaContext
	{
		private readonly Subject<StateChange> _changes = new();
		private readonly Subject<Event> _events = new();

		IObservable<Event> IHaContext.Events => _events;

		public EntityState? GetState(string entityId) =>
			throw new InvalidOperationException("Home Assistant is not connected yet.");

		public IObservable<StateChange> StateAllChanges() => _changes;
		public IReadOnlyList<Entity> GetAllEntities() => [];
		public void CallService(string domain, string service, ServiceTarget? target = null, object? data = null) { }
		public Task<JsonElement?> CallServiceWithResponseAsync(string domain, string service, ServiceTarget? target = null, object? data = null) => Task.FromResult<JsonElement?>(null);
		public Area? GetAreaFromEntityId(string entityId) => null;
		public Entity Entity(string entityId) => new(this, entityId);
		public EntityRegistration? GetEntityRegistration(string entityId) => null;
		public void SendEvent(string eventType, object? data) { }
	}

	private sealed class FakeAppConfig(AdaptiveLightingConfig value) : IAppConfig<AdaptiveLightingConfig>
	{
		public AdaptiveLightingConfig Value { get; } = value;
	}

	[TestMethod]
	public void A_House_Mode_Only_Setup_With_HA_Down_Still_Reports_Not_Connected()
	{
		var config = new AdaptiveLightingConfig
		{
			Global = new GlobalConfig
			{
				HouseMode = new HouseModeConfig
				{
					Entity = "input_select.husmodus",
					Options = [new() { Value = "Normal", Kind = ModeKind.Normal }, new() { Value = "Sover", Kind = ModeKind.Sleep }]
				}
			}
		};

		var ha = new DisconnectedHaContext();
		var catalog = new HaCatalog(ha, new FakeHaRegistry(), NullLoggerFactory.Instance);
		var host = new LightingEngineHost(
			new LightingConfigStore(
				Path.Combine(Path.GetTempPath(), $"modeservice-{Guid.NewGuid():N}.yaml"),
				NullLogger<LightingConfigStore>.Instance),
			NullLoggerFactory.Instance);
		var service = new ModeService(ha, new FakeAppConfig(config), catalog, host, NullLogger<ModeService>.Instance);

		// The page renders both, in this order.
		_ = service.GetHouseMode();
		var toggles = service.GetToggles();

		Assert.AreEqual(0, toggles.Count, "no legacy toggle entities are configured");
		Assert.IsFalse(service.IsHomeAssistantReady,
			"GetToggles must not reset the false that GetHouseMode set when HA is down");
	}
}
