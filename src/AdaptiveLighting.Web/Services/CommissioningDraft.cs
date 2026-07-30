using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     Every answer the commissioning board has taken, held in the browser circuit's memory until the one commit.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is not a second place configuration lives.</b> It is a set of pending corrections, and the
///         document on disk is untouched from the moment discovery wrote it until <see cref="Apply"/> runs inside
///         the commit button's single <c>LightingEngineHost.Save</c>. There is no autosave, no per-sheet write and
///         no copy in the browser's storage: a closed tab loses the answers, which costs two minutes of
///         re-answering and buys the property that nothing can ever disagree with the file
///         (<c>docs/design/first-run-wizard.md</c> §4).
///     </para>
///     <para>
///         <b>Every field is nullable-by-absence on purpose.</b> A sheet nobody opened must leave the document
///         exactly as it was, so "not answered" and "answered with the value that happens to be the default" are
///         different states here — the difference between leaving <see cref="GlobalConfig.Persons"/> empty (which
///         means "watch every person Home Assistant knows") and writing out a list that happens to contain the
///         same names today and will not tomorrow.
///     </para>
///     <para>
///         Mutable, and deliberately so: it is per-circuit scratch, edited by five sheets and read once. What is
///         pure — and what the tests pin — is <see cref="Apply"/>, which is where every answer turns into a change
///         to a document.
///     </para>
/// </remarks>
public sealed class CommissioningDraft
{
	private readonly HashSet<string> _picked = new(StringComparer.Ordinal);
	private readonly HashSet<string> _dropped = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The house name as typed, or <c>null</c> while the sheet has not been answered.</summary>
	/// <remarks>An answered-and-cleared name is <see cref="string.Empty"/>, not <c>null</c>: see <see cref="Apply"/>.</remarks>
	public string? HouseName { get; private set; }

	/// <summary>The empty-house delay in minutes, or <c>null</c> while the token has not been picked.</summary>
	public int? AwayDebounceMinutes { get; private set; }

	/// <summary>
	///     Whether the adopted house-mode select is kept, or <c>null</c> while the sheet has not been answered.
	/// </summary>
	/// <remarks>
	///     <c>true</c> is not the same as <c>null</c> even though both leave the document alone: the checklist reads
	///     this to say <i>kept</i> rather than <i>not looked at</i>, and a house with no select at all never sets it.
	/// </remarks>
	public bool? KeepHouseMode { get; private set; }

	/// <summary>The rooms switched on, by the key <see cref="RoomKey"/> builds.</summary>
	public IReadOnlyCollection<string> PickedRooms => _picked;

	/// <summary>The people toggled out of the house, by entity id.</summary>
	public IReadOnlyCollection<string> DroppedPersons => _dropped;

	/// <summary>How many rooms are switched on — the number the commit button counts.</summary>
	public int PickedCount => _picked.Count;

	/// <summary>Whether anything at all has been answered, which is what a "save without switching anything on" offer needs.</summary>
	public bool HasAnswers =>
		HouseName is not null || AwayDebounceMinutes is not null || KeepHouseMode is not null || _dropped.Count > 0;

	/// <summary>
	///     How a room is identified across the board, the draft and the document.
	/// </summary>
	/// <remarks>
	///     The area id when there is one, the display name otherwise — the same fallback
	///     <c>AreaView.VisibleCards</c> and the snapshot cache use, so a room hand-configured with explicit
	///     entities and no area id is still pickable rather than silently unswitchable.
	/// </remarks>
	/// <param name="area">The room, as the document holds it.</param>
	/// <exception cref="ArgumentNullException"><paramref name="area"/> is <c>null</c>.</exception>
	public static string RoomKey(AreaConfig area)
	{
		ArgumentNullException.ThrowIfNull(area);

		return area.AreaId is { Length: > 0 } areaId ? areaId : area.DisplayName;
	}

	/// <summary>Stages the house name. An empty or blank box is an answer — "no name" — not an unanswered sheet.</summary>
	/// <param name="typed">Whatever is in the box.</param>
	public void SetHouseName(string? typed) => HouseName = typed ?? string.Empty;

	/// <summary>Stages the empty-house delay, refusing a negative span rather than writing one.</summary>
	/// <param name="minutes">The picked value.</param>
	public void SetAwayDebounce(int minutes) => AwayDebounceMinutes = Math.Max(0, minutes);

	/// <summary>Stages the mode sheet's answer: keep the adopted select, or detach from it.</summary>
	/// <param name="keep">Whether the house keeps the select the helper adopted.</param>
	public void SetHouseMode(bool keep) => KeepHouseMode = keep;

	/// <summary>Whether a person is still counted for Home and Away.</summary>
	/// <param name="entityId">The person or device-tracker entity.</param>
	public bool Counts(string entityId) => !_dropped.Contains(entityId);

	/// <summary>Toggles a person in or out of the house.</summary>
	/// <param name="entityId">The person or device-tracker entity.</param>
	/// <exception cref="ArgumentNullException"><paramref name="entityId"/> is <c>null</c>.</exception>
	public void TogglePerson(string entityId)
	{
		ArgumentNullException.ThrowIfNull(entityId);

		if (!_dropped.Remove(entityId))
			_dropped.Add(entityId);
	}

	/// <summary>Whether a room is switched on in the draft.</summary>
	/// <param name="key">The room's <see cref="RoomKey"/>.</param>
	public bool IsPicked(string key) => _picked.Contains(key);

	/// <summary>Switches a room on or off.</summary>
	/// <param name="key">The room's <see cref="RoomKey"/>.</param>
	/// <exception cref="ArgumentNullException"><paramref name="key"/> is <c>null</c>.</exception>
	public void ToggleRoom(string key)
	{
		ArgumentNullException.ThrowIfNull(key);

		if (!_picked.Remove(key))
			_picked.Add(key);
	}

	/// <summary>Switches a whole floor on, which is the floor header's one bulk action.</summary>
	/// <param name="keys">The rooms on the floor.</param>
	/// <exception cref="ArgumentNullException"><paramref name="keys"/> is <c>null</c>.</exception>
	public void PickAll(IEnumerable<string> keys)
	{
		ArgumentNullException.ThrowIfNull(keys);

		foreach (string key in keys)
			_picked.Add(key);
	}

	/// <summary>Switches a whole floor off again.</summary>
	/// <param name="keys">The rooms on the floor.</param>
	/// <exception cref="ArgumentNullException"><paramref name="keys"/> is <c>null</c>.</exception>
	public void DropAll(IEnumerable<string> keys)
	{
		ArgumentNullException.ThrowIfNull(keys);

		foreach (string key in keys)
			_picked.Remove(key);
	}

	/// <summary>
	///     Writes every answer onto a working copy of the document, which the caller then puts through the one
	///     save path.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>Absence means "leave it alone", everywhere.</b> An unanswered sheet must not restate the value it
	///         found, because restating is not free: writing today's <see cref="GlobalConfig.Persons"/> back into a
	///         house that had none pins the list, and the next person added in Home Assistant then silently stops
	///         counting for Home and Away.
	///     </para>
	///     <para>
	///         <b>Rooms are the exception, and only in one direction.</b> Every picked room is written
	///         <c>Enabled = true</c> explicitly rather than left to inherit — a decision somebody made with a switch
	///         is a decision the file should state, which is <c>AreaView.SwitchAll</c>'s rule and the reason this
	///         does not simply clear the property. Rooms nobody picked are not touched at all: they were written
	///         switched off by discovery and stay exactly as they are, so abandoning the board halfway leaves the
	///         document as discovery left it.
	///     </para>
	///     <para>
	///         Deliberately not here yet: per-room <c>ExcludeEntities</c> from the impostor sheet, per-room
	///         <c>LuxSensor</c> pins and <c>Defaults.LuxThreshold</c> from the sensor sheet. Those sheets are not
	///         built; adding their staging with no surface to set it would be dead code that reads as a promise.
	///     </para>
	/// </remarks>
	/// <param name="config">The working copy, freshly read from disk. Mutated in place.</param>
	/// <param name="watchedPersons">
	///     The people the chips were drawn from, in order — <c>ModeService.GetPeople</c>'s list, which mirrors
	///     <c>PresenceMonitor</c>'s resolution exactly. Needed rather than derived from the document because the
	///     two disagree in the one case that matters: an empty <see cref="GlobalConfig.Persons"/> means "watch
	///     every person Home Assistant knows", so evicting the car has to pin the rest, and filtering the
	///     document's own empty list would silently do nothing.
	/// </param>
	/// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
	public void Apply(AdaptiveLightingConfig config, IReadOnlyList<string> watchedPersons)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(watchedPersons);

		if (HouseName is { } typed)
			config.ConfigName = IdentitySentence.Normalize(typed);

		if (AwayDebounceMinutes is { } minutes)
			config.Global.AwayDebounceMinutes = minutes;

		// Written only when somebody was actually evicted. An empty Persons list means "watch every person Home
		// Assistant knows" (PresenceMonitor's own rule), and turning that into an explicit list because the sheet
		// was merely looked at would quietly stop counting the next person the household adds.
		if (_dropped.Count > 0)
			config.Global.Persons = [.. watchedPersons.Where(person => !_dropped.Contains(person))];

		// Detach only. Keeping is the do-nothing answer, because the select is already in the document — writing
		// it back would be a save that changes nothing and a chance to change something by accident.
		if (KeepHouseMode is false)
			config.Global.HouseMode = null;

		foreach (AreaConfig area in config.Areas)
			if (_picked.Contains(RoomKey(area)))
				area.Enabled = true;
	}
}
