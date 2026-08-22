using System.Globalization;

using AdaptiveLighting.NetDaemon;

using Serilog.Events;

namespace AdaptiveLighting.Tests.Logging;

/// <summary>The filter every value crosses on its way to the durable log: what it drops, and what it must not.</summary>
[TestClass]
public sealed class LoggedValueTests
{
	private const string LongLivedToken =
		"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiI5YjEyIiwiaWF0IjoxNzU0MzUyMDAwfQ.Qm9ndXNTaWduYXR1cmVGb3JBVGVzdA";

	private static LogEventPropertyValue Value(object? raw) => new ScalarValue(raw);

	private sealed class Formattable : IFormattable
	{
		private readonly string _text;

		public Formattable(string text) => _text = text;

		public string ToString(string? format, IFormatProvider? formatProvider) => _text;
	}

	// ===================== a credential-shaped name =====================

	[TestMethod]
	public void A_Property_Whose_Name_Reads_As_A_Credential_Is_Replaced_Whole()
	{
		foreach (string name in (string[])["Token", "AccessToken", "Password", "SambaPassword", "Pwd", "Secret", "ApiKey", "api_key", "Credential", "ConnectionString"])
			Assert.AreEqual(LoggedValue.Hidden, LoggedValue.Of(name, Value("hunter2")), name);
	}

	[TestMethod]
	public void A_Credential_Nested_Inside_A_Structure_Is_Replaced_By_Its_Own_Name()
	{
		StructureValue structure = new([
			new LogEventProperty("Host", new ScalarValue("nas")),
			new LogEventProperty("Password", new ScalarValue("hunter2"))
		]);

		string rendered = LoggedValue.Of("Share", structure);

		StringAssert.Contains(rendered, "Host=nas");
		StringAssert.Contains(rendered, "Password=" + LoggedValue.Hidden);
		Assert.IsFalse(rendered.Contains("hunter2", StringComparison.Ordinal), rendered);
	}

	[TestMethod]
	public void A_Credential_In_A_Sequence_Under_A_Credential_Name_Is_Replaced()
	{
		SequenceValue sequence = new([new ScalarValue("hunter2"), new ScalarValue("swordfish")]);

		Assert.AreEqual(LoggedValue.Hidden, LoggedValue.Of("Passwords", sequence));
	}

	// ===================== a credential-shaped value =====================

	[TestMethod]
	public void A_Home_Assistant_Long_Lived_Token_Is_Replaced_Wherever_It_Sits()
	{
		string rendered = LoggedValue.Text($"connecting to ws://ha:8123 with {LongLivedToken} now");

		Assert.IsFalse(rendered.Contains("eyJ", StringComparison.Ordinal), rendered);
		StringAssert.Contains(rendered, LoggedValue.Hidden);
		StringAssert.Contains(rendered, "ws://ha:8123");
	}

	[TestMethod]
	public void A_Credential_Written_As_A_Pair_Loses_Its_Value()
	{
		Assert.IsFalse(LoggedValue.Text("password=hunter2").Contains("hunter2", StringComparison.Ordinal));
		Assert.IsFalse(LoggedValue.Text("password: hunter2").Contains("hunter2", StringComparison.Ordinal));
		Assert.IsFalse(LoggedValue.Text("smb.conf says password = \"hunter2\"").Contains("hunter2", StringComparison.Ordinal));
	}

	/// <summary>A quoted key puts its closing quote between the key and the separator, which YAML does not.</summary>
	[TestMethod]
	[DataRow("{\"password\": \"hunter2\"}")]
	[DataRow("{\"password\":\"hunter2\"}")]
	[DataRow("{\"api_key\" : \"hunter2\"}")]
	[DataRow("{'token': 'hunter2'}")]
	[DataRow("password: hunter2")]
	[DataRow("password=hunter2")]
	[DataRow("secret = 'hunter2'")]
	public void A_Credential_Pair_Loses_Its_Value_However_It_Is_Spelled(string written)
	{
		string rendered = LoggedValue.Text(written);

		Assert.IsFalse(rendered.Contains("hunter2", StringComparison.Ordinal), rendered);
		StringAssert.Contains(rendered, LoggedValue.Hidden, StringComparison.Ordinal);
	}

	[TestMethod]
	public void A_Sentence_Mentioning_A_Credential_Keeps_The_Words_Around_It()
	{
		Assert.AreEqual(
			"the Samba password is wrong",
			LoggedValue.Text("the Samba password is wrong"));
	}

	[TestMethod]
	public void A_Samba_Credential_Written_As_A_Url_Loses_Its_User_Info()
	{
		string rendered = LoggedValue.Text("mounting smb://espen:hunter2@nas/config");

		Assert.IsFalse(rendered.Contains("hunter2", StringComparison.Ordinal), rendered);
		Assert.IsFalse(rendered.Contains("espen", StringComparison.Ordinal), rendered);
		StringAssert.Contains(rendered, "nas/config");
	}

	[TestMethod]
	public void A_Long_Opaque_Mixed_Case_Run_Is_Replaced_Even_Under_An_Innocent_Name()
	{
		string rendered = LoggedValue.Of("Value", Value("Kf3Qz9WbTn2LpXr7Vh4JdG8sYm1AcEuNqZ0iRoBt"));

		Assert.AreEqual(LoggedValue.Hidden, rendered);
	}

	// ===================== what has to survive =====================

	[TestMethod]
	public void An_Entity_Id_Survives_However_Long_It_Is()
	{
		const string entityId = "binary_sensor.kjeller_multimedia_bevegelse_2_occupancy";

		Assert.AreEqual(entityId, LoggedValue.Of("EntityId", Value(entityId)));
	}

	[TestMethod]
	public void A_Path_Survives_Although_It_Is_Long_And_Mixed_Case()
	{
		foreach (string path in (string[])["/config/adaptive-lighting/B1House2026.yaml", @"C:\Deploy\NetDaemon4\apps\AdaptiveLighting"])
			Assert.AreEqual(path, LoggedValue.Of("Path", Value(path)), path);
	}

	[TestMethod]
	public void A_Norwegian_Area_Name_Survives_Unchanged()
	{
		Assert.AreEqual("Kjøkken øst", LoggedValue.Of("AreaName", Value("Kjøkken øst")));
	}

	[TestMethod]
	public void An_Area_Id_Survives_Whether_It_Arrives_As_A_Guid_Or_As_Its_Text()
	{
		Guid id = Guid.NewGuid();

		Assert.AreEqual(id.ToString(), LoggedValue.Of("AreaId", Value(id)));
		Assert.AreEqual(id.ToString(), LoggedValue.Of("AreaId", Value(id.ToString())));
	}

	// ===================== shape of what is written =====================

	[TestMethod]
	public void A_Number_Is_Written_Invariantly_Whatever_The_Machines_Locale_Is()
	{
		const double brightness = 58.5;

		Assert.AreEqual(brightness.ToString(CultureInfo.InvariantCulture), LoggedValue.Of("BrightnessPct", Value(brightness)));
	}

	[TestMethod]
	public void A_Timestamp_Is_Written_Invariantly_And_Keeps_Its_Own_Offset()
	{
		DateTimeOffset midnight = new(2026, 8, 5, 0, 3, 12, TimeSpan.FromHours(2));

		Assert.AreEqual(midnight.ToString(null, CultureInfo.InvariantCulture), LoggedValue.Of("At", Value(midnight)));
	}

	[TestMethod]
	public void A_Newline_In_A_Value_Cannot_Split_The_Line_It_Is_Written_On()
	{
		string rendered = LoggedValue.Of("Reason", Value("first\r\nsecond\tthird"));

		Assert.IsFalse(rendered.Contains('\n'), rendered);
		Assert.IsFalse(rendered.Contains('\r'), rendered);
		Assert.IsFalse(rendered.Contains('\t'), rendered);
	}

	[TestMethod]
	public void A_Value_Is_Capped_So_One_Property_Cannot_Take_The_Whole_Line()
	{
		string rendered = LoggedValue.Of("Detail", Value(new string('æ', 5000)));

		Assert.IsTrue(rendered.Length <= LoggedValue.MaxValueLength + 3, rendered.Length.ToString(CultureInfo.InvariantCulture));
	}

	/// <summary>Nothing reaches the file except through the filter, including a branch that formats invariantly.</summary>
	[TestMethod]
	public void A_Formattable_Of_A_Call_Sites_Own_Type_Crosses_The_Filter_Like_A_String()
	{
		Assert.AreEqual(LoggedValue.Hidden, LoggedValue.Of("Value", Value(new Formattable(LongLivedToken))));

		string split = LoggedValue.Of("Value", Value(new Formattable("first\r\nsecond")));

		Assert.IsFalse(split.Contains('\n'), split);
		Assert.IsFalse(split.Contains('\r'), split);

		string capped = LoggedValue.Of("Value", Value(new Formattable(new string('x', 5000))));

		Assert.IsTrue(capped.Length <= LoggedValue.MaxValueLength + 3, capped.Length.ToString(CultureInfo.InvariantCulture));
	}

	[TestMethod]
	public void A_Null_Property_Reads_As_Null_Rather_Than_Empty()
	{
		Assert.AreEqual("null", LoggedValue.Of("PeriodName", Value(null)));
		Assert.AreEqual("null", LoggedValue.Of("PeriodName", value: null));
	}
}
