using System.Text;
using System.Threading;

using Serilog.Debugging;

namespace AdaptiveLighting.NetDaemon;

/// <summary>The durable copy of the log: one capped file, one rolled generation, and nothing else in the directory.</summary>
/// <remarks>
///     <para>
///         Supervisor's own buffer holds the add-on log and two restarts overwrite it, so the same lines are kept
///         in the configuration document's directory, which survives a deploy.
///     </para>
///     <para>
///         The ceiling is <see cref="MaxFileBytes"/> twice over and does not move with time: rotation renames the
///         active file over the one rolled generation, so there is no numbered series and nothing to accumulate.
///     </para>
///     <para>
///         Every line is appended and closed. A held handle would be faster, and it is the tail of the file before
///         a crash that this exists to read.
///     </para>
/// </remarks>
public sealed class CircularLogWriter
{
	/// <summary>The subdirectory the log lives in, beside the document instead of on top of it.</summary>
	public const string FolderName = "log";

	/// <summary>What one file may reach; there are two files, so the directory never exceeds twice this.</summary>
	public const int MaxFileBytes = 10 * 1024 * 1024;

	/// <summary>What one line may reach, so no single event can approach <see cref="MaxFileBytes"/>.</summary>
	public const int MaxLineChars = 4096;

	private const string Extension = ".log";
	private const string RolledInfix = ".1";
	private const string Ellipsis = "...";

	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

	private readonly Lock _gate = new();
	private readonly int _maxFileBytes;
	private readonly Action<string> _report;

	// Whether the last attempt failed, so a sink that cannot write reports once instead of once per event.
	private bool _failing;

	/// <summary>Creates a writer over a directory, which is created if it is missing.</summary>
	/// <param name="directory">Where both files go, normally <see cref="FolderName"/> beside the document.</param>
	/// <param name="stem">The document's stem, so two houses sharing a directory cannot collide.</param>
	/// <param name="maxFileBytes">Overrides <see cref="MaxFileBytes"/>, for tests.</param>
	/// <param name="report">
	///     Where a failure is announced, standard error by default. Never an <c>ILogger</c>: a failure sent back
	///     through the logging pipeline arrives here again.
	/// </param>
	public CircularLogWriter(
		string directory,
		string stem,
		int maxFileBytes = MaxFileBytes,
		Action<string>? report = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		ArgumentException.ThrowIfNullOrWhiteSpace(stem);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);

		DirectoryPath = Path.GetFullPath(directory);
		ActivePath = Path.Combine(DirectoryPath, stem + Extension);
		RolledPath = Path.Combine(DirectoryPath, stem + RolledInfix + Extension);
		_maxFileBytes = maxFileBytes;
		_report = report ?? Console.Error.WriteLine;

		TryCreateDirectory();
	}

	/// <summary>The directory both files live in.</summary>
	public string DirectoryPath { get; }

	/// <summary>The file being appended to.</summary>
	public string ActivePath { get; }

	/// <summary>The one previous generation, overwritten by the next rotation and never joined by a second.</summary>
	public string RolledPath { get; }

	/// <summary>Appends one line; never throws, and never reports through <c>ILogger</c>, which would come back here.</summary>
	public void Append(string line)
	{
		ArgumentNullException.ThrowIfNull(line);

		byte[] payload = Utf8NoBom.GetBytes(Cap(line) + Environment.NewLine);

		lock (_gate)
		{
			try
			{
				long current = Length(ActivePath);

				// Before the write, so the active file never exceeds the cap. An empty file is never rolled: a line
				// longer than the whole cap would otherwise rotate on every call and keep nothing.
				if (current > 0 && current + payload.Length > _maxFileBytes)
					File.Move(ActivePath, RolledPath, overwrite: true);

				using FileStream file = new(ActivePath, FileMode.Append, FileAccess.Write, FileShare.Read);
				file.Write(payload);

				_failing = false;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
			{
				Failed($"AdaptiveLighting could not append to {ActivePath}: {exception.Message}");
			}
		}
	}

	private static long Length(string path)
	{
		FileInfo file = new(path);

		return file.Exists ? file.Length : 0;
	}

	private static string Cap(string line)
	{
		if (line.Length <= MaxLineChars)
			return line;

		int cut = MaxLineChars;

		if (char.IsHighSurrogate(line[cut - 1]))
			cut--;

		return string.Concat(line.AsSpan(0, cut), Ellipsis);
	}

	private void TryCreateDirectory()
	{
		try
		{
			Directory.CreateDirectory(DirectoryPath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Failed($"AdaptiveLighting could not create {DirectoryPath}: {exception.Message}");
		}
	}

	/// <summary>Announces a failure once per run of them, so an outage is not doubled by a report per event.</summary>
	private void Failed(string message)
	{
		SelfLog.WriteLine("{0}", message);

		if (_failing)
			return;

		// Before the recovery below, which reports through here again if it fails too.
		_failing = true;

		if (!Directory.Exists(DirectoryPath))
			TryCreateDirectory();

		_report(message);
	}
}
