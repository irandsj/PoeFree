namespace PoeFree.Core;

/// <summary>Where PoeFree keeps settings, profiles, backups and logs.</summary>
public static class AppPaths {
	static string? overrideDir;

	/// <summary>%APPDATA%\PoeFree on Windows, ~/.config/PoeFree elsewhere. Can be overridden with the POEFREE_HOME environment variable.</summary>
	public static string DataDirectory => overrideDir ??= Environment.GetEnvironmentVariable("POEFREE_HOME") is { Length: > 0 } env
		? env
		: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PoeFree");

	public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
	public static string ProfilesDirectory => Path.Combine(DataDirectory, "profiles");
	public static string BackupsDirectory => Path.Combine(DataDirectory, "backups");
	public static string LogsDirectory => Path.Combine(DataDirectory, "logs");
	public static string IgnoreListFile => Path.Combine(DataDirectory, "ignore-list.txt");

	public static void SetDataDirectory(string dir) => overrideDir = dir;
}
