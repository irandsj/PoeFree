using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PoeFree.Core.Game;

public enum GameVersion {
	Poe1,
	Poe2,
}

public enum GamePlatform {
	/// <summary>Standalone client from pathofexile.com (Content.ggpk).</summary>
	Standalone,
	Steam,
	Epic,
	/// <summary>Anything the user picked manually.</summary>
	Custom,
}

/// <summary>A detected Path of Exile installation.</summary>
/// <param name="ArchivePath">Content.ggpk (standalone) or Bundles2/_.index.bin (Steam/Epic).</param>
public sealed record GameInstall(GameVersion Version, GamePlatform Platform, string ArchivePath) {
	public string GameDirectory => ArchivePath.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase)
		? Path.GetDirectoryName(ArchivePath)!
		: Path.GetDirectoryName(Path.GetDirectoryName(ArchivePath)!)!;

	public string DisplayName => $"{(Version == GameVersion.Poe1 ? "Path of Exile 1" : "Path of Exile 2")} - {Platform} ({ArchivePath})";
}

/// <summary>Finds Path of Exile installs for the standalone client, Steam and Epic.</summary>
public static partial class GameDetector {
	static readonly (GameVersion Version, string Folder)[] Folders = [
		(GameVersion.Poe1, "Path of Exile"),
		(GameVersion.Poe2, "Path of Exile 2"),
	];

	public static List<GameInstall> FindInstalls() {
		var result = new List<GameInstall>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		void Add(GameVersion v, GamePlatform p, string path) {
			if (File.Exists(path) && seen.Add(Path.GetFullPath(path)))
				result.Add(new GameInstall(v, p, Path.GetFullPath(path)));
		}

		foreach (var dir in StandaloneFolders())
			foreach (var (v, folder) in Folders) {
				var d = Path.Combine(dir, folder);
				Add(v, GamePlatform.Standalone, Path.Combine(d, "Content.ggpk"));
				// GGG sometimes installs PoE2 into "Path of Exile 2 - poe2_production"
				if (v == GameVersion.Poe2 && Directory.Exists(dir))
					foreach (var alt in SafeDirs(dir).Where(x => Path.GetFileName(x).StartsWith("Path of Exile 2", StringComparison.OrdinalIgnoreCase)))
						Add(v, GamePlatform.Standalone, Path.Combine(alt, "Content.ggpk"));
			}
		foreach (var (v, dir) in RegistryStandaloneInstalls())
			Add(v, GamePlatform.Standalone, Path.Combine(dir, "Content.ggpk"));

		foreach (var common in SteamCommonFolders())
			foreach (var (v, folder) in Folders)
				Add(v, GamePlatform.Steam, Path.Combine(common, folder, "Bundles2", "_.index.bin"));

		foreach (var (v, dir) in EpicInstalls()) {
			Add(v, GamePlatform.Epic, Path.Combine(dir, "Bundles2", "_.index.bin"));
			Add(v, GamePlatform.Epic, Path.Combine(dir, "Content.ggpk"));
		}
		return result;
	}

	/// <summary>Guesses the game version from an archive path and, for a GGPK, from its header version (PoE2 uses version 4).</summary>
	public static GameVersion GuessVersion(string archivePath, uint? ggpkVersion = null) {
		if (ggpkVersion is >= 4)
			return GameVersion.Poe2;
		var p = archivePath.Replace('\\', '/');
		return Poe2PathRegex().IsMatch(p) ? GameVersion.Poe2 : GameVersion.Poe1;
	}

	public static GamePlatform GuessPlatform(string archivePath) {
		var p = archivePath.Replace('\\', '/');
		if (p.Contains("/steamapps/", StringComparison.OrdinalIgnoreCase))
			return GamePlatform.Steam;
		if (p.Contains("/Epic Games/", StringComparison.OrdinalIgnoreCase))
			return GamePlatform.Epic;
		return archivePath.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase) ? GamePlatform.Standalone : GamePlatform.Custom;
	}

	static IEnumerable<string> StandaloneFolders() {
		foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles" }) {
			var pf = Environment.GetEnvironmentVariable(env);
			if (!string.IsNullOrEmpty(pf))
				yield return Path.Combine(pf, "Grinding Gear Games");
		}
		foreach (var drive in SafeDrives())
			yield return Path.Combine(drive, "Grinding Gear Games");
	}

	static IEnumerable<(GameVersion, string)> RegistryStandaloneInstalls() {
		if (!OperatingSystem.IsWindows())
			yield break;
		foreach (var (v, key) in new[] { (GameVersion.Poe1, @"Software\GrindingGearGames\Path of Exile"), (GameVersion.Poe2, @"Software\GrindingGearGames\Path of Exile 2") }) {
			string? dir = null;
			try {
				using var k = Registry.CurrentUser.OpenSubKey(key);
				dir = k?.GetValue("InstallLocation") as string;
			} catch {
				// ignore
			}
			if (!string.IsNullOrWhiteSpace(dir))
				yield return (v, dir);
		}
	}

	/// <summary>All <c>steamapps/common</c> folders of every Steam library.</summary>
	public static IEnumerable<string> SteamCommonFolders() {
		var roots = new List<string>();
		if (OperatingSystem.IsWindows()) {
			try {
				using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
				if (k?.GetValue("SteamPath") is string sp)
					roots.Add(sp.Replace('/', '\\'));
			} catch {
				// ignore
			}
			var pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
			if (!string.IsNullOrEmpty(pf86))
				roots.Add(Path.Combine(pf86, "Steam"));
		} else {
			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			roots.Add(Path.Combine(home, ".steam", "steam"));
			roots.Add(Path.Combine(home, ".local", "share", "Steam"));
			roots.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
		}

		var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var root in roots) {
			if (!Directory.Exists(root))
				continue;
			libraries.Add(Path.GetFullPath(root));
			var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
			if (!File.Exists(vdf))
				continue;
			try {
				foreach (Match m in VdfPathRegex().Matches(File.ReadAllText(vdf)))
					libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
			} catch {
				// ignore
			}
		}
		foreach (var lib in libraries) {
			var common = Path.Combine(lib, "steamapps", "common");
			if (Directory.Exists(common))
				yield return common;
		}
	}

	/// <summary>Install folders of Epic Games titles (used to look for Path of Exile and for oo2core copies).</summary>
	public static IEnumerable<string> EpicGameFolders() {
		foreach (var (_, dir) in EpicManifests())
			yield return Path.GetDirectoryName(dir.TrimEnd('\\', '/')) ?? dir;
		var pf = Environment.GetEnvironmentVariable("ProgramFiles");
		if (!string.IsNullOrEmpty(pf) && Directory.Exists(Path.Combine(pf, "Epic Games")))
			yield return Path.Combine(pf, "Epic Games");
	}

	static IEnumerable<(GameVersion, string)> EpicInstalls() {
		foreach (var (name, dir) in EpicManifests()) {
			if (!name.Contains("Path of Exile", StringComparison.OrdinalIgnoreCase))
				continue;
			yield return (name.Contains('2') ? GameVersion.Poe2 : GameVersion.Poe1, dir);
		}
	}

	static IEnumerable<(string DisplayName, string InstallLocation)> EpicManifests() {
		var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
		var dir = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
		if (!Directory.Exists(dir))
			yield break;
		foreach (var file in SafeFiles(dir, "*.item")) {
			string? name = null, location = null;
			try {
				using var doc = JsonDocument.Parse(File.ReadAllText(file));
				name = doc.RootElement.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
				location = doc.RootElement.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
			} catch {
				// ignore broken manifests
			}
			if (name is not null && location is not null)
				yield return (name, location);
		}
	}

	static IEnumerable<string> SafeDrives() {
		try {
			return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
		} catch {
			return [];
		}
	}

	static IEnumerable<string> SafeDirs(string dir) {
		try {
			return Directory.GetDirectories(dir);
		} catch {
			return [];
		}
	}

	static IEnumerable<string> SafeFiles(string dir, string pattern) {
		try {
			return Directory.GetFiles(dir, pattern);
		} catch {
			return [];
		}
	}

	[GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
	private static partial Regex VdfPathRegex();

	[GeneratedRegex(@"Path of Exile 2|PathOfExile2|poe2", RegexOptions.IgnoreCase)]
	private static partial Regex Poe2PathRegex();
}
