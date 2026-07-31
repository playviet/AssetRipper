using System.Diagnostics;

namespace AssetRipper.IO.Files;

public partial class LocalFileSystem : FileSystem
{
	public static LocalFileSystem Instance { get; } = new();

	public partial class LocalFileImplementation
	{
	}

	public partial class LocalDirectoryImplementation
	{
		public override void Create(string path) => System.IO.Directory.CreateDirectory(path);

		public override void Delete(string path) => System.IO.Directory.Delete(path, true);
	}

	public static string ExecutingDirectory => AppContext.BaseDirectory;

	private string LocalTemporaryDirectory => Path.Join(ExecutingDirectory, "temp", GetRandomString()[0..4]);

	private string SystemTemporaryDirectory => Path.Join(System.IO.Path.GetTempPath(), "AssetRipper", GetRandomString()[0..4]);

	/// <summary>
	/// Whether <see cref="TemporaryDirectory"/> is one this class chose, rather than one a caller pointed it at.
	/// </summary>
	/// <remarks>
	/// Only a directory of our own making is safe to delete recursively on exit. A caller is free to point this at any
	/// path, and clearing that out from under them would destroy whatever else lives there.
	/// </remarks>
	private bool ownsTemporaryDirectory;

	public override string TemporaryDirectory
	{
		get
		{
			if (string.IsNullOrEmpty(field))
			{
				field = LocalTemporaryDirectory;
				Debug.Assert(!Directory.Exists(field));
				try
				{
					Directory.Create(field);
					File.WriteAllText(Path.Join(field, ".WriteTest"), "test");
					Directory.Delete(field);
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException)
				{
					field = SystemTemporaryDirectory;
				}
				ownsTemporaryDirectory = true;

				// Reading a game extracts archives here and keeps them for as long as that game is loaded, so nothing
				// can delete them earlier. Without this, every run leaves its extracted copy behind; ten runs over one
				// Android build filled several gigabytes.
				AppDomain.CurrentDomain.ProcessExit += DeleteOnExit;
			}
			return field;
		}
		set
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				field = Path.GetFullPath(value);
				ownsTemporaryDirectory = false;
			}
		}
	}

	private void DeleteOnExit(object? sender, EventArgs e) => DeleteOwnedTemporaryDirectory();

	/// <summary>
	/// Deletes the temporary directory, but only when this class chose where it is.
	/// </summary>
	/// <returns>True when there is nothing of ours left behind.</returns>
	public bool DeleteOwnedTemporaryDirectory()
	{
		return !ownsTemporaryDirectory || DeleteTemporaryDirectory();
	}
}
