using System.Reflection;

namespace AdaptiveLighting.Web.Services;

/// <summary>Routable components a host contributes beyond the pages this library ships.</summary>
/// <remarks>
///     A house registers none, so its router sees exactly the pages it always did. The developer host registers
///     itself, which is how a component can be looked at without a Home Assistant registry to resolve a room
///     against: <c>FakeHaRegistry</c> answers no areas, and <c>Area</c> has no public constructor to give it any.
/// </remarks>
public sealed record AdditionalRoutes(IReadOnlyList<Assembly> Assemblies);
