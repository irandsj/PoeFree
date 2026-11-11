using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace PoeFree.Core.Game;

/// <summary>What the update watcher needs to know about the game on this machine. Replaced by a fake in tests.</summary>
public interface IGameProbe {
	bool IsRunning(GameInstall install);

	/// <summary>Game version (patch CDN url) the installed client last started with, or null if unknown.</summary>
	string? InstalledVersion(GameInstall install);
}

/// <summary>
/// Read-only look at the game: is it running, and which version its own client log reports.
/// PoeFree never starts, closes or touches the game process.
/// </summary>
public sealed class GameProbe : IGameProbe {
	const string UpdateFinishedMarker = "Finished checking files";
	const string WebRootMarker = "] Web root: ";

	static readonly string[] Executables = [
		"PathOfExile.exe", "PathOfExile_x64.exe", "Client.exe",
		"PathOfExileSteam.exe", "PathOfExile_x64Steam.exe",
		"PathOfExileEGS.exe", "PathOfExile_x64EGS.exe",
		"PathOfExile_KG.exe", "PathOfExile_x64_KG.exe",
	];

	public bool IsRunning(GameInstall install) {
		var dir = Path.GetFullPath(install.GameDirectory).TrimEnd('\\', '/');
		var running = false;
		foreach (var name in Executables.Select(e => Path.GetFileNameWithoutExtension(e)).Distinct(StringComparer.OrdinalIgnoreCase)) {
			Process[] found;
			try {
				found = Process.GetProcessesByName(name);
			} catch {
				continue;
			}
			foreach (var p in found) {
				try {
					if (running)
						continue;
					var file = p.MainModule?.FileName;
					running = file is null || Path.GetFullPath(Path.GetDirectoryName(file)!).TrimEnd('\\', '/').Equals(dir, StringComparison.OrdinalIgnoreCase);
				} catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) {
					// Elevated or exiting process: its path cannot be read. Assume it is this install (the safe answer).
					running = !HasExited(p);
				} finally {
					p.Dispose();
				}
			}
		}
		return running;
	}

	static bool HasExited(Process p) {
		try {
			return p.HasExited;
		} catch {
			return false;
		}
	}

	public string? InstalledVersion(GameInstall install) {
		try {
			var logs = Path.Combine(install.GameDirectory, "logs");
			// LatestClient.txt holds only the last session, with the patch server answer near the top.
			var latest = Path.Combine(logs, "LatestClient.txt");
			if (File.Exists(latest) && ParseInstalledVersion(ReadRange(latest, 0, 4 << 20)) is { } v)
				return v;
			var full = Path.Combine(logs, "Client.txt");
			if (!File.Exists(full))
				return null;
			var length = new FileInfo(full).Length;
			foreach (var window in new long[] { 1 << 20, 16 << 20, 128 << 20 }) {
				var start = Math.Max(0, length - window);
				if (ParseInstalledVersion(ReadRange(full, start, length - start)) is { } found)
					return found;
				if (start == 0)
					break;
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// no log: version unknown
		}
		return null;
	}

	/// <summary>
	/// Finds the patch CDN url of the last game start that finished checking/updating its files.
	/// The client logs <c>Web root: https://patch.poecdn.com/3.29.3.6/</c> and, when it is up to date, <c>Finished checking files</c>.
	/// </summary>
	public static string? ParseInstalledVersion(string logText) {
		var end = logText.Length;
		while (end > 0) {
			var finished = logText.LastIndexOf(UpdateFinishedMarker, end - 1, StringComparison.Ordinal);
			if (finished < 0)
				return null;
			var root = logText.LastIndexOf(WebRootMarker, finished, StringComparison.Ordinal);
			if (root < 0)
				return null;
			// Both lines must belong to the same game start.
			var opening = logText.IndexOf("LOG FILE OPENING", root, finished - root, StringComparison.Ordinal);
			if (opening < 0) {
				var from = root + WebRootMarker.Length;
				var to = logText.IndexOfAny(['\r', '\n'], from);
				var url = (to < 0 ? logText[from..] : logText[from..to]).Trim();
				return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
			}
			end = root;
		}
		return null;
	}

	static string ReadRange(string file, long start, long count) {
		using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		if (start > fs.Length)
			return "";
		fs.Position = start;
		var buffer = new byte[Math.Min(count, fs.Length - start)];
		var read = 0;
		while (read < buffer.Length) {
			var n = fs.Read(buffer, read, buffer.Length - read);
			if (n == 0)
				break;
			read += n;
		}
		return Encoding.UTF8.GetString(buffer, 0, read);
	}

	/// <summary>Compares two patch CDN urls (e.g. <c>https://patch.poecdn.com/3.29.3.6/</c>).</summary>
	public static bool SameVersion(string a, string b) =>
		a.Trim().TrimEnd('/').Equals(b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

	/// <summary>The short version number of a patch CDN url: <c>https://patch.poecdn.com/3.29.3.6/</c> gives <c>3.29.3.6</c>.</summary>
	public static string? VersionLabel(string? version) {
		if (string.IsNullOrWhiteSpace(version))
			return null;
		var v = version.Trim().TrimEnd('/');
		var slash = v.LastIndexOf('/');
		return slash >= 0 && slash < v.Length - 1 ? v[(slash + 1)..] : v;
	}
}
