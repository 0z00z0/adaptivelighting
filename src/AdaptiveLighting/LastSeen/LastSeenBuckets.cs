using System.Security.Cryptography;
using System.Text;

namespace AdaptiveLighting.LastSeen;

/// <summary>
///     Decides which file an entity's record belongs in, and turns that decision into a file name.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why the cache is split at all.</b> For the person who opens the folder, not for the disk. Somebody
///         diagnosing "are my light-level sensors still reporting?" should open one small file and see only those,
///         rather than reading past 250 motion entries to find four.
///     </para>
///     <para>
///         <b>Why the bucket is a string and no longer an enum.</b> The first version split four ways —
///         illuminance, motion, light and a catch-all — and on a live house the catch-all was 94% of the bytes:
///         647 KB against 44 KB for the other three together. One file holding the whole rest of a large Home
///         Assistant instance defeats the reason the split exists, so the catch-all is now split by the entity's
///         own class. Device classes are open-ended — they are data from an external system, and a custom
///         integration may declare one nobody has heard of — so a fixed enum cannot hold them and the key is a
///         string.
///     </para>
///     <para>
///         <b>The keying rule, in order.</b> The three curated buckets come first and are unchanged: a light by
///         its domain, motion by the household's label or by a motion device class on a binary sensor, illuminance
///         by that device class on a sensor. Only after those does the class-based rule apply: an entity with a
///         <c>device_class</c> is filed under it — <c>temperature</c>, <c>battery</c>, <c>door</c>, <c>power</c> —
///         and an entity with none is filed under its <b>domain</b>, which is the only other thing its id actually
///         says about it and which gives self-describing files: <c>person</c>, <c>sun</c>, <c>automation</c>,
///         <c>script</c>, <c>input_boolean</c>.
///     </para>
///     <para>
///         <b>A class and a domain that share a name share a file, and that is left alone deliberately.</b> A
///         classless <c>button.x</c> and an <c>event.x</c> declaring <c>device_class: button</c> both land in
///         <c>button</c>. Prefixing every key with its domain would separate them, at the cost of turning
///         <c>temperature</c> into <c>sensor_temperature</c> for the entire cache — which is a worse file list for
///         a much rarer problem, and the two are the same word about the same thing anyway.
///     </para>
///     <para>
///         <b>Nothing is ever dropped, and that is load-bearing rather than tidy.</b> Device classes are absent or
///         surprising often enough on real hardware that a design which discarded what it could not classify would
///         quietly stop tracking exactly the entities most likely to misbehave. Every rule here ends somewhere:
///         an entity with no class, no usable domain and no curated match is filed under
///         <see cref="Unclassified"/> and tracked in full.
///     </para>
///     <para>
///         <b>The three curated tokens are reserved.</b> A <c>binary_sensor</c> may legitimately declare
///         <c>device_class: light</c> — it detects light, it is not a lamp — and a house may rename what counts as
///         motion. Filing either under the curated token would change what the curated files hold, which is the one
///         thing this split was not allowed to do. Such an entity is filed under <c>&lt;domain&gt;_&lt;class&gt;</c>
///         instead: <c>binary_sensor_light</c> says exactly the surprising thing a person would want named.
///     </para>
///     <para>
///         <b>Deliberately self-contained rather than calling the engine's resolver.</b> The resolver answers a
///         different question — "what does <i>this room</i> resolve to", with area membership, include and exclude
///         labels, light groups and liveness all having a say — and it needs the area registry to do it. Filing
///         needs none of that: it is one entity, one bucket, no configuration. Sharing the code would couple a
///         disposable cache's layout to the engine's room-resolution rules, so the rules are mirrored instead,
///         which is what the comments on <see cref="LastSeenOptions.MotionDeviceClasses"/> and
///         <see cref="LastSeenOptions.MotionLabel"/> record.
///     </para>
/// </remarks>
public static class LastSeenBuckets
{
	private const string LightDomain = "light";
	private const string BinarySensorDomain = "binary_sensor";
	private const string SensorDomain = "sensor";

	/// <summary>Lights: what the engine commands. A curated bucket, filed by domain alone.</summary>
	public const string Light = "light";

	/// <summary>Motion and presence sources: what the occupancy decision reads. A curated bucket.</summary>
	public const string Motion = "motion";

	/// <summary>Light-level sensors: what the darkness decision reads, and the reason this module was asked for.</summary>
	public const string Illuminance = "illuminance";

	/// <summary>
	///     The defined home for an entity that has nothing to be filed under — no device class, no usable domain.
	/// </summary>
	/// <remarks>
	///     Keeps the name the pre-split catch-all had, and deliberately: an installation upgrading already has a
	///     <c>.last-seen.other.json</c>, and reusing the name means that file is read rather than orphaned. Its
	///     records are re-keyed by class on the first census and moved, after which the bucket is empty and the file
	///     removes itself. What is left in it afterwards is only what genuinely has no class and no domain, which is
	///     a much smaller and much more interesting set than the one it used to hold.
	/// </remarks>
	public const string Unclassified = "other";

	/// <summary>
	///     What a file token falls back to when a bucket key has nothing a file name can represent.
	/// </summary>
	/// <remarks>
	///     Never used alone: it always carries the key's fingerprint, so two unrepresentable keys get two files
	///     rather than silently sharing one. The document's own <c>kind</c> field carries the real key back.
	/// </remarks>
	private const string UnnamedToken = "unnamed";

	/// <summary>
	///     How much of a bucket key survives into the file name.
	/// </summary>
	/// <remarks>
	///     A device class is data from an external system and reaches the file system, so its length is not this
	///     module's to trust. Real classes are a dozen characters; anything longer is truncated and fingerprinted,
	///     which keeps the whole name — stem, infix, token, extension and a <c>.bak</c> — comfortably inside every
	///     path limit worth worrying about.
	/// </remarks>
	private const int MaxTokenLength = 48;

	/// <summary>The buckets whose contents are decided by rule rather than by an entity's class, in file order.</summary>
	public static readonly IReadOnlyList<string> Curated = [Illuminance, Motion, Light];

	private static readonly HashSet<string> CuratedSet = new(Curated, StringComparer.Ordinal);

	/// <summary>Whether <paramref name="bucket"/> is one of the three buckets filed by curated rule.</summary>
	/// <param name="bucket">A bucket key.</param>
	/// <returns><c>true</c> for <c>illuminance</c>, <c>motion</c> and <c>light</c>.</returns>
	public static bool IsCurated(string? bucket) => bucket is not null && CuratedSet.Contains(bucket);

	/// <summary>
	///     Works out which bucket <paramref name="entityId"/> belongs in.
	/// </summary>
	/// <remarks>
	///     Never fails and never returns "unknown". The label is checked before the device class for motion, because
	///     the label exists precisely for the hardware whose device class is wrong. Everything after the curated
	///     rules is the entity's own class, then its domain, then <see cref="Unclassified"/> — so the answer is
	///     always a bucket that will be tracked exactly as thoroughly as any other.
	/// </remarks>
	/// <param name="entityId">The entity id. Its domain is half the answer, and all of it for a classless entity.</param>
	/// <param name="deviceClass">The entity's <c>device_class</c> attribute, or <c>null</c> when it has none.</param>
	/// <param name="labels">The labels the entity registration carries, if any.</param>
	/// <param name="options">Supplies the device-class and label conventions.</param>
	/// <returns>The bucket key, lower-cased and trimmed. Never empty, never <c>null</c>, never throws.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="options"/> is <c>null</c>.</exception>
	public static string Classify(
		string? entityId,
		string? deviceClass,
		IEnumerable<string>? labels,
		LastSeenOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		if (entityId is not { Length: > 0 })
			return Unclassified;

		if (entityId.HasDomain(LightDomain))
			return Light;

		if (labels is not null
			&& options.MotionLabel is { Length: > 0 } motionLabel
			&& labels.Contains(motionLabel, StringComparer.OrdinalIgnoreCase))
			return Motion;

		if (entityId.HasDomain(BinarySensorDomain)
			&& deviceClass is { Length: > 0 }
			&& options.MotionDeviceClasses.Contains(deviceClass, StringComparer.OrdinalIgnoreCase))
			return Motion;

		if (entityId.HasDomain(SensorDomain)
			&& string.Equals(deviceClass, options.IlluminanceDeviceClass, StringComparison.OrdinalIgnoreCase))
			return Illuminance;

		string domain = Normalise(entityId.Domain());
		string derived = Normalise(deviceClass);

		if (derived.Length == 0)
			derived = domain;

		if (derived.Length == 0)
			return Unclassified;

		if (!CuratedSet.Contains(derived))
			return derived;

		// The entity did not earn a curated bucket but its class is named after one. Naming the domain as well keeps
		// the curated files holding exactly what they held before, and says the odd thing out loud.
		return domain.Length > 0 && !string.Equals(domain, derived, StringComparison.Ordinal)
			? domain + "_" + derived
			: Unclassified;
	}

	/// <summary>
	///     The word that goes in the file name for <paramref name="bucket"/>, and it is safe to put there.
	/// </summary>
	/// <remarks>
	///     <para>
	///         <b>A bucket key is untrusted data that reaches the file system.</b> Home Assistant's device classes
	///         are lower-case ASCII today, but the value arrives from an external system and a custom integration
	///         can put anything in it — a separator, a traversal, a control character, a paragraph. So the token is
	///         built from an allow-list rather than by removing the characters somebody thought of: only
	///         <c>a-z</c>, <c>0-9</c> and <c>_</c> survive, and everything else is dropped.
	///     </para>
	///     <para>
	///         <b>Dropping characters would let two different classes collide onto one file</b> — <c>a/b</c> and
	///         <c>a\b</c> both reduce to <c>ab</c> — and one file holding two classes' histories is exactly the
	///         quiet data loss this whole module exists to avoid. So any key that was changed at all, by dropping
	///         or by truncation, gets a fingerprint of the original appended.
	///     </para>
	///     <para>
	///         <b>Why <c>-</c> is not in the allow-list, which is the part that looks arbitrary.</b> It is the
	///         fingerprint's separator, and excluding it from clean tokens is what makes the mapping provably
	///         injective: a token containing <c>-</c> is always a fingerprinted one, a token without it is always
	///         the key verbatim, and neither form can be reached from two different keys. Real device classes and
	///         domains use <c>_</c>, so nothing is lost by it.
	///     </para>
	/// </remarks>
	/// <param name="bucket">A bucket key, as <see cref="Classify"/> returns or as a document records.</param>
	/// <returns>A file-name-safe token. Distinct keys always give distinct tokens.</returns>
	public static string FileToken(string? bucket)
	{
		string key = Normalise(bucket);

		if (key.Length == 0)
			return Unclassified;

		StringBuilder safe = new(key.Length);
		bool altered = false;

		foreach (char character in key)
			if (character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
				safe.Append(character);
			else
				altered = true;

		if (safe.Length > MaxTokenLength)
		{
			safe.Length = MaxTokenLength;
			altered = true;
		}

		if (!altered)
			return safe.ToString();

		return (safe.Length > 0 ? safe.ToString() : UnnamedToken) + "-" + Fingerprint(key);
	}

	/// <summary>
	///     The bucket key a file name carries, for a file whose contents could not be trusted to say.
	/// </summary>
	/// <remarks>
	///     A last resort, and only correct for a token that was never fingerprinted — which is every real bucket.
	///     The document's own <c>kind</c> field is the truth; this exists so a file whose body lost that field is
	///     still filed somewhere sensible rather than being merged into the catch-all.
	/// </remarks>
	/// <param name="token">The token from between the infix and the extension.</param>
	/// <returns>The key it most likely names, or <see cref="Unclassified"/>.</returns>
	public static string FromToken(string? token)
	{
		string key = Normalise(token);
		return key.Length > 0 && string.Equals(FileToken(key), key, StringComparison.Ordinal) ? key : Unclassified;
	}

	/// <summary>
	///     Trimmed and lower-cased: two spellings of one class are one bucket, not two files.
	/// </summary>
	/// <remarks>
	///     Case folding is not cosmetic. Most file systems this runs on are case-insensitive, so
	///     <c>Temperature</c> and <c>temperature</c> would otherwise be two keys fighting over one file — two
	///     divergent histories of the same entities. Folding them into one bucket is both correct and what a reader
	///     would expect, since the two spellings mean the same class.
	/// </remarks>
	/// <param name="raw">A key as it arrived — from a device class, a domain, or a document written earlier.</param>
	/// <returns>The canonical key, or an empty string when there was nothing in it.</returns>
	public static string NormaliseKey(string? raw) => Normalise(raw);

	private static string Normalise(string? value) => value is null ? string.Empty : value.Trim().ToLowerInvariant();

	/// <summary>
	///     Eight hex characters of SHA-256 over the key.
	/// </summary>
	/// <remarks>
	///     A cryptographic hash rather than <see cref="string.GetHashCode()"/> because this ends up in a file name:
	///     the runtime's string hash is randomised per process, so a record written today would be looked for under
	///     a different name tomorrow.
	/// </remarks>
	private static string Fingerprint(string key) =>
		Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 4));
}
