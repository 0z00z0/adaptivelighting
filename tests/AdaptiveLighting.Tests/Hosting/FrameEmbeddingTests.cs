using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

using AdaptiveLighting.Configuration;
using AdaptiveLighting.Web;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AdaptiveLighting.Tests.Hosting;

/// <summary>What the two frame headers look like to a browser, with and without an address to embed from.</summary>
/// <remarks>
///     The endpoint below writes the values the framework itself writes, measured off a running host: Blazor's
///     interactive server endpoint sets the policy and ASP.NET Core's antiforgery sets <c>X-Frame-Options</c>.
///     Standing in for them keeps the test on the middleware without a Blazor render in it.
/// </remarks>
[TestClass]
public sealed class FrameEmbeddingTests
{
	private const string FrameworkPolicy = "frame-ancestors 'self'";
	private const string FrameworkXFrameOptions = "SAMEORIGIN";

	[TestMethod]
	public async Task With_No_Address_The_Headers_Are_The_Frameworks_Own()
	{
		using HttpResponseMessage response = await Answer(embedFrom: null);

		Assert.AreEqual(FrameworkPolicy, Header(response, "Content-Security-Policy"),
			"an untouched policy is what refuses every frame but this site's own");

		// Both sides, because a relaxed policy beside a surviving X-Frame-Options still refuses the frame, and a
		// removed X-Frame-Options beside the default policy is a header set nobody chose.
		Assert.AreEqual(FrameworkXFrameOptions, Header(response, "X-Frame-Options"));
	}

	[TestMethod]
	public async Task An_Address_Reaches_The_Policy_And_Takes_X_Frame_Options_With_It()
	{
		using HttpResponseMessage response = await Answer(["http://10.0.0.5:8123", "https://ha.example:443"]);

		Assert.AreEqual("frame-ancestors 'self' http://10.0.0.5:8123 https://ha.example",
			Header(response, "Content-Security-Policy"),
			"the site's own origin stays, and every configured one joins it");

		Assert.IsNull(Header(response, "X-Frame-Options"),
			"it carries one same-origin source and no cross-origin one, so leaving it refuses the frame anyway");
	}

	[TestMethod]
	public async Task A_Response_Carrying_Neither_Header_Is_Left_As_It_Was()
	{
		using HttpResponseMessage response = await Answer(["http://10.0.0.5:8123"], path: "/plain");

		Assert.IsNull(Header(response, "Content-Security-Policy"),
			"a static asset carries no policy today, and the setting is about relaxing one, not adding one");
	}

	/// <summary>The three ways a typed address misses, each measured against this parser rather than assumed.</summary>
	[TestMethod]
	public void An_Address_That_Is_Not_One_Is_Reported_And_Left_Out()
	{
		AdaptiveLightingConfig config = AdaptiveLightingConfig.CreateDefault();

		// No scheme parses as nothing at all; a wrong scheme parses perfectly and is a page no browser would frame
		// from; a scheme with no host after it parses and has nothing to match.
		config.Global.EmbedFrom = ["10.0.0.5:8123", "ftp://10.0.0.5", "javascript:alert(1)", "http://10.0.0.5:8123"];

		ValidationResult result = ConfigValidator.Validate(config);

		Assert.IsTrue(result.IsValid, "a mistyped address is an operator's typo, not a document the engine cannot run");

		foreach (string wrong in new[] { "10.0.0.5:8123", "ftp://10.0.0.5", "javascript:alert(1)" })
			Assert.IsTrue(
				result.Warnings.Any(warning => warning.Contains($"'{wrong}'", StringComparison.Ordinal)
					&& warning.Contains("EmbedFrom", StringComparison.Ordinal)),
				$"'{wrong}' has to be named; warnings were {string.Join(" | ", result.Warnings)}");

		CollectionAssert.AreEqual(
			new[] { "http://10.0.0.5:8123" },
			EmbedOrigin.ReadAll(config.Global.EmbedFrom).ToArray(),
			"only the readable one reaches the header");
	}

	private static string? Header(HttpResponseMessage response, string name) =>
		response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(", ", values) : null;

	/// <summary>Starts a host with the middleware over a stand-in endpoint and asks it once.</summary>
	private static async Task<HttpResponseMessage> Answer(IReadOnlyList<string>? embedFrom, string path = "/")
	{
		string root = Directory.CreateDirectory(
			Path.Combine(Path.GetTempPath(), $"al-embed-{Guid.NewGuid():N}")).FullName;

		WriteDocument(Path.Combine(root, "house.yaml"), embedFrom);

		int port = FreePort();

		WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
		builder.Configuration["AdaptiveLighting:ConfigPath"] = Path.Combine(root, "house.yaml");
		builder.Services.AddLightingWeb();

		await using WebApplication app = builder.Build();

		app.Urls.Add($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}");
		app.UseLightingFrameEmbedding();

		app.MapGet("/", (HttpResponse response) =>
		{
			response.Headers["Content-Security-Policy"] = FrameworkPolicy;
			response.Headers["X-Frame-Options"] = FrameworkXFrameOptions;

			return "the page";
		});

		app.MapGet("/plain", () => "an asset");

		await app.StartAsync();

		try
		{
			using HttpClient client = new();

			return await client.GetAsync(
				new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{path}"));
		}
		finally
		{
			await app.StopAsync();
		}
	}

	private static void WriteDocument(string path, IReadOnlyList<string>? embedFrom)
	{
		string list = embedFrom is null
			? string.Empty
			: string.Concat(embedFrom.Select(origin => $"{Environment.NewLine}      - {origin}"));

		File.WriteAllText(path,
			$"""
			AdaptiveLighting.Configuration.AdaptiveLightingConfig:
			  Global:
			    EmbedFrom:{(embedFrom is null ? " []" : list)}
			""");
	}

	private static int FreePort()
	{
		TcpListener probe = new(IPAddress.Loopback, 0);
		probe.Start();
		int port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();

		return port;
	}
}
