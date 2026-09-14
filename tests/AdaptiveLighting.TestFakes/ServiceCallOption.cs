namespace AdaptiveLighting.TestFakes;

/// <summary>Reads a recorded <see cref="ServiceCall"/>'s anonymous "option" field, the shape a select_option call takes.</summary>
public static class ServiceCallOption
{
	public static string? Option(this ServiceCall call) => call.Data?.GetType().GetProperty("option")?.GetValue(call.Data) as string;
}
