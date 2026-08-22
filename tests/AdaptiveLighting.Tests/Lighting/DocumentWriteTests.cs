using AdaptiveLighting.Configuration;
using AdaptiveLighting.Hosting;
using AdaptiveLighting.Web.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.Lighting;

/// <summary>The settings editor's guard: it sends the whole document, but never over somebody else's write.</summary>
[TestClass]
public sealed class DocumentWriteTests
{
	private string _directory = "";
	private string _path = "";

	[TestInitialize]
	public void CreateTempDirectory()
	{
		_directory = Path.Combine(Path.GetTempPath(), $"lighting-tests-{Guid.NewGuid():N}");
		Directory.CreateDirectory(_directory);
		_path = Path.Combine(_directory, "AdaptiveLighting.yaml");
	}

	[TestCleanup]
	public void RemoveTempDirectory()
	{
		if (Directory.Exists(_directory))
			Directory.Delete(_directory, recursive: true);
	}

	private LightingConfigStore BuildStore() =>
		new(_path, NullLogger<LightingConfigStore>.Instance);

	private static AdaptiveLightingConfig House() => new()
	{
		ConfigName = "Adaptive lighting [test]",
		Periods = [new TimePeriodConfig { Name = "day", Start = "06:00", BrightnessPct = 80, ColorTempKelvin = 3500 }],
		Areas = [new AreaConfig { AreaId = "stue" }]
	};

	[TestMethod]
	public void ChangedUnderneath_WhenNothingHasBeenWrittenSince_IsFalse()
	{
		LightingConfigStore store = BuildStore();
		store.Save(House(), InvalidDocument.Refuse);

		string stamp = ConfigStamp.OfDocument(store.Load());

		Assert.IsFalse(DocumentWrite.ChangedUnderneath(store, stamp));
	}

	/// <summary>Any part of the file, including a room this page is not looking at.</summary>
	[TestMethod]
	public void ChangedUnderneath_WhenAnotherWriterAddedARoom_IsTrue()
	{
		LightingConfigStore store = BuildStore();
		store.Save(House(), InvalidDocument.Refuse);

		string stamp = ConfigStamp.OfDocument(store.Load());

		AdaptiveLightingConfig other = store.Load();
		other.Areas.Add(new AreaConfig { AreaId = "kjokken" });
		store.Save(other, InvalidDocument.Refuse);

		Assert.IsTrue(DocumentWrite.ChangedUnderneath(store, stamp));
	}

	/// <summary>A fresh install has no file, so the first save has nothing to conflict with.</summary>
	[TestMethod]
	public void ChangedUnderneath_WithNoFileAtAll_IsFalse() =>
		Assert.IsFalse(DocumentWrite.ChangedUnderneath(BuildStore(), stamp: ""));

	/// <summary>Repairing a broken file is what this page's save is for, so it must not be refused.</summary>
	[TestMethod]
	public void ChangedUnderneath_WhenTheFileWillNotParse_IsFalse()
	{
		File.WriteAllText(_path, "this: [is not: a document");

		Assert.IsFalse(DocumentWrite.ChangedUnderneath(BuildStore(), stamp: ""));
	}

	[TestMethod]
	public void Conflict_IsRefusedAndSaysWhatToPress()
	{
		SaveResult conflict = DocumentWrite.Conflict(null);

		Assert.AreEqual(SaveStatus.Conflicted, conflict.Status);
		Assert.IsFalse(conflict.Written);
		StringAssert.Contains(conflict.Message, "Discard changes", StringComparison.Ordinal);
	}

	/// <summary>The save bar counts the problems off this, so a conflict must not blank them.</summary>
	[TestMethod]
	public void Conflict_CarriesTheValidationThePageAlreadyHad()
	{
		ValidationResult validation = new();
		validation.AddError("something the page already knew about");

		Assert.AreSame(validation, DocumentWrite.Conflict(validation).Validation);
	}
}
