using AdaptiveLighting.Lamplight;

namespace AdaptiveLighting.Tests.Web;

/// <summary>The base address Lamplight's pages declare. The header arrives from the client's side of a proxy
/// and this UI has no login of its own, so anything outside the allowed shape has to read as no prefix at all.</summary>
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
}
