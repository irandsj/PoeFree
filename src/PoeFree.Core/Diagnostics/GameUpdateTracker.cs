using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoeFree.Core.Archive;
using PoeFree.Core.Game;

namespace PoeFree.Core.Diagnostics;

/// <summary>The last original game index PoeFree saw for an install (stored next to a copy of that index).</summary>
public sealed class UpdateBaseline {
	public string ArchivePath { get; set; } = "";
	public string IndexSha256 { get; set; } = "";
	/// <summary>Game version (patch CDN url) read from the client log when this index was seen, if known.</summary>
	public string? Version { get; set; }
	/// <summary>Version of the index this one replaced: the client log may still show it until the game runs once.</summary>
	public string? PreviousVersion { get; set; }
	public DateTime SeenUtc { get; set; }
	/// <summary>Report of the last game update that the user has not dismissed yet.</summary>
	public string? PendingReportFile { get; set; }
	public string? PendingOldVersion { get; set; }
	public string? PendingNewVersion { get; set; }
	public DateTime? PendingDetectedUtc { get; set; }
	/// <summary>A game update removed the options PoeFree had applied and they were not applied again yet.</summary>
	public bool ReapplyPending { get; set; }
}

public enum WatchedFiles {
	/// <summary>Not read yet, or the index could not be read (e.g. the game is writing it).</summary>
	Unknown,
	Original,
	PatchedByPoeFree,
	ModifiedByOtherTool,
}

/// <summary>A game update: the original index changed from <see cref="OldIndex"/> to <see cref="NewIndex"/>.</summary>
public sealed record GameUpdate(GameInstall Install, string OldSha256, string NewSha256, string? OldVersion, string? NewVersion,
	byte[] OldIndex, byte[] NewIndex, DateTime DetectedUtc);

/// <summary>
/// Notices that the game updated itself, by looking at the game files only: the original <c>_.index.bin</c> is compared with the
/// last one PoeFree saw. Nothing is started, closed or written in the game folder; the game process is never touched.
/// <para>Call <see cref="Poll"/> every few seconds. It is cheap unless the archive file changed.</para>
/// </summary>
public sealed class GameUpdateTracker(GameInstall install, IGameProbe probe) {
	/// <summary>The index is hashed again at least every this many polls, even if the file looks untouched.</summary>
	public int FullCheckEvery { get; set; } = 12;

	public GameInstall Install => install;
	public WatchedFiles Files { get; private set; }
	/// <summary>The index is original although PoeFree had patched this install: an update (or a file repair) removed the options.</summary>
	public bool OptionsWereRemoved { get; private set; }

	/// <summary>Last time the archive file was written, as far as the file system tells.</summary>
	public DateTime LastWriteUtc => lastStamp.Written;

	(long Length, DateTime Written) lastStamp;
	string? lastSha;
	int lastCustomBundles;
	string? candidate;
	int polls;

	static string Key(string archivePath) =>
		Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(archivePath).ToLowerInvariant())))[..12];

	/// <summary>Folder with the baseline and the update reports of an install.</summary>
	public static string FolderFor(string archivePath) => Path.Combine(AppPaths.DataDirectory, "updates", Key(archivePath));
	static string BaselineFile(string archivePath) => Path.Combine(FolderFor(archivePath), "baseline.json");
	static string BaselineIndexFile(string archivePath) => Path.Combine(FolderFor(archivePath), "baseline.index.bin");

	public static UpdateBaseline? LoadBaseline(string archivePath) {
		try {
			var file = BaselineFile(archivePath);
			return File.Exists(file) && File.Exists(BaselineIndexFile(archivePath))
				? JsonSerializer.Deserialize(File.ReadAllText(file), UpdateJsonContext.Default.UpdateBaseline)
				: null;
		} catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
			return null;
		}
	}

	static void SaveBaseline(UpdateBaseline baseline, byte[]? index = null) {
		Directory.CreateDirectory(FolderFor(baseline.ArchivePath));
		if (index is not null)
			File.WriteAllBytes(BaselineIndexFile(baseline.ArchivePath), index);
		File.WriteAllText(BaselineFile(baseline.ArchivePath), JsonSerializer.Serialize(baseline, UpdateJsonContext.Default.UpdateBaseline));
	}

	(long, DateTime) Stamp() {
		try {
			var info = new FileInfo(install.ArchivePath);
			return (info.Length, info.LastWriteTimeUtc);
		} catch (IOException) {
			return default;
		}
	}

	/// <summary>
	/// Looks at the game files. Returns a <see cref="GameUpdate"/> once the original index differs from the stored baseline on two
	/// polls in a row (so an index that is still being written is never reported). Call <see cref="Accept"/> afterwards.
	/// </summary>
	/// <param name="force">Hash the index even if the archive file looks untouched.</param>
	public GameUpdate? Poll(bool force = false) {
		var stamp = Stamp();
		var full = force || candidate is not null || lastSha is null || stamp != lastStamp || ++polls % FullCheckEvery == 0;
		if (!full)
			return null;

		byte[] raw;
		string sha;
		try {
			raw = GameArchive.ReadRawIndex(install.ArchivePath);
			sha = RestoreService.Sha256(raw);
			if (sha != lastSha) {
				lastCustomBundles = RestoreService.CustomBundlesIn(raw, out _).Count;
				lastSha = sha;
			}
			lastStamp = stamp;
		} catch (Exception e) when (e is not OutOfMemoryException) {
			// Missing, locked or half written (the game is patching): look again on the next poll.
			Files = WatchedFiles.Unknown;
			lastSha = null;
			candidate = null;
			return null;
		}

		var state = RestoreService.LoadState(install.ArchivePath);
		string originalSha;
		Func<byte[]?> original;
		if (lastCustomBundles == 0) {
			Files = WatchedFiles.Original;
			OptionsWereRemoved = state?.PatchedSha256 is not null;
			originalSha = sha;
			original = () => raw;
		} else if (state?.PatchedSha256 == sha && !string.IsNullOrEmpty(state.VanillaSha256)) {
			// PoeFree's own patch: the original index of this game version is the backup made before applying.
			Files = WatchedFiles.PatchedByPoeFree;
			OptionsWereRemoved = false;
			originalSha = state.VanillaSha256;
			original = () => RestoreService.ReadBackupIndex(install.ArchivePath) is { } b && RestoreService.Sha256(b) == originalSha ? b : null;
		} else {
			// Changed by another tool: the original index is unknown, nothing can be compared.
			Files = WatchedFiles.ModifiedByOtherTool;
			OptionsWereRemoved = false;
			candidate = null;
			return null;
		}

		var baseline = LoadBaseline(install.ArchivePath);
		if (baseline is null) {
			if (original() is { } first)
				SaveBaseline(new UpdateBaseline {
					ArchivePath = Path.GetFullPath(install.ArchivePath),
					IndexSha256 = originalSha,
					Version = probe.InstalledVersion(install),
					SeenUtc = DateTime.UtcNow,
				}, first);
			candidate = null;
			return null;
		}
		if (baseline.IndexSha256 == originalSha) {
			candidate = null;
			if (baseline.Version is null && probe.InstalledVersion(install) is { } known
				&& (baseline.PreviousVersion is null || !GameProbe.SameVersion(known, baseline.PreviousVersion))) {
				baseline.Version = known;
				SaveBaseline(baseline);
			}
			return null;
		}
		if (candidate != originalSha) {
			candidate = originalSha; // confirm on the next poll
			return null;
		}
		candidate = null;
		byte[] oldIndex;
		try {
			oldIndex = File.ReadAllBytes(BaselineIndexFile(install.ArchivePath));
		} catch (IOException) {
			return null;
		}
		if (original() is not { } newIndex)
			return null;
		var newVersion = probe.InstalledVersion(install);
		// The client log only shows the new version after the game ran once with it (Steam/Epic update the files by themselves).
		if (newVersion is not null && baseline.Version is not null && GameProbe.SameVersion(newVersion, baseline.Version))
			newVersion = null;
		return new GameUpdate(install, baseline.IndexSha256, originalSha, baseline.Version, newVersion, oldIndex, newIndex, DateTime.UtcNow);
	}

	/// <summary>Makes the new index the baseline and remembers the report written for this update.</summary>
	/// <param name="reapplyPending">The update removed options that PoeFree had applied.</param>
	public static void Accept(GameUpdate update, string? reportFile, bool reapplyPending = false) =>
		SaveBaseline(new UpdateBaseline {
			ArchivePath = Path.GetFullPath(update.Install.ArchivePath),
			IndexSha256 = update.NewSha256,
			Version = update.NewVersion,
			PreviousVersion = update.OldVersion,
			SeenUtc = update.DetectedUtc,
			PendingReportFile = reportFile,
			PendingOldVersion = update.OldVersion,
			PendingNewVersion = update.NewVersion,
			PendingDetectedUtc = reportFile is null ? null : update.DetectedUtc,
			ReapplyPending = reapplyPending,
		}, update.NewIndex);

	public static void SetReapplyPending(string archivePath, bool value) {
		if (LoadBaseline(archivePath) is not { } baseline || baseline.ReapplyPending == value)
			return;
		baseline.ReapplyPending = value;
		SaveBaseline(baseline);
	}

	/// <summary>Forgets the pending report (the file stays on disk).</summary>
	public static void DismissReport(string archivePath) {
		if (LoadBaseline(archivePath) is not { PendingReportFile: not null } baseline)
			return;
		baseline.PendingReportFile = null;
		baseline.PendingOldVersion = baseline.PendingNewVersion = null;
		baseline.PendingDetectedUtc = null;
		SaveBaseline(baseline);
	}
}

[JsonSerializable(typeof(UpdateBaseline))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class UpdateJsonContext : JsonSerializerContext;
