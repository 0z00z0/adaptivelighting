using System.Globalization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdaptiveLighting.Lamplight;

/// <summary>Serves Lamplight on its own port, beside the first design, in the same process.</summary>
public static class LamplightSite
{
	/// <summary>The setting that turns Lamplight on. Absent, empty or 0 leaves the process exactly as without it.</summary>
	public const string PortKey = "AdaptiveLighting:LamplightPort";

	/// <summary>The configured port, or <c>null</c> when Lamplight is off or the value is not a port.</summary>
	public static int? Port(IConfiguration configuration)
	{
		ArgumentNullException.ThrowIfNull(configuration);

		return int.TryParse(configuration[PortKey], NumberStyles.None, CultureInfo.InvariantCulture, out int port)
			&& port is > 0 and <= 65535
				? port
				: null;
	}

	/// <summary>Registers the routing rule that keeps each site on its own port.</summary>
	public static IServiceCollection AddLamplight(this IServiceCollection services)
	{
		ArgumentNullException.ThrowIfNull(services);

		services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, ListeningPortPolicy>());

		return services;
	}

	/// <summary>Maps Lamplight's pages to <paramref name="port"/> and keeps <paramref name="firstDesign"/> off it.</summary>
	/// <remarks>Both roots claim "/", so the first design has to be tagged as well, or "/" is ambiguous on every port.</remarks>
	public static RazorComponentsEndpointConventionBuilder MapLamplight(
		this IEndpointRouteBuilder endpoints,
		int port,
		RazorComponentsEndpointConventionBuilder firstDesign)
	{
		ArgumentNullException.ThrowIfNull(endpoints);
		ArgumentNullException.ThrowIfNull(firstDesign);

		firstDesign.WithMetadata(new ListeningPort(port, Excluded: true));

		return endpoints.MapRazorComponents<LamplightApp>()
			.AddInteractiveServerRenderMode()
			.WithMetadata(new ListeningPort(port, Excluded: false));
	}
}

/// <summary>An endpoint answers only on <see cref="Port"/>, or, when <see cref="Excluded"/>, on every port but it.</summary>
internal sealed record ListeningPort(int Port, bool Excluded);

// The port the connection arrived on, never the Host header: an add-on can map an outside port onto a different
// inside one, and a Host-based split then answers nothing on that site.
internal sealed class ListeningPortPolicy : MatcherPolicy, IEndpointSelectorPolicy
{
	public override int Order => 0;

	public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) =>
		endpoints.Any(endpoint => endpoint.Metadata.GetMetadata<ListeningPort>() is not null);

	public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
	{
		int local = httpContext.Connection.LocalPort;

		for (int i = 0; i < candidates.Count; i++)
		{
			if (!candidates.IsValidCandidate(i))
				continue;

			if (candidates[i].Endpoint?.Metadata.GetMetadata<ListeningPort>() is { } site && (local == site.Port) == site.Excluded)
				candidates.SetValidity(i, false);
		}

		return Task.CompletedTask;
	}
}
