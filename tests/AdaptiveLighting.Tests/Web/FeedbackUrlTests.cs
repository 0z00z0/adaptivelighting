using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Web;

/// <summary>The layout's feedback link: every value in it must round-trip through the query string exactly, so
/// nothing typed as a version can silently corrupt the link.</summary>
[TestClass]
public sealed class FeedbackUrlTests
{
	[TestMethod]
	public void The_Link_Points_At_A_New_Issue_On_This_Repository()
	{
		string url = FeedbackUrl.Build("2026.9.5");

		Assert.IsTrue(url.StartsWith("https://github.com/0z00z0/adaptivelighting/issues/new?", StringComparison.Ordinal));
	}

	[TestMethod]
	public void The_Link_Is_A_Well_Formed_Absolute_Uri()
	{
		string url = FeedbackUrl.Build("2026.9.5");

		Assert.IsTrue(Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed), $"'{url}' must parse as an absolute URI");
		Assert.AreEqual("https", parsed!.Scheme);
	}

	[TestMethod]
	public void The_Body_Carries_The_Running_Version()
	{
		string url = FeedbackUrl.Build("2026.9.5");

		StringAssert.Contains(QueryValue(url, "body"), "2026.9.5");
	}

	[TestMethod]
	public void A_Version_Containing_Query_Breaking_Characters_Round_Trips_Untouched()
	{
		// '&' and '=' would start a new parameter or a new value if left unescaped; '#' would truncate the
		// query as a fragment marker. None of this is a version anyone would ship, but nothing here trusts that.
		const string awkward = "2026.9.5&x=1#frag\nnext line";

		string url = FeedbackUrl.Build(awkward);
		IReadOnlyDictionary<string, string> query = ParseQuery(url);

		Assert.AreEqual(2, query.Count, "the awkward characters must not introduce extra query parameters");
		StringAssert.Contains(query["body"], awkward);
	}

	[TestMethod]
	public void The_Title_Is_Left_For_The_Person_To_Finish()
	{
		string url = FeedbackUrl.Build("2026.9.5");

		StringAssert.Contains(QueryValue(url, "title"), "Feedback");
	}

	private static string QueryValue(string url, string name) => ParseQuery(url)[name];

	/// <summary>A minimal, dependency-free query-string reader: the point of the awkward-characters test is that
	/// decoding here must land back on exactly what <see cref="FeedbackUrl.Build"/> was given.</summary>
	private static Dictionary<string, string> ParseQuery(string url)
	{
		Uri parsed = new(url, UriKind.Absolute);
		Dictionary<string, string> result = new(StringComparer.Ordinal);

		foreach (string pair in parsed.Query.TrimStart('?').Split('&'))
		{
			string[] parts = pair.Split('=', 2);
			result[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
		}

		return result;
	}
}
