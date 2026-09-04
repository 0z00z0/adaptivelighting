using System.Reactive.Concurrency;

using AdaptiveLighting.Hosting;

using NetDaemon.AppModel;
using NetDaemon.HassModel;

namespace MinimalHost;

// Hands the engine its Home Assistant connection. This is the whole app: AdaptiveLighting owns
// every rule, every room and the UI — nothing else needs writing here.
[NetDaemonApp(Id = "adaptive_lighting")]
internal sealed class AdaptiveLightingApp : IAsyncDisposable
{
	private readonly LightingEngineHost _engine;

	public AdaptiveLightingApp(LightingEngineHost engine, IHaContext ha, IHaRegistry registry, IScheduler scheduler)
	{
		_engine = engine;
		_engine.Attach(ha, registry, scheduler, NetDaemonAppSwitch.EntityIdFor(GetType()));
		_engine.Reload();
	}

	public ValueTask DisposeAsync()
	{
		_engine.Detach();
		return ValueTask.CompletedTask;
	}
}
