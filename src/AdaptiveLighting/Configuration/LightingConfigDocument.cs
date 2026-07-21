using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace AdaptiveLighting.Configuration;

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

		// OmitNull, not OmitDefaults: on a ZoneConfig every settings property is a nullable twin where null
		// means "inherit Defaults", so a null must not be written. But a zone that deliberately sets
		// Enabled: false or LuxThreshold: 0 has said something, and OmitDefaults would delete it.
		ISerializer serializer = new SerializerBuilder()
			.ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
			.Build();

		Dictionary<string, AdaptiveLightingConfig> document = new(StringComparer.Ordinal) { [RootKey] = config };

		return Header + serializer.Serialize(document);
	}

	/// <summary>
	///     Parses the text of a configuration document.
	/// </summary>
	/// <param name="yaml">The file's contents.</param>
	/// <returns>The bound document. Never <c>null</c>.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="yaml"/> is <c>null</c>.</exception>
	/// <exception cref="LightingConfigException">
	///     The text is not YAML, or carries no <see cref="RootKey"/> section.
	/// </exception>
	public static AdaptiveLightingConfig Deserialize(string yaml)
	{
		ArgumentNullException.ThrowIfNull(yaml);

		// IgnoreUnmatchedProperties mirrors the .NET configuration binder, which also ignores keys it does not
		// know. Being stricter here would mean a stale key left in a hand-edited file bricks the UI that exists
		// to fix exactly that kind of thing.
		IDeserializer deserializer = new DeserializerBuilder()
			.IgnoreUnmatchedProperties()
			.Build();

		Dictionary<string, AdaptiveLightingConfig?>? document;

		try
		{
			document = deserializer.Deserialize<Dictionary<string, AdaptiveLightingConfig?>>(yaml);
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
		string? match = document.Keys.FirstOrDefault(key => string.Equals(key, RootKey, StringComparison.OrdinalIgnoreCase));

		if (match is null)
			throw new LightingConfigException(
				$"The configuration file has no '{RootKey}' section. It has: {string.Join(", ", document.Keys)}.");

		// A present-but-empty section parses to null. That is a legitimate starting point — an operator who
		// deleted everything below the key — and it means "all defaults, no zones", which the validator will
		// then reject with a message that says so.
		return document[match] ?? new AdaptiveLightingConfig();
	}
}
