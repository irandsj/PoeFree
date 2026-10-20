using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoeFree.Core.Game;

namespace PoeFree.Core.Archive;

public sealed class BackupState {
	public string ArchivePath { get; set; } = "";
	public string VanillaSha256 { get; set; } = "";
	public string? PatchedSha256 { get; set; }
	public DateTime CreatedUtc { get; set; }
	public DateTime? PatchedUtc { get; set; }
}

[JsonSerializable(typeof(BackupState))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class BackupJsonContext : JsonSerializerContext;

public enum RestoreOutcome {
	AlreadyOriginal,
	RestoredFromBackup,
	RestoredFromOfficialServer,
}

/// <summary>
/// Backup and restore of the original game index.
/// <para>
/// Before the first patch PoeFree stores a copy of the vanilla <c>_.index.bin</c> in %APPDATA%\PoeFree\backups.
/// After patching it remembers the hash of the index it wrote. On restore, if the index on disk is still the one PoeFree wrote,
/// the backup is put back. Otherwise (the game was patched by another tool, or the backup is gone) the official index is downloaded
/// from Grinding Gear Games' patch CDN, after checking that every bundle it references exists locally (same game version).
/// </para>
/// </summary>
public static class RestoreService {
	static string BackupDir(string archivePath) {
		var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(archivePath).ToLowerInvariant())))[..12];
		return Path.Combine(AppPaths.BackupsDirectory, key);
	}

	static string IndexBackupPath(string archivePath) => Path.Combine(BackupDir(archivePath), "_.index.bin");
	static string StatePath(string archivePath) => Path.Combine(BackupDir(archivePath), "state.json");

	public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

	public static BackupState? LoadState(string archivePath) {
		var p = StatePath(archivePath);
		if (!File.Exists(p))
			return null;
		try {
			return JsonSerializer.Deserialize(File.ReadAllText(p), BackupJsonContext.Default.BackupState);
		} catch {
			return null;
		}
	}

	static void SaveState(string archivePath, BackupState state) {
		Directory.CreateDirectory(BackupDir(archivePath));
		File.WriteAllText(StatePath(archivePath), JsonSerializer.Serialize(state, BackupJsonContext.Default.BackupState));
	}

	/// <summary>Stores <paramref name="vanillaIndex"/> as the backup for this install.</summary>
	public static void BackupVanilla(string archivePath, byte[] vanillaIndex) {
		Directory.CreateDirectory(BackupDir(archivePath));
		File.WriteAllBytes(IndexBackupPath(archivePath), vanillaIndex);
		SaveState(archivePath, new BackupState {
			ArchivePath = Path.GetFullPath(archivePath),
			VanillaSha256 = Sha256(vanillaIndex),
			CreatedUtc = DateTime.UtcNow,
		});
	}

	/// <summary>Remembers the index PoeFree just wrote, so a later restore knows the backup still matches.</summary>
	public static void RecordPatched(string archivePath) {
		var state = LoadState(archivePath) ?? throw new InvalidOperationException("No backup exists for this install.");
		state.PatchedSha256 = Sha256(GameArchive.ReadRawIndex(archivePath));
		state.PatchedUtc = DateTime.UtcNow;
		SaveState(archivePath, state);
	}

	/// <summary>Lists the custom (mod) bundles referenced by a raw index.</summary>
	public static List<string> CustomBundlesIn(byte[] rawIndex, out List<string> allBundles) {
		OodleResolver.EnsureLoaded();
		using var factory = new SteamBundleFactory(AppContext.BaseDirectory); // never used: only the bundle list is read
		using var index = new LibBundle3.Index(new MemoryStream(rawIndex, false), false, false, factory);
		allBundles = index.Bundles.ToArray().Select(b => b.Path).ToList();
		return allBundles.Where(CustomBundles.IsCustom).ToList();
	}

	/// <summary>True if the index on disk has no mod bundles.</summary>
	public static bool IsOriginal(string archivePath) => CustomBundlesIn(GameArchive.ReadRawIndex(archivePath), out _).Count == 0;

	/// <summary>Makes sure a backup of the vanilla index exists before patching (only if the current index is vanilla).</summary>
	public static void EnsureBackup(string archivePath) {
		var raw = GameArchive.ReadRawIndex(archivePath);
		if (CustomBundlesIn(raw, out _).Count != 0)
			throw new InvalidOperationException("The game index is already modified; restore it before creating a backup.");
		var state = LoadState(archivePath);
		if (state is not null && state.VanillaSha256 == Sha256(raw) && File.Exists(IndexBackupPath(archivePath)))
			return;
		BackupVanilla(archivePath, raw);
	}

	/// <summary>Restores the original game files. The archive must not be open.</summary>
	public static async Task<RestoreOutcome> RestoreAsync(string archivePath, GameVersion version, IProgress<string>? log = null, CancellationToken ct = default) {
		var raw = GameArchive.ReadRawIndex(archivePath);
		var custom = CustomBundlesIn(raw, out _);
		if (custom.Count == 0) {
			log?.Report("The game index is already original.");
			var removedLeftovers = GameArchive.DeleteCustomBundles(archivePath);
			if (removedLeftovers > 0)
				log?.Report($"Removed {removedLeftovers} leftover mod bundle(s).");
			return RestoreOutcome.AlreadyOriginal;
		}
		log?.Report($"Modified index detected ({custom.Count} mod bundle(s): {string.Join(", ", custom.Take(5))}).");

		var state = LoadState(archivePath);
		var backup = IndexBackupPath(archivePath);
		RestoreOutcome outcome;
		if (state?.PatchedSha256 == Sha256(raw) && File.Exists(backup)) {
			log?.Report("Restoring the index backup made before patching...");
			GameArchive.WriteRawIndex(archivePath, File.ReadAllBytes(backup));
			outcome = RestoreOutcome.RestoredFromBackup;
		} else {
			log?.Report("No matching backup (the files were modified by another tool or the game updated). Downloading the official index...");
			var official = await DownloadOfficialIndexAsync(version, log, ct);
			CustomBundlesIn(official, out var bundles);
			var missing = GameArchive.FindMissingBundles(archivePath, bundles);
			if (missing.Count > 0)
				throw new InvalidOperationException(
					$"The official index references {missing.Count} bundle(s) that are not installed (e.g. {missing[0]}). " +
					"Your game version differs from the server: update the game with its launcher (or use PackCheck/Verify files), then try again.");
			GameArchive.WriteRawIndex(archivePath, official);
			outcome = RestoreOutcome.RestoredFromOfficialServer;
		}

		var removed = GameArchive.DeleteCustomBundles(archivePath);
		log?.Report($"Index restored. Removed {removed} mod bundle file(s).");
		if (state is not null) {
			state.PatchedSha256 = null;
			state.PatchedUtc = null;
			SaveState(archivePath, state);
		}
		return outcome;
	}

	public static async Task<string> GetPatchCdnUrlAsync(GameVersion version) {
		var endpoint = version == GameVersion.Poe2 ? LibGGPK3.PatchClient.ServerEndPoints.US2 : LibGGPK3.PatchClient.ServerEndPoints.US;
		return await LibGGPK3.PatchClient.GetPatchCdnUrlAsync(endpoint);
	}

	/// <summary>Downloads Bundles2/_.index.bin of the current game version from the official patch CDN.</summary>
	public static async Task<byte[]> DownloadOfficialIndexAsync(GameVersion version, IProgress<string>? log = null, CancellationToken ct = default) {
		var cdn = await GetPatchCdnUrlAsync(version);
		log?.Report("Patch CDN: " + cdn);
		using var http = new HttpClient { BaseAddress = new Uri(cdn) };
		return await http.GetByteArrayAsync("Bundles2/_.index.bin", ct);
	}
}
