using System.Reflection;
using System.Reflection.Emit;

using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Web;

/// <summary>The one version derivation the Configuration page's row and the layout's feedback link both read.
/// A build's commit suffix must never reach either of them.</summary>
[TestClass]
public sealed class AppVersionTests
{
	[TestMethod]
	public void The_Commit_Suffix_SourceLink_Appends_Is_Dropped()
	{
		string text = AppVersion.Read(WithInformationalVersion("2026.9.5+abc123def456"));

		Assert.AreEqual("2026.9.5", text);
	}

	[TestMethod]
	public void A_Version_Without_A_Suffix_Is_Left_Alone()
	{
		string text = AppVersion.Read(WithInformationalVersion("2026.9.5"));

		Assert.AreEqual("2026.9.5", text);
	}

	[TestMethod]
	public void Without_The_Attribute_The_Assembly_Names_Own_Version_Is_Used()
	{
		AssemblyBuilder built = AssemblyBuilder.DefineDynamicAssembly(
			new AssemblyName("AdaptiveLighting.Tests.Unattributed") { Version = new Version(3, 2, 1, 0) },
			AssemblyBuilderAccess.Run);

		Assert.AreEqual("3.2.1.0", AppVersion.Read(built));
	}

	[TestMethod]
	public void The_Running_Version_Is_Reported_Without_A_Suffix()
	{
		Assert.IsFalse(string.IsNullOrWhiteSpace(AppVersion.Text));
		Assert.IsFalse(AppVersion.Text.Contains('+'), $"'{AppVersion.Text}' must carry no commit suffix");
	}

	private static Assembly WithInformationalVersion(string version)
	{
		AssemblyBuilder built = AssemblyBuilder.DefineDynamicAssembly(
			new AssemblyName($"AdaptiveLighting.Tests.Versioned{Guid.NewGuid():N}"),
			AssemblyBuilderAccess.Run);

		built.SetCustomAttribute(new CustomAttributeBuilder(
			typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
			[version]));

		return built;
	}
}
