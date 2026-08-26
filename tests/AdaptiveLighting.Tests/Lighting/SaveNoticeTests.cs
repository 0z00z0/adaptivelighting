using System.Globalization;

using AdaptiveLighting.Web.Services;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The room page's one save surface, which is why the confirmation no longer floats over the page.</summary>
[TestClass]
public sealed class SaveNoticeTests
{
	private static readonly TimeSpan Lingers = TimeSpan.FromSeconds(3.5);

	private static readonly DateTimeOffset Saved = new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(1));

	[TestMethod]
	public void A_Refusal_Stands_Over_Everything_Else()
	{
		SaveNotice state = SaveNotice.Of(failed: true, dirty: true, Saved, Saved, Lingers);

		Assert.AreEqual(SaveNotice.Failed, state.Class);
		Assert.AreNotEqual(string.Empty, state.Text);
	}

	[TestMethod]
	public void A_Fresh_Change_Outranks_The_Confirmation_Of_The_Last_One()
	{
		SaveNotice state = SaveNotice.Of(failed: false, dirty: true, Saved, Saved, Lingers);

		Assert.AreEqual(SaveNotice.Pending, state.Class);
	}

	/// <summary>Compared against the same projection, never a written-out clock: CI runs in another zone.</summary>
	[TestMethod]
	public void A_Confirmation_Names_The_Moment_The_File_Was_Written()
	{
		SaveNotice state = SaveNotice.Of(failed: false, dirty: false, Saved, Saved + TimeSpan.FromSeconds(1), Lingers);

		Assert.AreEqual(SaveNotice.Done, state.Class);
		StringAssert.Contains(state.Text, Saved.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture));
	}

	[TestMethod]
	public void The_Confirmation_Goes_Once_It_Has_Had_Its_Few_Seconds()
	{
		SaveNotice state = SaveNotice.Of(failed: false, dirty: false, Saved, Saved + Lingers, Lingers);

		Assert.AreEqual(SaveNotice.Idle, state.Class);
		Assert.AreEqual(string.Empty, state.Text);
	}

	[TestMethod]
	public void A_Page_That_Has_Saved_Nothing_Says_Nothing()
	{
		SaveNotice state = SaveNotice.Of(failed: false, dirty: false, null, Saved, Lingers);

		Assert.AreEqual(SaveNotice.Idle, state.Class);
		Assert.AreEqual(string.Empty, state.Text);
	}

	/// <summary>Every state reaches the reader through one line, so nothing has to be floated over the page to be seen.</summary>
	[TestMethod]
	public void Each_State_Is_Told_Apart_By_Its_Own_Class()
	{
		string[] classes =
		[
			SaveNotice.Of(true, false, null, Saved, Lingers).Class,
			SaveNotice.Of(false, true, null, Saved, Lingers).Class,
			SaveNotice.Of(false, false, Saved, Saved, Lingers).Class,
			SaveNotice.Of(false, false, null, Saved, Lingers).Class
		];

		Assert.AreEqual(classes.Length, classes.Distinct(StringComparer.Ordinal).Count(), string.Join(" / ", classes));
	}
}
