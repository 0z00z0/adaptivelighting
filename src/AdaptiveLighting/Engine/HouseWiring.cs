using System.Reactive.Concurrency;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;
using AdaptiveLighting.LastSeen;

namespace AdaptiveLighting.Engine;

/// <summary>Everything house-wide an area controller is built on.</summary>
/// <remarks>
///     Composed once in <see cref="LightingOrchestrator"/> and handed to every room, so no two rooms can end up
///     on a different actuator, publisher, house-state stream or origin reader.
/// </remarks>
// No member has a default: a wiring that leaves one out does not compile, which is the whole point of the
// record. LastSeen is null for a house with no cache, and OriginNames names nobody when null.
public sealed record HouseWiring(
	IHaContext Ha,
	IScheduler Scheduler,
	GlobalConfig Global,
	IReadOnlyList<TimePeriodConfig> Periods,
	ILightActuator Actuator,
	IStatePublisher Publisher,
	IObservable<HouseState> HouseChanged,
	ILoggerFactory LoggerFactory,
	IEntityLastSeen? LastSeen,
	ChangeOriginNames? OriginNames);
