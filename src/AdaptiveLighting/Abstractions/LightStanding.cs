namespace AdaptiveLighting.Abstractions;

/// <summary>One light holding a level of its own: what the engine last commanded it.</summary>
/// <remarks>A <c>null</c> brightness is a light commanded off.</remarks>
public sealed record LightStanding(string EntityId, double? BrightnessPct, int? ColorTempKelvin);
