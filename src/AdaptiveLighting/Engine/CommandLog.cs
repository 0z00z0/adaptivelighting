using System.Globalization;
using System.Text;

using AdaptiveLighting.Abstractions;
using AdaptiveLighting.Configuration;

namespace AdaptiveLighting.Engine;

/// <summary>Writes every light command the engine puts on a light: a folder per area, a file per light, a line per command.</summary>
/// <remarks>One per engine build, created only while the document's <see cref="GlobalConfig.CommandLog"/> is on.</remarks>
public sealed class CommandLog
{
	/// <summary>The folder beside the document that holds the durable log and this one.</summary>
	public const string LogFolderName = "log";

	public const string FolderName = "commands";

	/// <summary>Where a house-mode scene is written, since it belongs to no one room.</summary>
	public const string HouseFolder = "_house";

	internal const string SceneFile = "_scene";

	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	private readonly string _directory;
	private readonly ILogger _logger;
	private readonly Lock _gate = new();
	private bool _failureReported;

	public CommandLog(string directory, ILogger logger)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);

		_directory = directory;
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>The log's directory for the document at <paramref name="documentPath"/>: <c>log/commands</c> beside it.</summary>
	public static string DirectoryBeside(string documentPath) =>
		Path.Combine(Path.GetDirectoryName(Path.GetFullPath(documentPath)) ?? ".", LogFolderName, FolderName);

	/// <summary>An area's folder: its Home Assistant area id, or its name where it has none.</summary>
	public static string FolderFor(string? areaId, string areaName) =>
		SafeName(areaId is { Length: > 0 } ? areaId : areaName);

	/// <summary>Lower case, with everything outside <c>a-z 0-9 . _ -</c> as <c>_</c>.</summary>
	public static string SafeName(string name)
	{
		StringBuilder safe = new(name.Length);

		foreach (char c in name.ToLowerInvariant())
			safe.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' ? c : '_');

		// A name of dots alone would climb out of the folder.
		return safe.Length == 0 || safe.ToString().Trim('.').Length == 0 ? "_" : safe.ToString();
	}

	/// <summary>Wraps an actuator so every command it carries is written under <paramref name="folder"/>.</summary>
	// context is read at the moment of sending, inside the caller's lock.
	internal ILightActuator Wrap(
		ILightActuator inner,
		string folder,
		string displayName,
		Func<DateTimeOffset> now,
		Func<CommandContext> context) =>
		new CommandLogActuator(this, inner, folder, displayName, now, context);

	// Formatting runs inside the catch as well, so nothing about a line can reach the lighting path.
	internal void Write(string folder, string fileStem, string header, Func<string> line)
	{
		lock (_gate)
		{
			try
			{
				string directory = Path.Combine(_directory, folder);
				string path = Path.Combine(directory, fileStem + ".log");
				string text = line() + "\n";

				if (!File.Exists(path))
				{
					Directory.CreateDirectory(directory);
					text = header + "\n" + text;
				}

				File.AppendAllText(path, text, Utf8NoBom);
			}
			catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
			{
				if (_failureReported)
					return;

				_failureReported = true;
				_logger.LogWarning(exception,
					"Could not write the light command log under {Directory}. Lighting carries on; further failures are not reported until the next save.",
					_directory);
			}
		}
	}

	internal static string Line(DateTimeOffset at, CommandContext context, LightCommand command, ActuatorOutcome outcome)
	{
		StringBuilder line = Start(at, context);

		string action = outcome.Service ?? (command.On ? "turn_on" : "turn_off");
		line.Append(" action=").Append(action);

		if (outcome is { Sent: true, Data: { } data })
			AppendData(line, data);
		else
			AppendCommand(line, command);

		line.Append(" outcome=").Append(outcome.Sent ? "sent" : "not-sent-already-matches");
		return line.ToString();
	}

	internal static string SceneLine(DateTimeOffset at, CommandContext context, string sceneId) =>
		Start(at, context).Append(" action=scene.turn_on scene=").Append(sceneId).Append(" outcome=sent").ToString();

	private static StringBuilder Start(DateTimeOffset at, CommandContext context) =>
		new StringBuilder()
			.Append(at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture))
			.Append(" reason=").Append(context.Reason?.ToString() ?? "unknown")
			.Append(" state=").Append(context.State?.ToString() ?? "none");

	private static readonly string[] LeadingKeys = ["brightness_pct", "color_temp_kelvin", "transition"];

	private static void AppendData(StringBuilder line, IReadOnlyDictionary<string, object> data)
	{
		foreach (string key in LeadingKeys)
			if (data.TryGetValue(key, out object? value))
				AppendPair(line, key, value);

		foreach (string key in data.Keys.Where(key => !LeadingKeys.Contains(key)).Order(StringComparer.Ordinal))
			AppendPair(line, key, data[key]);
	}

	private static void AppendCommand(StringBuilder line, LightCommand command)
	{
		if (command.On && command.BrightnessPct is { } brightness)
			AppendPair(line, "brightness_pct", Math.Round(brightness, 1));

		if (command.On && command.ColorTempKelvin is { } kelvin)
			AppendPair(line, "color_temp_kelvin", kelvin);

		if (command.TransitionSeconds is { } transition)
			AppendPair(line, "transition", Math.Round(transition, 1));

		if (command.On && command.Channels is { Count: > 0 } channels)
			AppendPair(line, "channels", channels.ToArray());
		else if (command.On && command.EqualChannels)
			AppendPair(line, "channels", "equal");
	}

	private static void AppendPair(StringBuilder line, string key, object value) =>
		line.Append(' ').Append(key).Append('=').Append(value switch
		{
			int[] channels => string.Join(",", channels.Select(channel => channel.ToString(CultureInfo.InvariantCulture))),
			IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
			_ => value.ToString()
		});
}

/// <summary>Why a command is being sent and what state the area is in, as the area knows it at that moment.</summary>
internal readonly record struct CommandContext(TransitionReason? Reason, AreaState? State);

/// <summary>One area's actuator, writing each command it carries after the light has been asked.</summary>
internal sealed class CommandLogActuator : ILightActuator
{
	private readonly CommandLog _log;
	private readonly ILightActuator _inner;
	private readonly string _folder;
	private readonly string _header;
	private readonly Func<DateTimeOffset> _now;
	private readonly Func<CommandContext> _context;

	public CommandLogActuator(
		CommandLog log,
		ILightActuator inner,
		string folder,
		string displayName,
		Func<DateTimeOffset> now,
		Func<CommandContext> context)
	{
		_log = log;
		_inner = inner;
		_folder = folder;
		_header = "# " + displayName.Replace('\r', ' ').Replace('\n', ' ');
		_now = now;
		_context = context;
	}

	public ActuatorOutcome Apply(string entityId, LightCommand command)
	{
		ActuatorOutcome outcome = _inner.Apply(entityId, command);
		_log.Write(_folder, CommandLog.SafeName(entityId), _header, () => CommandLog.Line(_now(), _context(), command, outcome));
		return outcome;
	}

	public void ActivateScene(string sceneId)
	{
		_inner.ActivateScene(sceneId);
		_log.Write(_folder, CommandLog.SceneFile, _header, () => CommandLog.SceneLine(_now(), _context(), sceneId));
	}
}
