using System.Globalization;

using AdaptiveLighting.LastSeen;

using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveLighting.Tests.LastSeen;

/// <summary>What the split produces on a large Home Assistant instance.</summary>
// The counts are chosen so the printed totals compare with the live house's measured file sizes: the
// pre-split catch-all was 647 KB against 44 KB for the other three buckets together.
[TestClass]
public sealed class LastSeenLargeHouseTests
{
	private static readonly DateTimeOffset Noon = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

	private static readonly LastSeenOptions Options = new();

	/// <summary>Room and fixture words, so the generated ids are as long as real ones.</summary>
	private static readonly string[] Places =
	[
		"living_room", "kitchen", "hallway", "bedroom", "bathroom", "office", "garage", "basement",
		"attic", "front_porch", "garden", "utility", "landing", "guest_room", "pantry", "workshop"
	];

	/// <summary>One large Home Assistant instance by domain and device class, in roughly stock proportions.</summary>
	private static readonly (string Domain, string? DeviceClass, int Count)[] Population =
	[
		// ---- the three curated buckets, sized to the live house's measured files ----
		("light", null, 180),
		("binary_sensor", "motion", 48),
		("binary_sensor", "occupancy", 22),
		("binary_sensor", "presence", 6),
		("sensor", "illuminance", 43),

		// ---- classed sensors: the bulk of any instance that measures anything ----
		("sensor", "temperature", 260),
		("sensor", "energy", 220),
		("sensor", "battery", 200),
		("sensor", "power", 200),
		("sensor", "humidity", 130),
		("sensor", "voltage", 130),
		("sensor", "current", 130),
		("sensor", "signal_strength", 120),
		("sensor", "timestamp", 100),
		("sensor", "enum", 80),
		("sensor", "duration", 90),
		("sensor", "data_size", 80),
		("sensor", "data_rate", 70),
		("sensor", "pressure", 60),
		("sensor", "power_factor", 50),
		("sensor", "apparent_power", 45),
		("sensor", "reactive_power", 45),
		("sensor", "frequency", 40),
		("sensor", "atmospheric_pressure", 25),
		("sensor", "monetary", 24),
		("sensor", "carbon_dioxide", 20),
		("sensor", "speed", 20),
		("sensor", "pm25", 18),
		("sensor", "volatile_organic_compounds", 16),
		("sensor", "moisture", 12),
		("sensor", "wind_speed", 12),
		("sensor", "precipitation", 10),
		("sensor", "distance", 10),
		("sensor", "water", 10),
		("sensor", "gas", 10),
		("sensor", "irradiance", 8),
		("sensor", "aqi", 8),
		("sensor", "date", 8),
		("sensor", "weight", 6),
		("sensor", "sound_pressure", 6),

		// ---- and the ones nobody gave a class: uptimes, versions, statuses, text ----
		("sensor", null, 300),

		// ---- classed binary sensors that are not motion ----
		("binary_sensor", "connectivity", 110),
		("binary_sensor", "battery", 90),
		("binary_sensor", "problem", 70),
		("binary_sensor", "update", 60),
		("binary_sensor", "door", 45),
		("binary_sensor", "running", 40),
		("binary_sensor", "window", 38),
		("binary_sensor", "battery_charging", 30),
		("binary_sensor", "opening", 25),
		("binary_sensor", "tamper", 25),
		("binary_sensor", "plug", 20),
		("binary_sensor", "moisture", 18),
		("binary_sensor", "power", 15),
		("binary_sensor", "vibration", 14),
		("binary_sensor", "smoke", 12),
		("binary_sensor", "light", 10),
		("binary_sensor", "safety", 10),
		("binary_sensor", "lock", 8),
		("binary_sensor", "sound", 8),
		("binary_sensor", "cold", 6),
		("binary_sensor", "heat", 6),
		("binary_sensor", "gas", 6),
		("binary_sensor", "garage_door", 4),
		("binary_sensor", null, 60),

		// ---- everything else, mostly classless, filed by domain ----
		("number", null, 150),
		("automation", null, 140),
		("update", "firmware", 120),
		("select", null, 110),
		("switch", "outlet", 70),
		("script", null, 70),
		("switch", "switch", 60),
		("scene", null, 60),
		("button", "restart", 60),
		("switch", null, 50),
		("input_boolean", null, 45),
		("button", "identify", 40),
		("button", null, 40),
		("device_tracker", null, 40),
		("input_number", null, 30),
		("button", "update", 30),
		("cover", "shutter", 25),
		("event", "button", 25),
		("group", null, 20),
		("media_player", "speaker", 20),
		("input_select", null, 20),
		("cover", "blind", 15),
		("camera", null, 12),
		("climate", null, 12),
		("input_text", null, 12),
		("cover", "curtain", 10),
		("event", "motion", 10),
		("fan", null, 10),
		("text", null, 10),
		("input_datetime", null, 10),
		("media_player", "tv", 8),
		("calendar", null, 8),
		("timer", null, 8),
		("notify", null, 8),
		("zone", null, 8),
		("lock", null, 6),
		("image", null, 6),
		("time", null, 6),
		("remote", null, 6),
		("counter", null, 6),
		("tag", null, 6),
		("cover", "window", 6),
		("media_player", null, 6),
		("person", null, 5),
		("todo", null, 5),
		("schedule", null, 5),
		("cover", "door", 4),
		("media_player", "receiver", 4),
		("valve", "water", 4),
		("date", null, 4),
		("datetime", null, 4),
		("weather", null, 3),
		("siren", null, 3),
		("vacuum", null, 3),
		("humidifier", "humidifier", 3),
		("assist_satellite", null, 3),
		("cover", "garage", 3),
		("event", "doorbell", 3),
		("tts", null, 3),
		("alarm_control_panel", null, 2),
		("water_heater", null, 2),
		("humidifier", "dehumidifier", 2),
		("valve", "gas", 2),
		("cover", "awning", 2),
		("conversation", null, 2),
		("sun", null, 1),
		("stt", null, 1),
		("lawn_mower", null, 1)
	];

	[TestMethod]
	public void What_The_Split_Produces_On_A_Large_Home_Assistant_Instance()
	{
		string root = Path.Combine(Path.GetTempPath(), "adaptive-lighting-large-" + Guid.NewGuid().ToString("N"));

		try
		{
			Directory.CreateDirectory(Path.Combine(root, "after"));
			Directory.CreateDirectory(Path.Combine(root, "before"));

			LastSeenStore after = new(Path.Combine(root, "after", "b1.yaml"), NullLogger<LastSeenStore>.Instance);
			LastSeenStore before = new(Path.Combine(root, "before", "b1.yaml"), NullLogger<LastSeenStore>.Instance);

			Dictionary<string, List<string>> split = new(StringComparer.Ordinal);
			Dictionary<string, List<string>> preSplit = new(StringComparer.Ordinal);
			int population = 0;

			foreach ((string entityId, string? deviceClass) in Enumerate())
			{
				population++;

				string bucket = LastSeenBuckets.Classify(entityId, deviceClass, null, Options);

				Add(split, bucket, entityId);

				// What the same entity was filed under before the split: curated, or everything else.
				Add(preSplit, LastSeenBuckets.IsCurated(bucket) ? bucket : LastSeenBuckets.Unclassified, entityId);
			}

			Save(after, split);
			Save(before, preSplit);

			Report(after, before, population);

			// ---- guard rails, not a benchmark ----

			LastSeenCacheLoad load = after.Load();

			Assert.AreEqual(population, load.Entities.Count, "every entity has to land in exactly one file");
			Assert.AreEqual(0, load.DuplicatesResolved);
			Assert.AreEqual(split.Count, load.FilesRead);

			long total = after.FilePaths.Sum(path => new FileInfo(path).Length);
			long largest = after.FilePaths.Max(path => new FileInfo(path).Length);

			Assert.IsTrue(largest < total / 5,
				$"the largest bucket is {largest * 100 / total}% of the cache; the whole point of the split was that no single file is most of it");

			foreach (string path in after.FilePaths)
				Assert.AreEqual(-1, Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()), path);
		}
		finally
		{
			try
			{
				Directory.Delete(root, recursive: true);
			}
			catch (IOException)
			{
				// Litter, not a test failure.
			}
		}
	}

	private static IEnumerable<(string EntityId, string? DeviceClass)> Enumerate()
	{
		int serial = 0;

		foreach ((string domain, string? deviceClass, int count) in Population)
			for (int index = 0; index < count; index++)
			{
				string place = Places[serial++ % Places.Length];

				yield return (
					string.Create(CultureInfo.InvariantCulture, $"{domain}.{place}_{deviceClass ?? "state"}_{index}"),
					deviceClass);
			}
	}

	private static void Add(Dictionary<string, List<string>> buckets, string bucket, string entityId)
	{
		if (!buckets.TryGetValue(bucket, out List<string>? filed))
			buckets[bucket] = filed = [];

		filed.Add(entityId);
	}

	private static void Save(LastSeenStore store, Dictionary<string, List<string>> buckets)
	{
		foreach (KeyValuePair<string, List<string>> bucket in buckets)
		{
			LastSeenDocument document = new()
			{
				Bucket = bucket.Key,
				SavedAt = Noon,
				HomeAssistantStarted = Noon.AddHours(-6)
			};

			for (int index = 0; index < bucket.Value.Count; index++)
				document.Entities[bucket.Value[index]] = new LastSeenEntry(Noon.AddMinutes(-index), Noon.AddDays(-30));

			Assert.IsTrue(store.TrySave(bucket.Key, document), bucket.Key);
		}
	}

	/// <summary>Prints what landed in each file.</summary>
	private static void Report(LastSeenStore after, LastSeenStore before, int population)
	{
		List<FileInfo> files = [.. after.FilePaths.Select(path => new FileInfo(path)).OrderByDescending(file => file.Length)];
		long total = files.Sum(file => file.Length);

		Write(string.Create(CultureInfo.InvariantCulture,
			$"=== last-seen cache for a {population:N0}-entity Home Assistant instance ==="));
		Write("");

		foreach (FileInfo file in files)
			Write(string.Create(CultureInfo.InvariantCulture,
				$"{file.Name,-46} {file.Length,9:N0} B  {file.Length * 100.0 / total,5:0.0}%"));

		Write("");
		Write(string.Create(CultureInfo.InvariantCulture, $"{files.Count} files, {total:N0} B in total"));
		Write(string.Create(CultureInfo.InvariantCulture,
			$"largest: {files[0].Name} at {files[0].Length:N0} B, {files[0].Length * 100.0 / total:0.0}% of the cache"));
		Write("");
		Write("--- the same house before the catch-all was split ---");

		List<FileInfo> old = [.. before.FilePaths.Select(path => new FileInfo(path)).OrderByDescending(file => file.Length)];
		long wasTotal = old.Sum(file => file.Length);

		foreach (FileInfo file in old)
			Write(string.Create(CultureInfo.InvariantCulture,
				$"{file.Name,-46} {file.Length,9:N0} B  {file.Length * 100.0 / wasTotal,5:0.0}%"));
	}

	private static void Write(string line) => Console.WriteLine(line);
}
