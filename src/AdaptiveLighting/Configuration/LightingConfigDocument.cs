using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

/// <summary>
///     What reading a document produced: the bound configuration, and whether the file had to be translated out of
///     the pre-2.0 schema to produce it.
/// </summary>
public sealed record DocumentReadResult(
	AdaptiveLightingConfig Config,
	bool UsedLegacyKeys,
	bool MintedStableKeys = false)
{
	/// <summary>Whether the file on disk is behind the schema, so <see cref="Hosting.LightingEngineHost.Reload"/> writes it back once.</summary>
	public bool NeedsMigratingWrite => UsedLegacyKeys || MintedStableKeys;
}

/// <summary>Raised when an <c>AdaptiveLighting.yaml</c> document cannot be read or written; the message is written for the web UI.</summary>
public sealed class LightingConfigException : Exception
{
	public LightingConfigException(string message) : base(message)
	{
	}

	public LightingConfigException(string message, Exception innerException) : base(message, innerException)
	{
	}

	public LightingConfigException()
	{
	}
}

/// <summary>
///     Turns an <see cref="AdaptiveLightingConfig"/> into the text of an <c>AdaptiveLighting.yaml</c> document and
///     back. Pure text in, pure text out, so the whole round trip is unit-testable.
/// </summary>
/// <remarks>
///     The only loader, so the engine and the UI cannot end up as two parsers disagreeing about one file. Comments in
///     the file do not survive a write: YamlDotNet emits a fresh document, and <see cref="Header"/> says so.
/// </remarks>
public static class LightingConfigDocument
{
	/// <summary>
	///     The document's single top-level key: the fully qualified name of <see cref="AdaptiveLightingConfig"/>, and
	///     also how the .NET configuration binder binds it.
	/// </summary>
	public const string RootKey = "AdaptiveLighting.Configuration.AdaptiveLightingConfig";

	/// <summary>The superseded key names, mapped to what the schema calls them now.</summary>
	/// <remarks>
	///     Deleting this is silent data loss: <see cref="Deserialize"/> binds with <c>IgnoreUnmatchedProperties</c>, so
	///     an unknown key is silence and not an error, and a document still saying <c>Zones:</c> would load as zero
	///     areas. The rename only moves the value across; <see cref="StableKeyMigration"/> turns a name into an id.
	/// </remarks>
	private static readonly Dictionary<string, string> LegacyKeys = new(StringComparer.OrdinalIgnoreCase)
	{
		["Zones"] = nameof(AdaptiveLightingConfig.Areas),
		["ZonesAutoDiscovered"] = nameof(GlobalConfig.AreasAutoDiscovered),
		["Period"] = nameof(RoomLevelOverride.PeriodId),
		["SetsMode"] = nameof(TimePeriodConfig.SetsModeId),
		["ClampPeriod"] = nameof(HouseModeOptionConfig.ClampPeriodId),
		["ResetOnPeriodStart"] = nameof(HouseModeOptionConfig.ResetOnPeriodStartId)
	};

	/// <summary>Values a retired setting used to take, and what each becomes.</summary>
	/// <remarks>
	///     An unknown key is silence, but an unknown enum value is a parse failure, so a retired value has to be
	///     translated, not ignored.
	/// </remarks>
	private static readonly Dictionary<string, Dictionary<string, string>> LegacyValues =
		new(StringComparer.Ordinal)
		{
			[nameof(AreaSettings.Darkness)] = new(StringComparer.OrdinalIgnoreCase)
			{
				["Either"] = nameof(DarknessSource.Lux)
			}
		};

	/// <summary>Keys a document may still carry that no longer do anything, and the sentence each earns.</summary>
	/// <remarks>
	///     Both binders ignore an unknown key, so a retired setting changes behaviour with nothing to point at it: a
	///     night period written <c>{ BrightnessPct: 15, MaxBrightnessPct: 30 }</c> now clamps to 15 % in sleep mode
	///     where it clamped to 30 %. The key is left in the file; only the next save drops it.
	/// </remarks>
	internal static readonly Dictionary<string, string> RetiredKeys = new(StringComparer.OrdinalIgnoreCase)
	{
		["MaxBrightnessPct"] =
			"per-period ceilings were removed, so this period no longer caps what it commands. Its BrightnessPct "
			+ "is now the level it holds in sleep mode, where the ceiling used to be.",
		["MinBrightnessPct"] =
			"per-period floors were removed, so the pre-off warning dim is no longer held up by this value.",
		["BrightnessTolerancePct"] =
			"the brightness tolerance is now fixed and is no longer configurable per house.",
		["ColorTempToleranceKelvin"] =
			"the colour-temperature tolerance is now fixed and is no longer configurable per house.",
		["ResetAtTime"] =
			"ending a house mode at a time of day was removed; the mode now ends only where its own option says it does.",
		["LuxBrightnessEnabled"] =
			"the daylight curve is chosen per period now, not per room. Set UseDaylightCurve on the periods that "
			+ "should follow the light outside; the other lux brightness settings still shape the curve."
	};

	// One wording for the log and the browser. Two copies would drift, and the browser's is the one nobody reads
	// twice to notice.
	private static string RetiredKeySentence(string key, string reason) =>
		$"'{key}' is still set in the configuration, but it no longer does anything: {reason} "
		+ "Remove it, or save once from the browser and it will be dropped.";

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
		#  What every setting means, and a worked example, are documented at:
		#
		#      https://adaptivelighting.netlify.app
		#
		#  Everything here is also editable in the browser — this file is written
		#  by the app, so a hand edit is overwritten on the next save.
		#
		#  The top-level key MUST stay the fully qualified config class name.
		# ============================================================================

		""";

	/// <summary>Serialises <paramref name="config"/> to the full text of a document, header comment included.</summary>
	public static string Serialize(AdaptiveLightingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		// OmitNull, not OmitDefaults. On an AreaConfig null means "inherit Defaults", but an area that sets
		// Enabled: false or LuxThreshold: 0 has said something and OmitDefaults would delete it.
		ISerializer serializer = new SerializerBuilder()
			.ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
			.Build();

		Dictionary<string, AdaptiveLightingConfig> document = new(StringComparer.Ordinal) { [RootKey] = config };

		return Header + serializer.Serialize(document);
	}

	/// <summary>Parses the text of a configuration document, translating any pre-2.0 key names on the way in.</summary>
	/// <exception cref="LightingConfigException">The text is not YAML, or carries no <see cref="RootKey"/> section.</exception>
	public static DocumentReadResult Deserialize(string yaml, ILogger? logger = null)
	{
		ArgumentNullException.ThrowIfNull(yaml);

		// Keyed, so a setting retired on four periods is one sentence and not four.
		Dictionary<string, string> retired = new(StringComparer.OrdinalIgnoreCase);

		(string current, bool usedLegacyKeys) = TranslateLegacyKeys(yaml, logger, retired);

		// IgnoreUnmatchedProperties mirrors the .NET configuration binder, so a stale key cannot brick the UI that
		// exists to remove it. It is also why TranslateLegacyKeys runs first: an unmatched Zones: would be passed
		// over just as quietly, and the house would load with no rooms.
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

		// Case-insensitive, because the binder that used to own this file is. The fallback matches any key naming
		// AdaptiveLightingConfig whatever namespace produced it, so renaming the namespace does not orphan every
		// file on disk. Reading is forgiving, writing is not: Serialize always emits RootKey.
		string? match =
			document.Keys.FirstOrDefault(key => string.Equals(key, RootKey, StringComparison.OrdinalIgnoreCase))
			?? document.Keys.FirstOrDefault(key => key.EndsWith("." + nameof(AdaptiveLightingConfig), StringComparison.OrdinalIgnoreCase));

		if (match is null)
			throw new LightingConfigException(
				$"The configuration file has no '{RootKey}' section. It has: {string.Join(", ", document.Keys)}.");

		// A present-but-empty section parses to null, meaning all defaults and no areas.
		AdaptiveLightingConfig config = document[match] ?? new AdaptiveLightingConfig();

		// The key is gone by the time the binder is done with it, so this is the only place it can be carried out of.
		config.RetiredKeysInDocument = [.. retired.Values];

		RepairStructuralNulls(config, logger);

		// After the repair, so the migration walks lists it can rely on. Without it every reference that used to resolve
		// by name resolves to nothing, silently.
		bool mintedStableKeys = StableKeyMigration.Apply(config);

		if (mintedStableKeys)
			logger?.LogInformation(
				"The configuration document referred to periods and house modes by name. Each has been given an id "
				+ "and every reference repointed at it, so renaming one no longer breaks what pointed at it.");

		return new DocumentReadResult(config, usedLegacyKeys, mintedStableKeys);
	}

	/// <summary>Puts back the collections and sub-objects the model says are never <c>null</c>.</summary>
	/// <remarks>
	///     A bare <c>Areas:</c> line is valid YAML for "this key is null", and YamlDotNet assigns that null over the
	///     property initialiser. Unrepaired it throws out of <see cref="Hosting.LightingEngineHost.Reload"/>, which is
	///     documented never to throw and whose caller dying takes the HA connection and the web UI with it. Blank list
	///     elements are dropped, so a stray <c>-</c> does not become a nameless room.
	/// </remarks>
	private static void RepairStructuralNulls(AdaptiveLightingConfig config, ILogger? logger)
	{
		bool repaired = config.Global is null || config.Defaults is null;

		config.Global ??= new GlobalConfig();
		config.Defaults ??= new AreaSettings();

		GlobalConfig global = config.Global;

		repaired |= NullSafeList(config.Periods, out List<TimePeriodConfig> periods);
		config.Periods = periods;

		repaired |= NullSafeList(config.Areas, out List<AreaConfig> areas);
		config.Areas = areas;

		repaired |= NullSafeList(global.Persons, out List<string> persons);
		global.Persons = persons;

		repaired |= NullSafeList(global.MotionDeviceClasses, out List<string> motionDeviceClasses);
		global.MotionDeviceClasses = motionDeviceClasses;

		if (global.HouseMode is { } houseMode)
		{
			repaired |= NullSafeList(houseMode.Options, out List<HouseModeOptionConfig> options);
			houseMode.Options = options;

			foreach (HouseModeOptionConfig option in options)
			{
				repaired |= NullSafeList(option.ActivateWhileOn, out List<string> activateWhileOn);
				option.ActivateWhileOn = activateWhileOn;

				repaired |= NullSafeList(option.ResetPresenceSensors, out List<string> resetPresenceSensors);
				option.ResetPresenceSensors = resetPresenceSensors;
			}
		}

		// The HouseMode.Options repair above, again for PeriodSelect.
		if (global.PeriodSelect is { } periodSelect)
		{
			repaired |= NullSafeList(periodSelect.Options, out List<PeriodSelectOptionConfig> periodOptions);
			periodSelect.Options = periodOptions;
		}

		// An area's entity lists are nullable by design (null means "let discovery find them"), so only their
		// contents are repaired, never their absence. Levels is never null in the model, so it gets both.
		foreach (AreaConfig area in areas)
		{
			repaired |= DropBlanks(area.Lights);
			repaired |= DropBlanks(area.MotionSensors);
			repaired |= DropBlanks(area.IgnoreWhenOn);
			repaired |= DropBlanks(area.KeepLitWhenOn);
			repaired |= DropBlanks(area.ExcludeEntities);

			repaired |= NullSafeList(area.Levels, out List<RoomLevelOverride> levels);
			area.Levels = levels;
		}

		// Never null in the model, and the normaliser clears it on every save, so a hand-emptied key would throw
		// there before the engine ever read it.
		foreach (TimePeriodConfig period in periods)
		{
			repaired |= NullSafeList(period.StartsOnMotionAreas, out List<string> startsOnMotionAreas);
			period.StartsOnMotionAreas = startsOnMotionAreas;
		}

		if (repaired)
			logger?.LogWarning(
				"The configuration document has empty sections or blank list entries — a key with nothing under it, "
				+ "or a bare '-'. They have been read as absent so the rest of the file still loads; the next save "
				+ "writes the document without them.");
	}

	/// <summary>Whether <paramref name="list"/> needed repair, and the list to use instead: never null, never holding a null.</summary>
	private static bool NullSafeList<T>(List<T>? list, out List<T> repaired) where T : class
	{
		if (list is null)
		{
			repaired = [];
			return true;
		}

		repaired = list;

		return DropBlanks(list);
	}

	/// <summary>Removes any null element from <paramref name="list"/>, in place. <c>true</c> when one was there.</summary>
	private static bool DropBlanks<T>(List<T>? list) where T : class =>
		list is not null && list.RemoveAll(item => item is null) > 0;

	/// <summary>
	///     Rewrites every <see cref="LegacyKeys"/> name in <paramref name="yaml"/> to its current name, returning the
	///     original instance when nothing matched.
	/// </summary>
	/// <remarks>
	///     Starts at this document's own section, never at the top of the file: another NetDaemon app beside this one
	///     may legitimately carry a <c>Zones</c> key. Works on the node tree, not the text, so the word is not renamed
	///     inside a room name or an entity id.
	/// </remarks>
	private static (string Yaml, bool UsedLegacyKeys) TranslateLegacyKeys(
		string yaml,
		ILogger? logger,
		Dictionary<string, string> retired)
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
			foreach (YamlNode section in SectionsOf(document.RootNode))
				used |= Translate(section, logger, retired);

		if (!used)
			return (yaml, false);

		StringWriter writer = new();
		stream.Save(writer, assignAnchors: false);

		return (writer.ToString(), true);
	}

	/// <summary>Renames the legacy keys of one node and everything under it, in place.</summary>
	/// <returns><c>true</c> when anything was renamed or dropped.</returns>
	private static bool Translate(YamlNode node, ILogger? logger, Dictionary<string, string> retired)
	{
		bool used = false;

		switch (node)
		{
			case YamlMappingNode mapping:
				// Materialised first: the renames below add to and remove from the very collection being walked.
				foreach (KeyValuePair<YamlNode, YamlNode> child in mapping.Children.ToList())
				{
					used |= Translate(child.Value, logger, retired);

					if (child.Key is not YamlScalarNode { Value: { Length: > 0 } name })
						continue;

					// `used` is not set here. The document is unchanged, and claiming a migration happened would send
					// Reload writing the file back.
					if (RetiredKeys.TryGetValue(name, out string? reason))
					{
						string sentence = RetiredKeySentence(name, reason);

						logger?.LogWarning("{RetiredSetting}", sentence);
						retired.TryAdd(name, sentence);
					}

					// Before the key rename below: a key about to be renamed still carries its value under the old
					// name at this point.
					if (LegacyValues.TryGetValue(name, out Dictionary<string, string>? moved)
						&& child.Value is YamlScalarNode { Value: { Length: > 0 } stated } value
						&& moved.TryGetValue(stated, out string? replacement))
					{
						logger?.LogInformation(
							"'{Setting}: {Old}' is no longer a setting this application has; it now reads as "
							+ "'{New}', and the next save from the browser writes that.",
							name, stated, replacement);

						value.Value = replacement;
						used = true;
					}

					if (!LegacyKeys.TryGetValue(name, out string? currentName))
						continue;

					mapping.Children.Remove(child.Key);
					used = true;

					// Both names present. The current schema's name wins; the legacy key is dropped.
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
					used |= Translate(item, logger, retired);

				break;
		}

		return used;
	}

	/// <summary>
	///     The value nodes of every top-level key naming this document's section, and the only part of the file
	///     <see cref="Translate"/> may rewrite. Matched the way <see cref="Deserialize"/> matches.
	/// </summary>
	private static IEnumerable<YamlNode> SectionsOf(YamlNode root) =>
		root is not YamlMappingNode mapping
			? []
			: mapping.Children
				.Where(child => child.Key is YamlScalarNode { Value: { Length: > 0 } key }
					&& (string.Equals(key, RootKey, StringComparison.OrdinalIgnoreCase)
						|| key.EndsWith("." + nameof(AdaptiveLightingConfig), StringComparison.OrdinalIgnoreCase)))
				.Select(child => child.Value);

	/// <summary>Whether <paramref name="mapping"/> already carries <paramref name="key"/>, matched as the binder matches.</summary>
	/// <remarks>
	///     Ordinal, because YamlDotNet is: a lower-case <c>areas:</c> binds to nothing, so matching case-insensitively
	///     here would read <c>Zones:</c> beside <c>areas:</c> as the both-keys case and drop the only key that binds.
	/// </remarks>
	private static bool HasKey(YamlMappingNode mapping, string key) =>
		mapping.Children.Keys.OfType<YamlScalarNode>()
			.Any(scalar => string.Equals(scalar.Value, key, StringComparison.Ordinal));
}
