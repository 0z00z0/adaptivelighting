namespace AdaptiveLighting.Web.Presentation;

/// <summary>How a stored value is rounded for a person to read.</summary>
public static class DisplayRounding
{
	/// <summary>A value as the whole number every ordinary readout shows.</summary>
	// Away from zero, not to the even neighbour: the editor, the collapsed summary and the file have to agree,
	// and 62.5 reading 62 in one place and 63 in another is the disagreement this exists to close.
	public static double Whole(double value) =>
		double.IsFinite(value) ? Math.Round(value, MidpointRounding.AwayFromZero) : value;

	/// <summary>A lit level as a whole number, never 0 while it is above zero.</summary>
	// Readouts only: the lowest step a light takes is about 0.39 %. The editors keep Whole.
	public static double WholeLit(double value) => value > 0 ? Math.Max(1, Whole(value)) : Whole(value);
}
