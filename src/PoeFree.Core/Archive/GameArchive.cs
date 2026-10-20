using System.Reflection;
using LibBundle3;
using LibBundledGGPK3;
using PoeFree.Core.Game;
using PoeFree.Core.Text;
using FileRecord = LibBundle3.Records.FileRecord;
using BundleRecord = LibBundle3.Records.BundleRecord;
using Index = LibBundle3.Index;

namespace PoeFree.Core.Archive;

/// <summary>
/// Unified access to the game files, for both layouts:
/// <list type="bullet">
/// <item>Standalone client: everything lives inside <c>Content.ggpk</c> (bundles are stored in its Bundles2 directory).</item>
/// <item>Steam/Epic: loose <c>Bundles2/*.bundle.bin</c> files next to <c>Bundles2/_.index.bin</c>.</item>
/// </list>
/// Writes never touch the original bundles: modified files are appended to new bundles (<c>LibGGPK3/N.bundle.bin</c>)
/// and the index is redirected to them. Restoring therefore only needs the original <c>_.index.bin</c>.
/// </summary>
public sealed class GameArchive : IDisposable {
	public string ArchivePath { get; }
	public bool IsGgpk { get; }
	public bool ReadOnly { get; }
	public GameVersion Version { get; }
	public Index Index { get; }
	/// <summary>Number of files whose path could not be resolved (usually files added by other tools).</summary>
	public int UnnamedFiles { get; }

	readonly BundledGGPK? ggpk;
	readonly SteamBundleFactory? factory;
	string[]? sortedPaths;
	readonly HashSet<string> addedPaths = new(StringComparer.Ordinal);

	GameArchive(string path, bool readOnly, string? cdnUrl) {
		ArchivePath = Path.GetFullPath(path);
		ReadOnly = readOnly;
		IsGgpk = path.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase);
		OodleResolver.EnsureLoaded();

		if (IsGgpk) {
			var fs = readOnly
				? new FileStream(ArchivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
				: new FileStream(ArchivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
			try {
				ggpk = new BundledGGPK(fs, false, false);
			} catch {
				fs.Dispose();
				throw;
			}
			Index = ggpk.Index;
			Version = GameDetector.GuessVersion(ArchivePath, ggpk.Version);
		} else {
			factory = new SteamBundleFactory(Path.GetDirectoryName(ArchivePath)!, cdnUrl);
			var fs = readOnly
				? new FileStream(ArchivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
				: new FileStream(ArchivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
			try {
				Index = new Index(fs, false, false, factory);
			} catch {
				fs.Dispose();
				factory.Dispose();
				throw;
			}
			Version = GameDetector.GuessVersion(ArchivePath);
		}
		UnnamedFiles = Index.ParsePaths();
	}

	/// <summary>Opens a Content.ggpk or a Bundles2/_.index.bin.</summary>
	/// <param name="readOnly">Read only mode works even while the game is running.</param>
	/// <param name="patchCdnUrl">Steam layout only: download missing bundles from this CDN (sandbox/testing).</param>
	public static GameArchive Open(string path, bool readOnly = true, string? patchCdnUrl = null) {
		if (!File.Exists(path))
			throw new FileNotFoundException("Game archive not found", path);
		try {
			return new GameArchive(path, readOnly, patchCdnUrl);
		} catch (IOException e) when (!readOnly && IsSharingViolation(e)) {
			throw new IOException("The game files are in use. Close Path of Exile (and its launcher) and try again.", e);
		}
	}

	static bool IsSharingViolation(IOException e) => (e.HResult & 0xFFFF) is 32 or 33;

	/// <summary>True if the index references bundles added by a modding tool (PoeFree, PoeSmoother, VisualGGPK...).</summary>
	public bool IsModified => CustomBundleNames().Any();

	public IEnumerable<string> CustomBundleNames() {
		foreach (var b in Index.Bundles.Span.ToArray())
			if (CustomBundles.IsCustom(b.Path))
				yield return b.Path;
	}

	/// <summary>All known file paths, sorted ordinally (paths in the index are lower case).</summary>
	public IReadOnlyList<string> Paths {
		get {
			if (sortedPaths is null) {
				var list = new List<string>(Index.Files.Count);
				foreach (var f in Index.Files.Values)
					if (f.Path is not null)
						list.Add(f.Path);
				list.Sort(StringComparer.Ordinal);
				sortedPaths = [.. list];
			}
			return sortedPaths;
		}
	}

	/// <summary>Enumerates paths that start with <paramref name="prefix"/> (case-insensitive, '/' separated).</summary>
	public IEnumerable<string> PathsUnder(string prefix) {
		prefix = Normalize(prefix);
		var paths = (string[])Paths;
		var i = Array.BinarySearch(paths, prefix, StringComparer.Ordinal);
		if (i < 0)
			i = ~i;
		for (; i < paths.Length && paths[i].StartsWith(prefix, StringComparison.Ordinal); i++)
			yield return paths[i];
	}

	public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

	public bool Exists(string path) => Index.TryGetFile(Normalize(path), out _);

	public FileRecord? Find(string path) => Index.TryGetFile(Normalize(path), out var f) ? f : null;

	public byte[]? ReadBytes(string path) => Find(path)?.Read().ToArray();

	public string? ReadText(string path) => ReadText(path, out _);

	public string? ReadText(string path, out TextEncodingKind kind) {
		var bytes = Find(path)?.Read();
		if (bytes is null) {
			kind = TextEncodingKind.Utf16LeBom;
			return null;
		}
		return PoeText.Decode(bytes.Value.Span, out kind);
	}

	/// <summary>Reads many files efficiently (grouped by bundle). Missing paths are skipped.</summary>
	public int ReadMany(IEnumerable<string> paths, Action<string, ReadOnlyMemory<byte>> callback, CancellationToken ct = default) {
		var records = new List<FileRecord>();
		foreach (var p in paths)
			if (Index.TryGetFile(Normalize(p), out var f))
				records.Add(f);
		return Index.Extract(records, (record, content) => {
			if (content is { } c)
				callback(record.Path, c);
			return ct.IsCancellationRequested;
		});
	}

	/// <summary>Replaces (or adds) a file. Call <see cref="Save"/> afterwards.</summary>
	public void Write(string path, ReadOnlySpan<byte> content) {
		EnsureWritable();
		path = Normalize(path);
		if (Index.TryGetFile(path, out var f))
			f.Write(content);
		else
			AddFile(path, content);
	}

	public void WriteText(string path, string text, TextEncodingKind kind) => Write(path, PoeText.Encode(text, kind));

	static readonly FieldInfo FilesField = typeof(Index).GetField("_Files", BindingFlags.NonPublic | BindingFlags.Instance)
		?? throw new MissingFieldException("LibBundle3.Index._Files not found; LibGGPK3 version is incompatible.");

	/// <summary>
	/// Adds a brand new file to the index. The game resolves files by the hash of their path, so new files work even though
	/// they are not listed in the index directory tables (they just will not show up in tree views of other tools).
	/// </summary>
	void AddFile(string path, ReadOnlySpan<byte> content) {
		var files = (Dictionary<ulong, FileRecord>)FilesField.GetValue(Index)!;
		var hash = Index.NameHash(path);
		var record = new AddedFileRecord(hash, Index.Bundles.Span[0], path);
		files.Add(hash, record);
		record.Write(content);
		addedPaths.Add(path);
		sortedPaths = null;
	}

	public IReadOnlyCollection<string> AddedPaths => addedPaths;

	public void Save() {
		EnsureWritable();
		Index.Save();
	}

	void EnsureWritable() {
		if (ReadOnly)
			throw new InvalidOperationException("The archive was opened read-only.");
	}

	public void Dispose() {
		if (ggpk is not null)
			ggpk.Dispose();
		else
			Index.Dispose();
		factory?.Dispose();
	}

	sealed class AddedFileRecord : FileRecord {
		public AddedFileRecord(ulong hash, BundleRecord bundle, string path) : base(hash, bundle, 0, 0) {
			Path = path;
		}
	}

	#region Raw index access (used by backup/restore)

	/// <summary>Reads the raw (compressed) bytes of <c>Bundles2/_.index.bin</c> without parsing it.</summary>
	public static byte[] ReadRawIndex(string archivePath) {
		if (!archivePath.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase)) {
			using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			var data = new byte[fs.Length];
			fs.ReadExactly(data);
			return data;
		}
		using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var g = new LibGGPK3.GGPK(stream, false);
		return GetGgpkIndexRecord(g).Read();
	}

	/// <summary>Overwrites <c>Bundles2/_.index.bin</c> with <paramref name="data"/>. The archive must not be open elsewhere.</summary>
	public static void WriteRawIndex(string archivePath, ReadOnlySpan<byte> data) {
		if (!archivePath.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase)) {
			var tmp = archivePath + ".poefree.tmp";
			File.WriteAllBytes(tmp, data.ToArray());
			File.Move(tmp, archivePath, true);
			return;
		}
		using var g = new LibGGPK3.GGPK(archivePath);
		GetGgpkIndexRecord(g).Write(data);
	}

	/// <summary>
	/// Deletes bundles created by modding tools. Only call this right after restoring a vanilla index
	/// (so nothing references them any more). Returns the number of bundles removed.
	/// </summary>
	public static int DeleteCustomBundles(string archivePath) {
		var count = 0;
		if (!archivePath.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase)) {
			var bundles2 = Path.GetDirectoryName(Path.GetFullPath(archivePath))!;
			var libDir = Path.Combine(bundles2, "LibGGPK3");
			if (Directory.Exists(libDir))
				foreach (var f in Directory.GetFiles(libDir, "*.bundle.bin")) {
					File.Delete(f);
					count++;
				}
			foreach (var f in Directory.GetFiles(bundles2, "*.bundle.bin"))
				if (CustomBundles.IsCustom(Path.GetFileName(f))) {
					File.Delete(f);
					count++;
				}
			return count;
		}
		using var g = new LibGGPK3.GGPK(archivePath);
		if (g.Root["Bundles2"] is not LibGGPK3.Records.DirectoryRecord dir)
			return 0;
		var toRemove = new List<LibGGPK3.Records.TreeNode>();
		foreach (var node in dir) {
			if (node is LibGGPK3.Records.DirectoryRecord sub && sub.Name.Equals("LibGGPK3", StringComparison.OrdinalIgnoreCase))
				toRemove.Add(sub);
			else if (node is LibGGPK3.Records.FileRecord file && CustomBundles.IsCustom(file.Name))
				toRemove.Add(file);
		}
		foreach (var node in toRemove) {
			if (node is LibGGPK3.Records.DirectoryRecord d)
				count += d.Count;
			else
				count++;
			node.Remove();
		}
		return count;
	}

	static LibGGPK3.Records.FileRecord GetGgpkIndexRecord(LibGGPK3.GGPK g) {
		if (g.Root["Bundles2"] is not LibGGPK3.Records.DirectoryRecord dir)
			throw new InvalidDataException("Could not find Bundles2 in the GGPK.");
		if (dir["_.index.bin"] is not LibGGPK3.Records.FileRecord rec)
			throw new InvalidDataException("Could not find Bundles2/_.index.bin in the GGPK.");
		return rec;
	}

	/// <summary>Checks that every non custom bundle referenced by an index exists in the install (to validate a downloaded index).</summary>
	public static List<string> FindMissingBundles(string archivePath, IEnumerable<string> bundlePaths) {
		var missing = new List<string>();
		if (!archivePath.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase)) {
			var bundles2 = Path.GetDirectoryName(Path.GetFullPath(archivePath))!;
			foreach (var b in bundlePaths)
				if (!CustomBundles.IsCustom(b) && !File.Exists(Path.Combine(bundles2, b)))
					missing.Add(b);
			return missing;
		}
		using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var g = new LibGGPK3.GGPK(stream, false);
		if (g.Root["Bundles2"] is not LibGGPK3.Records.DirectoryRecord dir)
			throw new InvalidDataException("Could not find Bundles2 in the GGPK.");
		foreach (var b in bundlePaths)
			if (!CustomBundles.IsCustom(b) && !dir.TryFindNode(b, out _))
				missing.Add(b);
		return missing;
	}

	#endregion
}
