using System.Text;

namespace AdaptiveLighting.Configuration;

/// <summary>
///     A configuration problem scoped to a single zone. These degrade rather than throw: the zone is skipped
///     and the rest of the house keeps working.
/// </summary>
/// <param name="ZoneName">Display name of the offending zone.</param>
/// <param name="Message">What is wrong, phrased for someone editing the YAML.</param>
public sealed record ZoneError(string ZoneName, string Message);

/// <summary>
///     The outcome of <see cref="ConfigValidator.Validate"/>. Document-level <see cref="Errors"/> are fatal —
///     the app must throw. <see cref="ZoneErrors"/> are not: an entity renamed in HA must not black out the
///     whole house.
/// </summary>
public sealed class ValidationResult
{
	private readonly List<string> _errors = [];
	private readonly List<ZoneError> _zoneErrors = [];
	private readonly List<string> _warnings = [];

	/// <summary>Document-level errors. Non-empty means the configuration cannot be run at all.</summary>
	public IReadOnlyList<string> Errors => _errors;

	/// <summary>Zone-level errors. Each names one zone that will be skipped.</summary>
	public IReadOnlyList<ZoneError> ZoneErrors => _zoneErrors;

	/// <summary>Non-blocking warnings. Rendered and logged, but they never refuse a save or stop the engine.</summary>
	public IReadOnlyList<string> Warnings => _warnings;

	/// <summary>Whether the document can be run. Zone errors and warnings do not make a document invalid.</summary>
	public bool IsValid => _errors.Count == 0;

	/// <summary>Records a fatal, document-level error.</summary>
	public void AddError(string message) => _errors.Add(message);

	/// <summary>Records a zone that must be skipped, and why.</summary>
	public void AddZoneError(string zoneName, string message) => _zoneErrors.Add(new ZoneError(zoneName, message));

	/// <summary>Records a non-blocking warning — worth surfacing, but not worth refusing the document over.</summary>
	public void AddWarning(string message) => _warnings.Add(message);

	/// <summary>Plain-text rendering for log output and exception messages.</summary>
	public override string ToString()
	{
		StringBuilder text = new();

		foreach (string error in _errors)
			text.Append("- ").AppendLine(error);

		foreach (ZoneError zoneError in _zoneErrors)
			text.Append("- [").Append(zoneError.ZoneName).Append("] ").AppendLine(zoneError.Message);

		if (_warnings.Count > 0)
		{
			text.AppendLine("Warnings:");
			foreach (string warning in _warnings)
				text.Append("- ").AppendLine(warning);
		}

		return text.Length == 0 ? "No configuration problems." : text.ToString();
	}

	/// <summary>HTML rendering for the persistent notification, whose message body accepts markup.</summary>
	public string ToHtml()
	{
		StringBuilder text = new("<ul>");

		foreach (string error in _errors)
			text.Append("<li>").Append(Escape(error)).Append("</li>");

		foreach (ZoneError zoneError in _zoneErrors)
			text.Append("<li><b>").Append(Escape(zoneError.ZoneName)).Append("</b>: ").Append(Escape(zoneError.Message)).Append("</li>");

		text.Append("</ul>");

		if (_warnings.Count > 0)
		{
			text.Append("<p><b>Warnings</b></p><ul>");
			foreach (string warning in _warnings)
				text.Append("<li>").Append(Escape(warning)).Append("</li>");
			text.Append("</ul>");
		}

		return text.ToString();
	}

	private static string Escape(string text) =>
		text.Replace("&", "&amp;", StringComparison.Ordinal)
			.Replace("<", "&lt;", StringComparison.Ordinal)
			.Replace(">", "&gt;", StringComparison.Ordinal);
}
