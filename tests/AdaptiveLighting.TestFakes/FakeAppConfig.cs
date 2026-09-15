using AdaptiveLighting.Configuration;

using NetDaemon.AppModel;

namespace AdaptiveLighting.TestFakes;

/// <summary>An <see cref="IAppConfig{T}"/> whose value is fixed at construction, standing in for NetDaemon's frozen config.</summary>
public sealed class FakeAppConfig(AdaptiveLightingConfig value) : IAppConfig<AdaptiveLightingConfig>
{
	public AdaptiveLightingConfig Value { get; } = value;
}
