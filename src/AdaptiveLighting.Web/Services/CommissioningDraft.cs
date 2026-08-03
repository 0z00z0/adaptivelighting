using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Web.Services;

/// <summary>
///     Every answer the commissioning board has taken, held in the browser circuit's memory until the one commit.
/// </summary>
/// <remarks>
///     Pending corrections, not a second place configuration lives. The document is untouched from the moment
///     discovery wrote it until <see cref="Apply"/> runs inside the commit button's single
///     <c>LightingEngineHost.Save</c>. Every field distinguishes "not answered" from "answered with the default",
///     because an unanswered sheet has to leave the document as it was.
/// </remarks>
public sealed class CommissioningDraft
{
	private readonly HashSet<string> _picked = new(StringComparer.Ordinal);
	private readonly HashSet<string> _dropped = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>The house name as typed, or <c>null</c> while the sheet has not been answered.</summary>
	/// <remarks>An answered-and-cleared name is <see cref="string.Empty"/>, not <c>null</c>.</remarks>
	public string? HouseName { get; private set; }

	/// <summary>The empty-house delay in minutes, or <c>null</c> while the token has not been picked.</summary>
	public int? AwayDebounceMinutes { get; private set; }

	/// <summary>Whether the adopted house-mode select is kept, or <c>null</c> while the sheet is unanswered.</summary>
	/// <remarks><c>true</c> and <c>null</c> both leave the document alone, but the checklist tells them apart.</remarks>
	public bool? KeepHouseMode { get; private set; }

	public int PickedCount => _picked.Count;

	public bool HasAnswers =>
		HouseName is not null || AwayDebounceMinutes is not null || KeepHouseMode is not null || _dropped.Count > 0;

	/// <summary>How a room is identified across the board, the draft and the document.</summary>
	/// <remarks>
	///     Area id when there is one, display name otherwise: the same fallback <c>AreaView.VisibleCards</c> and the
	///     snapshot cache use, so a room configured with explicit entities and no area id is still pickable.
	/// </remarks>
	public static string RoomKey(AreaConfig area)
	{
		ArgumentNullException.ThrowIfNull(area);

		return area.AreaId is { Length: > 0 } areaId ? areaId : area.DisplayName;
	}

	/// <summary>Stages the house name. An empty or blank box is an answer, "no name", not an unanswered sheet.</summary>
	public void SetHouseName(string? typed) => HouseName = typed ?? string.Empty;

	/// <summary>Stages the empty-house delay, clamping a negative span to zero.</summary>
	public void SetAwayDebounce(int minutes) => AwayDebounceMinutes = Math.Max(0, minutes);

	/// <summary>Stages the mode sheet's answer: keep the adopted select, or detach from it.</summary>
	public void SetHouseMode(bool keep) => KeepHouseMode = keep;

	/// <summary>Whether a person is still counted for Home and Away.</summary>
	public bool Counts(string entityId) => !_dropped.Contains(entityId);

	public void TogglePerson(string entityId)
	{
		ArgumentNullException.ThrowIfNull(entityId);

		if (!_dropped.Remove(entityId))
			_dropped.Add(entityId);
	}

	/// <param name="key">The room's <see cref="RoomKey"/>.</param>
	public bool IsPicked(string key) => _picked.Contains(key);

	/// <param name="key">The room's <see cref="RoomKey"/>.</param>
	public void ToggleRoom(string key)
	{
		ArgumentNullException.ThrowIfNull(key);

		if (!_picked.Remove(key))
			_picked.Add(key);
	}

	/// <summary>Switches a whole floor on, the floor header's one bulk action.</summary>
	public void PickAll(IEnumerable<string> keys)
	{
		ArgumentNullException.ThrowIfNull(keys);

		foreach (string key in keys)
			_picked.Add(key);
	}

	public void DropAll(IEnumerable<string> keys)
	{
		ArgumentNullException.ThrowIfNull(keys);

		foreach (string key in keys)
			_picked.Remove(key);
	}

	/// <summary>
	///     Writes every answer onto a working copy of the document, which the caller then puts through the one save
	///     path. An unanswered sheet writes nothing, since restating a value can pin it.
	/// </summary>
	/// <param name="config">The working copy, freshly read from disk. Mutated in place.</param>
	/// <param name="watchedPersons">
	///     The people the chips were drawn from, in order: <c>ModeService.GetPeople</c>'s list, which mirrors
	///     <c>PresenceMonitor</c>'s resolution. Passed in, not derived from the document, because an empty
	///     <see cref="GlobalConfig.Persons"/> means "watch everyone" and filtering it would do nothing.
	/// </param>
	public void Apply(AdaptiveLightingConfig config, IReadOnlyList<string> watchedPersons)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(watchedPersons);

		if (HouseName is { } typed)
			config.ConfigName = IdentitySentence.Normalize(typed);

		if (AwayDebounceMinutes is { } minutes)
			config.Global.AwayDebounceMinutes = minutes;

		// Both conditions matter. Writing Persons when nobody was evicted turns "watch everyone" into a pinned list.
		// Writing it when watchedPersons is empty is worse: every Home Assistant reader here returns an empty list
		// when it cannot answer, so a hiccup would write Persons = [], which means watch everyone again.
		if (_dropped.Count > 0 && watchedPersons.Count > 0)
			config.Global.Persons = [.. watchedPersons.Where(person => !_dropped.Contains(person))];

		// Detach only. Keeping is the do-nothing answer; the select is already in the document.
		if (KeepHouseMode is false)
			config.Global.HouseMode = null;

		// Picked rooms are written explicitly, matching AreaView.SwitchAll. Unpicked rooms are not touched, so
		// abandoning the board halfway leaves the document as discovery left it.
		foreach (AreaConfig area in config.Areas)
			if (_picked.Contains(RoomKey(area)))
				area.Enabled = true;
	}
}
