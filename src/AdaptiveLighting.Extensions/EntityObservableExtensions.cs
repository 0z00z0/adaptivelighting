using NetDaemon.HassModel.Entities;

namespace AdaptiveLighting.Extensions;

/// <summary>
///     Edge-triggered verbs on <see cref="Entity"/>: the on/off subscriptions the archived helpers established,
///     rebuilt on NetDaemon's <c>SubscribeSafe</c> so a thrown handler is logged rather than silently killing the
///     subscription.
/// </summary>
/// <remarks>
///     <para>
///         <c>StateChanges()</c> only emits when the state <i>value</i> changes, so a new value of <c>on</c>
///         already implies the old value was not <c>on</c>. <see cref="TurnsOn(Entity)"/> therefore filters only
///         on the new state — which means it fires on <c>unavailable → on</c> as well as <c>off → on</c>, matching
///         the engine's own inline filters. The archived versions additionally required the old state to be off,
///         which silently dropped a sensor coming back online <i>with</i> motion; that was a bug, not a feature.
///     </para>
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
