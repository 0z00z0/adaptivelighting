using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Extensions;

/// <summary>
///     Edge-triggered on/off subscriptions on <see cref="Entity"/>, built on <c>SubscribeSafe</c> so a thrown handler
///     is logged instead of killing the subscription.
/// </summary>
/// <remarks>
///     These filter on the new state only, so they fire on unavailable to on as well as off to on. Requiring the old
///     state to be off drops a sensor that comes back online already reporting motion.
/// </remarks>
public static class EntityObservableExtensions
{
	/// <summary>The change stream filtered to the moments the entity turns on. Composable.</summary>
	public static IObservable<StateChange> TurnsOn(this Entity entity) =>
		entity.StateChanges().Where(change => change.TurnedOn());

	/// <summary>The change stream filtered to the moments the entity turns off. Composable.</summary>
	public static IObservable<StateChange> TurnsOff(this Entity entity) =>
		entity.StateChanges().Where(change => change.TurnedOff());

	/// <summary>Runs <paramref name="handler"/> whenever the entity turns on. The terminal form for the common case.</summary>
	public static IDisposable WhenTurnsOn(this Entity entity, Action<StateChange> handler, ILogger logger) =>
		entity.TurnsOn().SubscribeSafe(handler, logger);

	/// <summary>Runs <paramref name="handler"/> whenever the entity turns off.</summary>
	public static IDisposable WhenTurnsOff(this Entity entity, Action<StateChange> handler, ILogger logger) =>
		entity.TurnsOff().SubscribeSafe(handler, logger);

	/// <summary>Runs <paramref name="handler"/> whenever the entity's state becomes <paramref name="state"/> (ordinal-ignore-case).</summary>
	public static IDisposable WhenStateBecomes(this Entity entity, string state, Action<StateChange> handler, ILogger logger) =>
		entity.StateChanges().Where(change => change.StateBecame(state)).SubscribeSafe(handler, logger);

	/// <summary>The typed change stream filtered to the moments the entity turns on. For generated-entity app code.</summary>
	public static IObservable<StateChange<TEntity, EntityState<TAttributes>>> TurnsOn<TEntity, TAttributes>(
		this Entity<TEntity, EntityState<TAttributes>, TAttributes> entity)
		where TEntity : Entity<TEntity, EntityState<TAttributes>, TAttributes>
		where TAttributes : class =>
		entity.StateChanges().Where(change => change.New?.IsOn() ?? false);

	/// <summary>The typed change stream filtered to the moments the entity turns off.</summary>
	public static IObservable<StateChange<TEntity, EntityState<TAttributes>>> TurnsOff<TEntity, TAttributes>(
		this Entity<TEntity, EntityState<TAttributes>, TAttributes> entity)
		where TEntity : Entity<TEntity, EntityState<TAttributes>, TAttributes>
		where TAttributes : class =>
		entity.StateChanges().Where(change => change.New?.IsOff() ?? false);

	/// <summary>Runs <paramref name="handler"/> whenever the typed entity turns on.</summary>
	public static IDisposable WhenTurnsOn<TEntity, TAttributes>(
		this Entity<TEntity, EntityState<TAttributes>, TAttributes> entity,
		Action<StateChange<TEntity, EntityState<TAttributes>>> handler,
		ILogger logger)
		where TEntity : Entity<TEntity, EntityState<TAttributes>, TAttributes>
		where TAttributes : class =>
		entity.TurnsOn().SubscribeSafe(handler, logger);

	/// <summary>Runs <paramref name="handler"/> whenever the typed entity turns off.</summary>
	public static IDisposable WhenTurnsOff<TEntity, TAttributes>(
		this Entity<TEntity, EntityState<TAttributes>, TAttributes> entity,
		Action<StateChange<TEntity, EntityState<TAttributes>>> handler,
		ILogger logger)
		where TEntity : Entity<TEntity, EntityState<TAttributes>, TAttributes>
		where TAttributes : class =>
		entity.TurnsOff().SubscribeSafe(handler, logger);
}
