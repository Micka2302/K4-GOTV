namespace K4GOTV;

internal static class DemoPaths
{
	public static string GetCsgoDirectory(string gameDirectory)
	{
		string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
		return Path.GetFileName(directory).Equals("csgo", StringComparison.OrdinalIgnoreCase)
			? directory
			: Path.Combine(directory, "csgo");
	}

	public static string GetRecordingPath(string demoDirectory, string fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(['/', '\\']) >= 0)
			throw new ArgumentException("Demo filename must be a single filename.", nameof(fileName));

		string path = Path.GetFullPath(Path.Combine(demoDirectory, fileName)).Replace('\\', '/');
		if (path.Any(c => char.IsControl(c) || c is '"' or ';'))
			throw new ArgumentException("Demo path contains characters unsafe for a server command.", nameof(demoDirectory));
		return path;
	}
}
