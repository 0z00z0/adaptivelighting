using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AdaptiveLighting.Tests.Web;

/// <summary>What the base address resolves to for an ingress header of the right shape, the wrong shape,
/// absent, or repeated.</summary>
[TestClass]
public sealed class IngressBasePathTests
{
	[TestMethod]
	public void No_Header_Or_A_Header_Of_The_Wrong_Shape_Leaves_The_Base_At_The_Root()
	{
		Assert.AreEqual("/", IngressBasePath.Resolve(null));
		Assert.AreEqual("/", IngressBasePath.Resolve(""));

		Assert.AreEqual("/", IngressBasePath.Resolve("/api/hassio_ingress/../../etc"), "a .. segment climbs out");
		Assert.AreEqual("/", IngressBasePath.Resolve("/api//hassio_ingress/abc"), "a doubled slash");
		Assert.AreEqual("/", IngressBasePath.Resolve("//evil.example/api"), "protocol-relative, another host");
		Assert.AreEqual("/", IngressBasePath.Resolve("api/hassio_ingress/abc"), "no leading slash");
		Assert.AreEqual("/", IngressBasePath.Resolve("https://evil.example/"), "a scheme");
		Assert.AreEqual("/", IngressBasePath.Resolve("/api/\"><script>x</script>"), "markup");
		Assert.AreEqual("/", IngressBasePath.Resolve("/api/x?y=1"), "a query");
		Assert.AreEqual("/", IngressBasePath.Resolve("/api/" + new string('a', 300)), "longer than the cap");
	}

	[TestMethod]
	public void A_Valid_Prefix_Becomes_The_Base_With_One_Trailing_Slash()
	{
		// Home Assistant Core sends the prefix without a trailing slash, so the slash is this code's to add:
		// "<base href>" without it drops the last segment from every relative link on the page.
		Assert.AreEqual("/api/hassio_ingress/faketoken123/", IngressBasePath.Resolve("/api/hassio_ingress/faketoken123"));
		Assert.AreEqual("/api/hassio_ingress/faketoken123/", IngressBasePath.Resolve("/api/hassio_ingress/faketoken123/"));
		Assert.AreEqual("/", IngressBasePath.Resolve("/"));
	}

	[TestMethod]
	public void No_Request_And_No_Header_Both_Leave_The_Base_At_The_Root()
	{
		Assert.AreEqual("/", IngressBasePath.For(null));
		Assert.AreEqual("/", IngressBasePath.For(new DefaultHttpContext()));
	}

	[TestMethod]
	public void The_Header_Is_Read_Off_The_Request_Under_Its_Own_Name()
	{
		// Indexed by the constant, so a header name that drifts from it fails here instead of only in a browser.
		DefaultHttpContext context = new();
		context.Request.Headers[IngressBasePath.HeaderName] = "/api/hassio_ingress/faketoken123";

		Assert.AreEqual("/api/hassio_ingress/faketoken123/", IngressBasePath.For(context));
	}

	[TestMethod]
	public void A_Header_Sent_Twice_Leaves_The_Base_At_The_Root()
	{
		// Two values of one header read back comma-joined, and the comma is outside the allowed characters.
		DefaultHttpContext context = new();
		context.Request.Headers[IngressBasePath.HeaderName] = new StringValues(["/a", "/b"]);

		Assert.AreEqual("/", IngressBasePath.For(context));
	}
}
