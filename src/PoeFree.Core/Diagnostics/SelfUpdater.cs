using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PoeFree.Core.Diagnostics;

/// <summary>
/// Installs a new PoeFree release over the running one: downloads the release package from GitHub, checks its SHA-256,
/// and swaps the files of the install folder. A running executable cannot be overwritten on Windows but it can be renamed,
/// so the old files are renamed to <c>*.old</c> and removed at the next start.
/// </summary>
public static class SelfUpdater {
	const string OldSuffix = ".old";

	/// <summary>Name of the release asset for this system, e.g. <c>PoeFree-win-x64.zip</c>.</summary>
	public static string PackageName { get; } = "PoeFree-" + (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")
		+ "-" + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() + ".zip";

	static string GuiFileName => OperatingSystem.IsWindows() ? "PoeFree.exe" : "PoeFree";

	/// <summary>
	/// Folder of the running PoeFree when it is a published package (single file executables), otherwise null
	/// (development build, tests): those are never replaced.
	/// </summary>
	public static string? InstallDirectory {
		get {
			var exe = Environment.ProcessPath;
			if (exe is null)
				return null;
			var name = Path.GetFileNameWithoutExtension(exe);
			if (!name.Equals("PoeFree", StringComparison.OrdinalIgnoreCase) && !name.Equals("poefree-cli", StringComparison.OrdinalIgnoreCase))
				return null;
			var dir = Path.GetDirectoryName(exe)!;
			return File.Exists(Path.Combine(dir, "PoeFree.Core.dll")) ? null : dir;
		}
	}

	/// <summary>Full path of the app (GUI) executable inside an install folder.</summary>
	public static string GuiPath(string installDirectory) => Path.Combine(installDirectory, GuiFileName);

	/// <summary>Downloads the package of <paramref name="info"/> to a temporary file and verifies it. Returns the file path.</summary>
	public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default) {
		if (info.PackageUrl is null || !Uri.TryCreate(info.PackageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
			throw new InvalidOperationException($"Release {info.Tag} has no {PackageName} package to download.");
		var dir = Path.Combine(Path.GetTempPath(), "PoeFree-update-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		var file = Path.Combine(dir, PackageName);
		try {
			using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
			http.DefaultRequestHeaders.UserAgent.ParseAdd("PoeFree/" + UpdateChecker.CurrentVersion);
			using var res = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
			res.EnsureSuccessStatusCode();
			var total = res.Content.Headers.ContentLength ?? info.PackageSize;
			using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			await using (var input = await res.Content.ReadAsStreamAsync(ct))
			await using (var output = File.Create(file)) {
				var buffer = new byte[1 << 17];
				long done = 0;
				int n;
				while ((n = await input.ReadAsync(buffer, ct)) > 0) {
					await output.WriteAsync(buffer.AsMemory(0, n), ct);
					sha.AppendData(buffer, 0, n);
					done += n;
					if (total > 0)
						progress?.Report(Math.Min(1.0, (double)done / total));
				}
			}
			var actual = Convert.ToHexString(sha.GetHashAndReset());
			if (info.PackageSha256 is { Length: > 0 } expected && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
				throw new InvalidDataException("The downloaded update is corrupted (SHA-256 mismatch). Nothing was changed.");
			return file;
		} catch {
			TryDeleteDirectory(dir);
			throw;
		}
	}

	/// <summary>
	/// Replaces the files of <paramref name="installDirectory"/> with the ones in the package. Either every file is replaced or none.
	/// Returns the names of the installed files.
	/// </summary>
	public static IReadOnlyList<string> InstallPackage(string zipPath, string installDirectory) {
		installDirectory = Path.GetFullPath(installDirectory);
		EnsureWritable(installDirectory);
		var staging = Path.Combine(Path.GetTempPath(), "PoeFree-update-" + Guid.NewGuid().ToString("N"));
		try {
			ZipFile.ExtractToDirectory(zipPath, staging);
			var root = staging;
			// A package made with a top level folder is accepted too.
			if (Directory.GetFiles(root).Length == 0 && Directory.GetDirectories(root) is [var only])
				root = only;
			var files = Directory.GetFiles(root);
			var gui = files.FirstOrDefault(f => Path.GetFileName(f).Equals(GuiFileName, StringComparison.OrdinalIgnoreCase));
			if (gui is null || new FileInfo(gui).Length < MinimumGuiSize)
				throw new InvalidDataException($"The update package does not contain {GuiFileName}. Nothing was changed.");

			var replaced = new List<(string Target, string? Backup)>();
			try {
				foreach (var source in files) {
					var target = Path.Combine(installDirectory, Path.GetFileName(source));
					string? backup = null;
					if (File.Exists(target)) {
						backup = FreeBackupName(target);
						File.Move(target, backup);
					}
					replaced.Add((target, backup));
					File.Move(source, target);
				}
			} catch {
				foreach (var (target, backup) in Enumerable.Reverse(replaced)) {
					try {
						if (File.Exists(target))
							File.Delete(target);
						if (backup is not null)
							File.Move(backup, target);
					} catch {
						// keep rolling back the other files
					}
				}
				throw;
			}
			return replaced.Select(r => Path.GetFileName(r.Target)).ToList();
		} finally {
			TryDeleteDirectory(staging);
		}
	}

	/// <summary>Smallest plausible size of the app executable (guards against an empty or truncated package). Lowered by tests.</summary>
	public static long MinimumGuiSize { get; set; } = 1 << 20;

	/// <summary>Downloads and installs <paramref name="info"/> into <paramref name="installDirectory"/>.</summary>
	public static async Task<IReadOnlyList<string>> UpdateAsync(UpdateInfo info, string installDirectory, IProgress<double>? progress = null, CancellationToken ct = default) {
		EnsureWritable(installDirectory);
		var zip = await DownloadAsync(info, progress, ct);
		try {
			return await Task.Run(() => InstallPackage(zip, installDirectory), ct);
		} finally {
			TryDeleteDirectory(Path.GetDirectoryName(zip)!);
		}
	}

	/// <summary>Removes the files left behind by a previous update. Safe to call at every start.</summary>
	public static int CleanupOldFiles(string? installDirectory = null) {
		installDirectory ??= InstallDirectory;
		if (installDirectory is null || !Directory.Exists(installDirectory))
			return 0;
		var removed = 0;
		try {
			foreach (var f in Directory.GetFiles(installDirectory, "*" + OldSuffix + "*")) {
				var name = Path.GetFileName(f);
				var cut = name.LastIndexOf(OldSuffix, StringComparison.OrdinalIgnoreCase);
				// Only "<file>.old" and "<file>.old<N>", for the files PoeFree ships.
				if (cut <= 0 || !name[(cut + OldSuffix.Length)..].All(char.IsAsciiDigit) || !IsPackageFile(name[..cut]))
					continue;
				try {
					File.Delete(f);
					removed++;
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					// the previous version is still closing: next start
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// nothing to clean
		}
		return removed;
	}

	static bool IsPackageFile(string name) =>
		Path.GetFileNameWithoutExtension(name).ToLowerInvariant() is "poefree" or "poefree-cli" or "license" or "readme";

	static string FreeBackupName(string target) {
		for (var i = 0; ; i++) {
			var candidate = target + OldSuffix + (i == 0 ? "" : i.ToString());
			try {
				if (File.Exists(candidate))
					File.Delete(candidate);
				return candidate;
			} catch (Exception e) when (i < 20 && e is IOException or UnauthorizedAccessException) {
				// still in use by a previous version that has not closed yet: use another name
			}
		}
	}

	static void EnsureWritable(string directory) {
		var probe = Path.Combine(directory, ".poefree-write-test-" + Guid.NewGuid().ToString("N"));
		try {
			File.WriteAllBytes(probe, []);
			File.Delete(probe);
		} catch (Exception e) when (e is UnauthorizedAccessException or IOException) {
			throw new UnauthorizedAccessException(
				$"PoeFree cannot write to its own folder ({directory}). Move PoeFree to a folder you own, or download the new version manually.", e);
		}
	}

	static void TryDeleteDirectory(string dir) {
		try {
			if (Directory.Exists(dir))
				Directory.Delete(dir, true);
		} catch {
			// temporary files: the system cleans them
		}
	}
}
