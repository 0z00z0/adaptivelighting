using System.Reflection;
using System.Xml.Linq;

namespace AdaptiveLighting.Tests.Tooling;

/// <summary>The core test set names tests by hand; a rename that misses this file drops the test silently.</summary>
[TestClass]
public sealed class CoreRunSettingsTests
{
	[TestMethod]
	public void Every_Name_In_The_Core_Set_Resolves_To_A_Real_Test()
	{
		Assembly assembly = typeof(CoreRunSettingsTests).Assembly;
		List<string> stale = [];

		foreach (string entry in Entries())
		{
			if (entry.StartsWith("FullyQualifiedName~", StringComparison.Ordinal))
			{
				string className = entry["FullyQualifiedName~".Length..].TrimEnd('.');
				Type? type = assembly.GetType(className);

				if (type is null || !HasTestMethod(type))
					stale.Add(entry);
			}
			else if (entry.StartsWith("FullyQualifiedName=", StringComparison.Ordinal))
			{
				string full = entry["FullyQualifiedName=".Length..];
				int split = full.LastIndexOf('.');
				Type? type = split < 0 ? null : assembly.GetType(full[..split]);
				string methodName = split < 0 ? full : full[(split + 1)..];

				if (type is null || !HasTestMethod(type, methodName))
					stale.Add(entry);
			}
			else
			{
				stale.Add(entry);
			}
		}

		Assert.AreEqual(0, stale.Count, "renamed or removed since the list was written: " + string.Join(", ", stale));
	}

	private static bool HasTestMethod(Type type, string? methodName = null) =>
		type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
			.Any(method =>
				(methodName is null || method.Name == methodName)
				&& method.GetCustomAttributes(typeof(TestMethodAttribute), inherit: true).Length > 0);

	/// <summary>The filter's own tokens, comments already excluded because <see cref="XElement.Value"/> skips <see cref="XComment"/> nodes.</summary>
	private static IEnumerable<string> Entries()
	{
		string path = System.IO.Path.Combine(RepositoryRoot(), "tests", "core.runsettings");
		XElement filter = XDocument.Load(path).Descendants("TestCaseFilter").Single();

		return filter.Value
			.Split('|')
			.Select(token => token.Trim())
			.Where(token => token.Length > 0);
	}

	private static string RepositoryRoot()
	{
		DirectoryInfo? at = new(AppContext.BaseDirectory);

		while (at is not null && !File.Exists(System.IO.Path.Combine(at.FullName, "AdaptiveLighting.slnx")))
			at = at.Parent;

		Assert.IsNotNull(at, $"no AdaptiveLighting.slnx above {AppContext.BaseDirectory}");

		return at.FullName;
	}
}
