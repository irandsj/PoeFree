using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PoeFree.Core.Game;

namespace PoeFree.Core.Archive;

/// <summary>
/// Path of Exile bundles are compressed with RAD Oodle. The Oodle runtime (oo2core) is proprietary, so PoeFree
/// does not ship it in the repository. Instead it looks for a copy at runtime:
/// <list type="number">
/// <item>a path chosen by the user,</item>
/// <item>next to PoeFree.exe or in %APPDATA%\PoeFree,</item>
/// <item>any <c>oo2core_N_win64.dll</c> shipped by another installed game (many Unreal Engine games ship one),</item>
/// <item>optionally downloaded from the LibGGPK3 GitHub release.</item>
/// </list>
/// </summary>
public static partial class OodleResolver {
	public const string LibraryName = "oo2core";

	/// <summary>Release asset of LibGGPK3 that contains a Windows x64 oo2core.dll.</summary>
	public const string DownloadUrl = "https://github.com/aianlinb/LibGGPK3/releases/download/v2.7.5/VPatchGGPK3_win-x64.zip";

	static readonly object Sync = new();
	static bool installed;
	static nint handle;

	/// <summary>Path of the Oodle library in use, once loaded.</summary>
	public static string? LoadedPath { get; private set; }

	/// <summary>User selected library path (from settings). Checked first.</summary>
	public static string? PreferredPath { get; set; }

	public static string UserDataDirectory => AppPaths.DataDirectory;

	static string LocalFileName => OperatingSystem.IsWindows() ? "oo2core.dll"
		: OperatingSystem.IsMacOS() ? "liboo2core.dylib" : "liboo2core.so";

	/// <summary>Installs the native library resolver for LibBundle3. Safe to call more than once.</summary>
	public static void Install() {
		lock (Sync) {
			if (installed)
				return;
			NativeLibrary.SetDllImportResolver(typeof(LibBundle3.Oodle).Assembly, Resolve);
			installed = true;
		}
	}

	static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) {
		if (!name.Equals(LibraryName, StringComparison.OrdinalIgnoreCase))
			return 0;
		lock (Sync) {
			if (handle != 0)
				return handle;
			foreach (var candidate in Candidates())
				if (NativeLibrary.TryLoad(candidate, out var h)) {
					if (!HasRequiredExports(h)) {
						NativeLibrary.Free(h);
						continue;
					}
					handle = h;
					LoadedPath = candidate;
					return h;
				}
		}
		return 0; // let the runtime try its default probing, which throws DllNotFoundException
	}

	static readonly string[] RequiredExports = [
		"OodleLZ_Compress", "OodleLZ_Decompress", "OodleLZ_GetCompressedBufferSizeNeeded",
		"OodleLZ_GetCompressScratchMemBound", "OodleLZDecoder_MemorySizeNeeded",
	];

	/// <summary>Older Oodle versions (shipped by some games) miss functions LibBundle3 needs.</summary>
	static bool HasRequiredExports(nint library) => RequiredExports.All(e => NativeLibrary.TryGetExport(library, e, out _));

	/// <summary>Whether a usable Oodle library can be found without downloading.</summary>
	public static bool IsAvailable() => handle != 0 || Resolve(LibraryName, typeof(OodleResolver).Assembly, null) != 0;

	/// <summary>Loads Oodle now and initializes LibBundle3. Throws <see cref="DllNotFoundException"/> with a helpful message when missing.</summary>
	public static void EnsureLoaded() {
		Install();
		try {
			LibBundle3.Oodle.Initialize(new LibBundle3.Oodle.Settings());
		} catch (DllNotFoundException e) {
			throw new DllNotFoundException(
				"Oodle (oo2core.dll) was not found. Use 'Locate oo2core' / 'Download oo2core' in PoeFree, " +
				$"or copy oo2core.dll next to PoeFree.exe or into {UserDataDirectory}.", e);
		}
	}

	public static IEnumerable<string> Candidates() {
		if (!string.IsNullOrWhiteSpace(PreferredPath) && File.Exists(PreferredPath))
			yield return PreferredPath;
		var local = Path.Combine(AppContext.BaseDirectory, LocalFileName);
		if (File.Exists(local))
			yield return local;
		var data = Path.Combine(UserDataDirectory, LocalFileName);
		if (File.Exists(data))
			yield return data;
		if (!OperatingSystem.IsWindows())
			yield break;
		foreach (var dll in FindInstalledCopies())
			yield return dll;
	}

	/// <summary>Searches installed Steam/Epic games for <c>oo2core_N_win64.dll</c>, newest version first.</summary>
	public static IEnumerable<string> FindInstalledCopies() {
		var found = new List<(int Version, string Path)>();
		foreach (var root in GameDetector.SteamCommonFolders().Concat(GameDetector.EpicGameFolders())) {
			IEnumerable<string> dirs;
			try {
				dirs = Directory.EnumerateDirectories(root);
			} catch {
				continue;
			}
			foreach (var game in dirs) {
				foreach (var dir in new[] { game, Path.Combine(game, "Engine", "Binaries", "ThirdParty", "Oodle") }) {
					try {
						if (!Directory.Exists(dir))
							continue;
						foreach (var f in Directory.EnumerateFiles(dir, "oo2core_*win64.dll")) {
							var m = VersionRegex().Match(Path.GetFileName(f));
							if (m.Success)
								found.Add((int.Parse(m.Groups[1].Value), f));
						}
					} catch {
						// ignore inaccessible folders
					}
				}
			}
		}
		// Exports are verified when loading; very old versions are skipped right away.
		return found.Where(f => f.Version >= 6).OrderByDescending(f => f.Version).Select(f => f.Path).ToList();
	}

	/// <summary>Copies a user selected Oodle library into the PoeFree data folder so it is found on next start.</summary>
	public static string ImportLibrary(string sourcePath) {
		Directory.CreateDirectory(UserDataDirectory);
		var dest = Path.Combine(UserDataDirectory, LocalFileName);
		File.Copy(sourcePath, dest, true);
		PreferredPath = dest;
		return dest;
	}

	/// <summary>Downloads oo2core.dll from the LibGGPK3 release zip into the PoeFree data folder.</summary>
	public static async Task<string> DownloadAsync(IProgress<string>? progress = null, CancellationToken ct = default) {
		if (!OperatingSystem.IsWindows())
			throw new PlatformNotSupportedException("Automatic download is only available on Windows. Copy liboo2core next to PoeFree manually.");
		Directory.CreateDirectory(UserDataDirectory);
		var dest = Path.Combine(UserDataDirectory, LocalFileName);
		progress?.Report("Downloading " + DownloadUrl);
		using var http = new HttpClient();
		http.DefaultRequestHeaders.UserAgent.ParseAdd("PoeFree");
		await using var zipStream = await http.GetStreamAsync(DownloadUrl, ct);
		using var ms = new MemoryStream();
		await zipStream.CopyToAsync(ms, ct);
		ms.Position = 0;
		using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
		var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("oo2core.dll", StringComparison.OrdinalIgnoreCase))
			?? throw new InvalidDataException("oo2core.dll was not found inside the downloaded package.");
		entry.ExtractToFile(dest, true);
		PreferredPath = dest;
		progress?.Report("Saved " + dest);
		return dest;
	}

	[GeneratedRegex(@"^oo2core_(\d+)_win64\.dll$", RegexOptions.IgnoreCase)]
	private static partial Regex VersionRegex();
}
