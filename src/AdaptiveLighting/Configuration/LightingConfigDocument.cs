using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>
///     What reading a document produced: the bound configuration, and whether the file had to be translated
///     out of the pre-2.0 schema to produce it.
/// </summary>
/// <remarks>
///     An explicit return rather than an out-parameter because the flag is not a diagnostic — it is the trigger
///     for the migrating write in <see cref="Hosting.LightingEngineHost.Reload"/>, and a caller that never sees
///     it leaves a house permanently dependent on the translation table.
/// </remarks>
/// <param name="Config">The bound document. Never <c>null</c>.</param>
/// <param name="UsedLegacyKeys">Whether <see cref="LightingConfigDocument.Deserialize"/> renamed any pre-2.0 key.</param>
public sealed record DocumentReadResult(AdaptiveLightingConfig Config, bool UsedLegacyKeys);

/// <summary>
///     Raised when an <c>AdaptiveLighting.yaml</c> document cannot be read or written. The message is written
///     for whoever is looking at the web UI, not for a stack trace.
/// </summary>
public sealed class LightingConfigException : Exception
{
	/// <summary>Creates the exception.</summary>
	/// <param name="message">What went wrong, phrased for a human.</param>
	public LightingConfigException(string message) : base(message)
	{
	}

	/// <summary>Creates the exception.</summary>
	/// <param name="message">What went wrong, phrased for a human.</param>
	/// <param name="innerException">The underlying parser or IO failure.</param>
	public LightingConfigException(string message, Exception innerException) : base(message, innerException)
	{
	}

	/// <summary>Creates the exception with no message. Present to satisfy the framework's exception pattern.</summary>
	public LightingConfigException()
	{
	}
}

/// <summary>
///     Turns an <see cref="AdaptiveLightingConfig"/> into the text of an <c>AdaptiveLighting.yaml</c> document
///     and back. Pure text in, pure text out: no file system, so the whole round trip is unit-testable.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a second parser exists at all.</b> The app model binds this document with the .NET
///         configuration binder, which reads and never writes. Once the UI can save, something has to
///         serialise — and having the engine load through the binder while the UI loads through YamlDotNet
///         would be two parsers disagreeing about one file, which is the bug you find at 03:00. So this is the
///         only loader: <see cref="Hosting.LightingEngineHost"/> reads through here too, and
///         <c>IAppConfig&lt;AdaptiveLightingConfig&gt;</c> is no longer in the engine's path.
///     </para>
///     <para>
///         <b>Comments do not survive.</b> YamlDotNet emits a fresh document; every hand-written comment in the
///         file is lost the first time the UI saves. That is accepted, and the worked examples that used to live
///         in those comments are preserved in <c>docs/adaptive-lighting/example-config.md</c>.
///         <see cref="Header"/> is re-emitted on every write so the file itself says where they went.
///     </para>
/// </remarks>
public static class LightingConfigDocument
{
	/// <summary>
	///     The document's single top-level key: the fully qualified name of <see cref="AdaptiveLightingConfig"/>.
	/// </summary>
	/// <remarks>
	///     This is how <c>IAppConfig&lt;T&gt;</c> binds, and it stays the key even though the engine no longer
	///     loads through the app model — the file must keep working if the engine is ever pointed back at it.
	/// </remarks>
	public const string RootKey = "AdaptiveLighting.Configuration.AdaptiveLightingConfig";

	/// <summary>
	///     The pre-2.0 key names, mapped to what the schema calls them now. The only place in the codebase where
	///     the word "Zone" still exists.
	/// </summary>
	/// <remarks>
	///     <b>Deleting this is silent data loss, not a cleanup.</b> <see cref="Deserialize"/> binds with
	///     <c>IgnoreUnmatchedProperties</c> — deliberately, so a stale key cannot brick the UI that exists to
	///     remove it — which means a document still saying <c>Zones:</c> would bind against a model that has only
	///     <c>Areas</c> and load as <i>zero areas</i>: no parse error, no warning, no lights, nothing in the log
	///     to look at. Every document written before 2.0 says <c>Zones:</c>, and there is no way to prove nobody
	///     is still running one, so there is no version at which this becomes safe to remove.
	/// </remarks>
	private static readonly Dictionary<string, string> LegacyKeys = new(StringComparer.OrdinalIgnoreCase)
	{
		["Zones"] = nameof(AdaptiveLightingConfig.Areas),
		["ZonesAutoDiscovered"] = nameof(GlobalConfig.AreasAutoDiscovered)
	};

	private const string Header =
		"""
		# ============================================================================
		#  Adaptive lighting — managed by the lighting web UI.
		# ============================================================================
		#
		#  This file is written by the Configuration page of the lighting web UI. It
		#  is still a perfectly ordinary YAML file and hand-editing it works — but the
		#  next save from the browser rewrites it from scratch, and any comments you
		#  add here are lost at that moment. YamlDotNet cannot round-trip comments.
		#
		#  The annotated worked examples that used to live in this file — what every
		#  knob means, and why — are preserved verbatim in:
		#
		#      docs/adaptive-lighting/example-config.md
		#
		#  The schema reference is docs/adaptive-lighting/03-configuration.md.
		#
		#  The top-level key MUST stay the fully qualified config class name.
		# ============================================================================

		""";

	/// <summary>
	///     Serialises <paramref name="config"/> to the full text of a configuration document, header comment
	///     included.
	/// </summary>
	/// <param name="config">The document to write.</param>
	/// <returns>YAML text, ready to be written to disk.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="config"/> is <c>null</c>.</exception>
	public static string Serialize(AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		// OmitNull, not OmitDefaults: on an AreaConfig every settings property is a nullable twin where null
		// means "inherit Defaults", so a null must not be written. But an area that deliberately sets
		// Enabled: false or LuxThreshold: 0 has said something, and OmitDefaults would delete it.
		ISerializer serializer = new SerializerBuilder()
			.ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
			.Build();

		Dictionary<string, AdaptiveLightingConfig> document = new(StringComparer.Ordinal) { [RootKey] = config };

		return Header + serializer.Serialize(document);
	}

	/// <summary>
	///     Parses the text of a configuration document, translating any pre-2.0 key names on the way in.
	/// </summary>
	/// <param name="yaml">The file's contents.</param>
	/// <param name="logger">
	///     Where the both-keys warning goes, or <c>null</c> when nobody is listening. Optional because the
	///     translation must work identically whether or not a caller happens to have a logger.
	/// </param>
	/// <returns>The bound document, and whether it had to be translated to bind at all.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="yaml"/> is <c>null</c>.</exception>
	/// <exception cref="LightingConfigException">
	///     The text is not YAML, or carries no <see cref="RootKey"/> section.
	/// </exception>
	public static DocumentReadResult Deserialize(string yaml, ILogger? logger = null)
	{
		ArgumentNullException.ThrowIfNull(yaml);

		(string current, bool usedLegacyKeys) = TranslateLegacyKeys(yaml, logger);

		// IgnoreUnmatchedProperties mirrors the .NET configuration binder, which also ignores keys it does not
		// know. Being stricter here would mean a stale key left in a hand-edited file bricks the UI that exists
		// to fix exactly that kind of thing. It is also why TranslateLegacyKeys has to run first: an unmatched
		// Zones: would be ignored just as quietly as a stale key, and the house would load with no rooms.
		IDeserializer deserializer = new DeserializerBuilder()
			.IgnoreUnmatchedProperties()
			.Build();

		Dictionary<string, AdaptiveLightingConfig?>? document;

		try
		{
			document = deserializer.Deserialize<Dictionary<string, AdaptiveLightingConfig?>>(current);
		}
		catch (YamlException exception)
		{
			throw new LightingConfigException(
				$"The configuration file is not valid YAML: {exception.Message}", exception);
		}

		if (document is null || document.Count == 0)
			throw new LightingConfigException("The configuration file is empty.");

		// Looked up case-insensitively even though the key is a .NET type name: the binder that used to own this
		// file is case-insensitive, so a file that worked before must keep working now.
		//
		// The fallback matches ANY key naming AdaptiveLightingConfig, whatever namespace produced it. The key is a
		// fully qualified type name, so renaming the namespace would otherwise orphan every file already on disk —
		// which is exactly what happened when this library was extracted for distribution. Reading is forgiving,
		// writing is not: Serialize always emits RootKey, so a file self-migrates the first time it is saved.
		string? match =
			document.Keys.FirstOrDefault(key => string.Equals(key, RootKey, StringComparison.OrdinalIgnoreCase))
			?? document.Keys.FirstOrDefault(key => key.EndsWith("." + nameof(AdaptiveLightingConfig), StringComparison.OrdinalIgnoreCase));

		if (match is null)
			throw new LightingConfigException(
				$"The configuration file has no '{RootKey}' section. It has: {string.Join(", ", document.Keys)}.");

		// A present-but-empty section parses to null. That is a legitimate starting point — an operator who
		// deleted everything below the key — and it means "all defaults, no areas", which the validator will
		// then reject with a message that says so.
		return new DocumentReadResult(document[match] ?? new AdaptiveLightingConfig(), usedLegacyKeys);
	}

	/// <summary>
	///     Rewrites every <see cref="LegacyKeys"/> name in <paramref name="yaml"/> to its current name, and
	///     returns the text the binder should see.
	/// </summary>
	/// <remarks>
	///     Works on the generic node tree rather than on the text: a regex over the file would rename the word
	///     inside a room's name or an entity id just as happily as it renamed a key. The tree is walked to any
	///     depth because the two legacy keys sit at two different levels (<c>Zones</c> under the root section,
	///     <c>ZonesAutoDiscovered</c> under <c>Global</c>), and a rule that knows where to look is a rule that
	///     breaks the day the schema moves something.
	/// </remarks>
	/// <returns>The translated text — the original instance when nothing matched — and whether anything did.</returns>
	private static (string Yaml, bool UsedLegacyKeys) TranslateLegacyKeys(string yaml, ILogger? logger)
	{
		YamlStream stream = new();

		try
		{
			stream.Load(new StringReader(yaml));
		}
		catch (YamlException exception)
		{
			throw new LightingConfigException(
				$"The configuration file is not valid YAML: {exception.Message}", exception);
		}

		bool used = false;

		foreach (YamlDocument document in stream.Documents)
			used |= Translate(document.RootNode, logger);

		if (!used)
			return (yaml, false);

		StringWriter writer = new();
		stream.Save(writer, assignAnchors: false);

		return (writer.ToString(), true);
	}

	/// <summary>Renames the legacy keys of one node and everything under it, in place.</summary>
	/// <returns><c>true</c> when anything was renamed or dropped.</returns>
	private static bool Translate(YamlNode node, ILogger? logger)
	{
		bool used = false;

		switch (node)
		{
			case YamlMappingNode mapping:
				// Materialised first: the renames below add to and remove from the very collection being walked.
				foreach (KeyValuePair<YamlNode, YamlNode> child in mapping.Children.ToList())
				{
					used |= Translate(child.Value, logger);

					if (child.Key is not YamlScalarNode { Value: { Length: > 0 } name }
						|| !LegacyKeys.TryGetValue(name, out string? currentName))
						continue;

					mapping.Children.Remove(child.Key);
					used = true;

					// Both names present: the file said two things and the reader has to pick one. The current
					// schema's name is the one a current editor wrote, so it wins and the legacy key is dropped.
					if (HasKey(mapping, currentName))
					{
						logger?.LogWarning(
							"The configuration document carries both the legacy key '{LegacyKey}' and '{CurrentKey}'. "
							+ "The current key is used, the legacy one is dropped, and the next save writes only the current one.",
							name, currentName);

						continue;
					}

					mapping.Children.Add(new YamlScalarNode(currentName), child.Value);
				}

				break;

			case YamlSequenceNode sequence:
				foreach (YamlNode item in sequence.Children)
					used |= Translate(item, logger);

				break;
		}

		return used;
	}

	/// <summary>Whether <paramref name="mapping"/> already carries <paramref name="key"/>, matched as the binder matches.</summary>
	private static bool HasKey(YamlMappingNode mapping, string key) =>
		mapping.Children.Keys.OfType<YamlScalarNode>()
			.Any(scalar => string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase));
}
