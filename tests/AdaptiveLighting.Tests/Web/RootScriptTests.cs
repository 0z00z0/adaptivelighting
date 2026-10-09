using System.Text.RegularExpressions;

namespace AdaptiveLighting.Tests.Web;

/// <summary>Both designs share components that call page scripts, so each root page has to load every script they call.</summary>
[TestClass]
public sealed class RootScriptTests
{
	private const string FirstRoot = "src/AdaptiveLighting.Web/App.razor";
	private const string LamplightRoot = "src/AdaptiveLighting.Lamplight/LamplightApp.razor";

	// Theme scripts belong to their own root; the other design has its own picker and never calls them.
	private static readonly Dictionary<string, string> OwnRoot = new(StringComparer.Ordinal)
	{
		["adaptiveLightingTheme"] = FirstRoot,
		["lamplightTheme"] = LamplightRoot,
	};

	/// <summary>A shared component calling a script its root never loads fails at run time, in the browser, with nothing in the build to say so.</summary>
	[TestMethod]
	public void Every_Script_Global_A_Component_Calls_Is_Loaded_By_Each_Root_That_Renders_It()
	{
		string root = RepositoryRoot();
		Dictionary<string, string> definedIn = ScriptDefinitions(root);
		HashSet<string> called = CalledGlobals(root);

		// A scan that finds nothing would pass; these two are known to exist.
		CollectionAssert.IsSubsetOf(
			new[] { "adaptiveLightingPresetSlider", "adaptiveLightingCurve" },
			called.ToArray());

		List<string> missing = [];

		foreach (string global in called.Order(StringComparer.Ordinal))
		{
			if (!definedIn.TryGetValue(global, out string? script))
			{
				missing.Add($"{global}: no script under wwwroot defines it");
				continue;
			}

			foreach (string page in new[] { FirstRoot, LamplightRoot })
			{
				if (OwnRoot.TryGetValue(global, out string? owner) && owner != page)
					continue;

				string html = File.ReadAllText(Path.Combine(root, page));

				if (!html.Contains(script, StringComparison.Ordinal))
					missing.Add($"{Path.GetFileName(page)} does not load {script}, which defines {global}");
			}
		}

		Assert.AreEqual(0, missing.Count, string.Join(Environment.NewLine, missing));
	}

	// The global's name is the part of the call string before its first dot.
	private static HashSet<string> CalledGlobals(string root)
	{
		Regex call = new(@"Invoke(?:Void)?Async(?:<[^>]+>)?\(\s*""(?<name>(?:adaptiveLighting|lamplight)\w*)\.", RegexOptions.CultureInvariant);
		HashSet<string> found = new(StringComparer.Ordinal);

		foreach (string project in new[] { "AdaptiveLighting.Web", "AdaptiveLighting.Lamplight" })
		{
			foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.*", SearchOption.AllDirectories)
				.Where(f => (f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
					&& !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
					&& !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
			{
				foreach (Match match in call.Matches(File.ReadAllText(file)))
					found.Add(match.Groups["name"].Value);
			}
		}

		return found;
	}

	// Global name to the published path a root page has to name: _content/{project}/{file}.
	private static Dictionary<string, string> ScriptDefinitions(string root)
	{
		Regex define = new(@"^\s*window\.(?<name>\w+)\s*=", RegexOptions.Multiline | RegexOptions.CultureInvariant);
		Dictionary<string, string> map = new(StringComparer.Ordinal);

		foreach (string project in new[] { "AdaptiveLighting.Web", "AdaptiveLighting.Lamplight" })
		{
			foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", project, "wwwroot"), "*.js"))
			{
				foreach (Match match in define.Matches(File.ReadAllText(file)))
					map[match.Groups["name"].Value] = $"_content/{project}/{Path.GetFileName(file)}";
			}
		}

		return map;
	}

	// Walks up from the test binary. Failing loudly beats a scan that quietly finds no files and passes.
	private static string RepositoryRoot()
	{
		DirectoryInfo? at = new(AppContext.BaseDirectory);

		while (at is not null && !File.Exists(Path.Combine(at.FullName, "AdaptiveLighting.slnx")))
			at = at.Parent;

		Assert.IsNotNull(at, $"no AdaptiveLighting.slnx above {AppContext.BaseDirectory}");

		return at.FullName;
	}
}
