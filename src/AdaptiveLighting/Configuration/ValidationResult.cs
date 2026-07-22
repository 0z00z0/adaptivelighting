using System.Text;

namespace AdaptiveLighting.Configuration;

/// <summary>
///     A configuration problem scoped to a single area. These degrade rather than throw: the area is skipped
///     and the rest of the house keeps working.
/// </summary>
/// <param name="AreaName">Display name of the offending area.</param>
/// <param name="Message">What is wrong, phrased for someone editing the YAML.</param>
public sealed record AreaError(string AreaName, string Message);

/// <summary>
///     The outcome of <see cref="ConfigValidator.Validate"/>. Document-level <see cref="Errors"/> are fatal —
///     the app must throw. <see cref="AreaErrors"/> are not: an entity renamed in HA must not black out the
///     whole house.
/// </summary>
public sealed class ValidationResult
{
	private readonly List<string> _errors = [];
	private readonly List<AreaError> _areaErrors = [];
	private readonly List<string> _warnings = [];

	/// <summary>Document-level errors. Non-empty means the configuration cannot be run at all.</summary>
	public IReadOnlyList<string> Errors => _errors;

	/// <summary>Area-level errors. Each names one area that will be skipped.</summary>
	public IReadOnlyList<AreaError> AreaErrors => _areaErrors;

	/// <summary>Non-blocking warnings. Rendered and logged, but they never refuse a save or stop the engine.</summary>
	public IReadOnlyList<string> Warnings => _warnings;

	/// <summary>Whether the document can be run. Area errors and warnings do not make a document invalid.</summary>
	public bool IsValid => _errors.Count == 0;

	/// <summary>Records a fatal, document-level error.</summary>
	public void AddError(string message) => _errors.Add(message);

	/// <summary>Records an area that must be skipped, and why.</summary>
	public void AddAreaError(string areaName, string message) => _areaErrors.Add(new AreaError(areaName, message));

	/// <summary>Records a non-blocking warning — worth surfacing, but not worth refusing the document over.</summary>
	public void AddWarning(string message) => _warnings.Add(message);

	/// <summary>Plain-text rendering for log output and exception messages.</summary>
	public override string ToString()
	{
		StringBuilder text = new();

		foreach (string error in _errors)
			text.Append("- ").AppendLine(error);

		foreach (AreaError areaError in _areaErrors)
			text.Append("- [").Append(areaError.AreaName).Append("] ").AppendLine(areaError.Message);

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

		foreach (AreaError areaError in _areaErrors)
			text.Append("<li><b>").Append(Escape(areaError.AreaName)).Append("</b>: ").Append(Escape(areaError.Message)).Append("</li>");

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
