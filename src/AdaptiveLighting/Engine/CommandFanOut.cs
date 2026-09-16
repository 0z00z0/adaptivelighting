using AdaptiveLighting.Abstractions;

namespace AdaptiveLighting.Engine;

/// <summary>Puts one area's commands on its lights, declaring each one before it is applied.</summary>
// The rule this class exists to keep: a command reaching Home Assistant before the expectation that explains
// it defeats the detector's primary heuristic, and the area reads its own work as a hand at the switch. The
// entry and leaf maps are read off the resolved area, never Home Assistant, so they are settled before Start.
// Called only from inside AreaController's lock and takes none of its own.
internal sealed class CommandFanOut
{
	private readonly ResolvedArea _area;
	private readonly IHaContext _ha;
	private readonly OverrideDetector _detector;
	private readonly ILightActuator _actuator;

	// Every light beneath a group entry, and the entries it sits under.
	private readonly Dictionary<string, List<string>> _entriesOfMember = new(StringComparer.OrdinalIgnoreCase);

	private readonly HashSet<string> _leaves;

	public CommandFanOut(ResolvedArea area, IHaContext ha, OverrideDetector detector, ILightActuator actuator)
	{
		_area = area;
		_ha = ha;
		_detector = detector;
		_actuator = actuator;

		foreach (string entry in area.Lights)
			foreach (string leaf in LeavesOf(entry))
			{
				if (string.Equals(leaf, entry, StringComparison.OrdinalIgnoreCase))
					continue;

				if (!_entriesOfMember.TryGetValue(leaf, out List<string>? entries))
					_entriesOfMember[leaf] = entries = [];

				entries.Add(entry);
			}

		_leaves = AllLeaves();
	}

	/// <summary>Every light the room commands, groups followed to the bottom.</summary>
	public IReadOnlySet<string> Leaves => _leaves;

	/// <summary>The lights this room reaches only beneath a group entry.</summary>
	public IEnumerable<string> GroupMembers => _entriesOfMember.Keys;

	/// <summary>Declares a member's availability change on every entry above it.</summary>
	// Must run before the group's own change is classified; the caller hears the member first.
	public void ExpectMemberAvailability(string member)
	{
		if (!_entriesOfMember.TryGetValue(member, out List<string>? entries))
			return;

		foreach (string entry in entries)
			_detector.ExpectMemberAvailabilityChange(entry);
	}

	/// <summary>Puts <paramref name="command"/> on the area's lights, each light stating its own where it does.</summary>
	public void Send(LightCommand command, IReadOnlyDictionary<string, LightCommand>? lightCommands = null)
	{
		// Nothing in this room states a level of its own, so membership is not consulted and every entry gets the
		// room's command. This branch is the safety property.
		if (lightCommands is not { Count: > 0 } stated)
		{
			foreach (string light in _area.Lights)
			{
				_detector.ExpectCommand(light, command);
				_actuator.Apply(light, command);
			}

			return;
		}

		HashSet<string> sent = new(StringComparer.OrdinalIgnoreCase);

		foreach (string entry in _area.Lights)
		{
			IReadOnlySet<string> leaves = LeavesOf(entry);

			if (!leaves.Any(stated.ContainsKey))
			{
				// An explicit Lights list can hold a group and one of its own members, so a leaf already commanded
				// under another entry is not commanded again.
				if (leaves.All(sent.Contains))
					continue;

				_detector.ExpectCommand(entry, command);
				_actuator.Apply(entry, command);
				sent.UnionWith(leaves);
				continue;
			}

			// The entry itself is not commanded, and the expectation on it is still load-bearing: a group entity
			// re-publishes a member's change under the group's id, and an echo nothing explains classifies as a
			// hand at the switch. The detector matches polarity only, so the entry is expected on while anything
			// beneath it is being switched on, and off when every light under it is going out.
			bool anyLeafOn = leaves.Any(leaf => CommandForLeaf(leaf, stated, command).On);

			_detector.ExpectCommand(entry, anyLeafOn == command.On ? command : command with { On = anyLeafOn });

			foreach (string leaf in leaves.Order(StringComparer.Ordinal))
			{
				if (!sent.Add(leaf))
					continue;

				LightCommand own = CommandForLeaf(leaf, stated, command);
				_detector.ExpectCommand(leaf, own);
				_actuator.Apply(leaf, own);
			}
		}
	}

	/// <summary>Commands one light alone, declaring an expectation on it and on every entry that reaches it.</summary>
	// A group re-publishes a member's change under its own id and the room subscribes to the group, so without the
	// entry's expectation the echo reads as a hand at the switch. Polarity only: the group stays on while any light
	// under it is on.
	public void SendToLight(string light, LightCommand command)
	{
		foreach (string entry in _area.Lights)
		{
			if (string.Equals(entry, light, StringComparison.OrdinalIgnoreCase))
				continue;

			IReadOnlySet<string> leaves = LeavesOf(entry);

			if (!leaves.Contains(light))
				continue;

			bool groupOn = command.On
				|| leaves.Any(other => !string.Equals(other, light, StringComparison.OrdinalIgnoreCase) && _ha.IsOn(other));

			_detector.ExpectCommand(entry, groupOn == command.On ? command : command with { On = groupOn });
		}

		_detector.ExpectCommand(light, command);
		_actuator.Apply(light, command);
	}

	/// <summary>Commands one entity, with no expectation on any entry above it.</summary>
	// For a room-wide return, whose captured entities are the area's own entries.
	public void SendAlone(string entityId, LightCommand command)
	{
		_detector.ExpectCommand(entityId, command);
		_actuator.Apply(entityId, command);
	}

	/// <summary>Runs a scene on this area's lights.</summary>
	// Declared before the call, as a command would be: the scene's own light changes carry neither a user nor a
	// parent, which the detector reads as a hand at the switch.
	public void RunScene(string sceneId, double transitionSeconds)
	{
		ExpectSceneOnEveryLight(transitionSeconds);
		_actuator.ActivateScene(sceneId);
	}

	/// <summary>Declares a scene about to run, so its changes are not read as a person's.</summary>
	public void ExpectSceneOnEveryLight(double transitionSeconds)
	{
		foreach (string light in _area.Lights)
			_detector.ExpectScene(light, transitionSeconds);
	}

	/// <summary>What one light under an entry is told to be: its own command where it has one, the room's where not.</summary>
	private static LightCommand CommandForLeaf(
		string leaf,
		IReadOnlyDictionary<string, LightCommand> stated,
		LightCommand room) =>
		stated.TryGetValue(leaf, out LightCommand? own) ? own : room;

	/// <summary>The lights beneath one of the area's entries; the entry itself when it groups nothing.</summary>
	private IReadOnlySet<string> LeavesOf(string entry) =>
		_area.LeavesOfEntry.TryGetValue(entry, out IReadOnlySet<string>? leaves) && leaves.Count > 0
			? leaves
			: new HashSet<string>(StringComparer.Ordinal) { entry };

	private HashSet<string> AllLeaves()
	{
		HashSet<string> leaves = new(StringComparer.OrdinalIgnoreCase);

		foreach (string entry in _area.Lights)
			leaves.UnionWith(LeavesOf(entry));

		return leaves;
	}
}
