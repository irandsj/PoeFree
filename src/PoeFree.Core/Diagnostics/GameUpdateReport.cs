using System.Globalization;
using System.Text;
using PoeFree.Core.Archive;
using PoeFree.Core.Game;
using PoeFree.Core.Patching;
using PoeFree.Core.Text;

namespace PoeFree.Core.Diagnostics;

public sealed record ResizedFile(string Path, int OldSize, int NewSize);

/// <summary>What changed between two game indexes, judged by file path, size and position only (no file is read).</summary>
public sealed record IndexDiff(int OldFiles, int NewFiles, IReadOnlyList<string> Added, IReadOnlyList<string> Removed,
	IReadOnlyList<ResizedFile> Resized, int Moved, int OldBundles, int NewBundles) {
	public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Resized.Count == 0 && Moved == 0;

	/// <summary>Compares two raw <c>_.index.bin</c> files.</summary>
	public static IndexDiff Compare(byte[] oldIndex, byte[] newIndex) {
		var before = Read(oldIndex, out var oldBundles);
		var after = Read(newIndex, out var newBundles);
		var added = new List<string>();
		var removed = new List<string>();
		var resized = new List<ResizedFile>();
		var moved = 0;
		foreach (var (hash, n) in after) {
			if (!before.TryGetValue(hash, out var o))
				added.Add(n.Path);
			else if (o.Size != n.Size)
				resized.Add(new ResizedFile(n.Path, o.Size, n.Size));
			else if (o.Offset != n.Offset || !string.Equals(o.Bundle, n.Bundle, StringComparison.Ordinal))
				moved++;
		}
		foreach (var (hash, o) in before)
			if (!after.ContainsKey(hash))
				removed.Add(o.Path);
		added.Sort(StringComparer.Ordinal);
		removed.Sort(StringComparer.Ordinal);
		resized.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
		return new IndexDiff(before.Count, after.Count, added, removed, resized, moved, oldBundles, newBundles);
	}

	readonly record struct Entry(string Path, int Size, int Offset, string Bundle);

	static Dictionary<ulong, Entry> Read(byte[] raw, out int bundles) {
		OodleResolver.EnsureLoaded();
		using var factory = new SteamBundleFactory(AppContext.BaseDirectory); // never used: no bundle is opened
		using var index = new LibBundle3.Index(new MemoryStream(raw, false), false, true, factory);
		bundles = index.Bundles.Length;
		var files = new Dictionary<ulong, Entry>(index.Files.Count);
		foreach (var (hash, f) in index.Files)
			files[hash] = new Entry(f.Path ?? "#" + hash.ToString("x16", CultureInfo.InvariantCulture), f.Size, f.Offset, f.BundleRecord.Path);
		return files;
	}
}

/// <summary>
/// The report written at every game update: what changed in the game files and how each PoeFree option copes with it.
/// It is plain text meant to be pasted into a conversation with an assistant (or a GitHub issue) to ask for PoeFree to be adapted.
/// </summary>
public static class GameUpdateReport {
	/// <summary>Paths listed per section in the full report.</summary>
	public const int FullSample = 400;

	/// <param name="before">Compatibility check stored before the update (used only if it was made on the old index).</param>
	/// <param name="after">Compatibility check of the new, original game files; null if it could not run.</param>
	/// <param name="settings">Options the user has selected (to mark which rows matter to them).</param>
	/// <param name="sample">Maximum number of paths listed per section.</param>
	public static string Build(GameUpdate update, IndexDiff diff, CompatibilityReport? before, CompatibilityReport? after,
		PatchSettings? settings = null, int sample = FullSample) {
		var inv = CultureInfo.InvariantCulture;
		var sb = new StringBuilder();
		var install = update.Install;
		var oldLabel = GameProbe.VersionLabel(update.OldVersion) ?? "unknown";
		var newLabel = GameProbe.VersionLabel(update.NewVersion) ?? "unknown (the game has not been started with it yet)";
		if (before is not null && before.IndexSha256 != update.OldSha256)
			before = null;
		if (after is not null && (after.IndexSha256 != update.NewSha256 || after.ArchiveModified))
			after = null;

		sb.AppendLine("# PoeFree: game update report");
		sb.AppendLine();
		sb.AppendLine(inv, $"- Game: {(install.Version == GameVersion.Poe2 ? "Path of Exile 2" : "Path of Exile 1")} ({install.Platform})");
		sb.AppendLine(inv, $"- Game files: {install.ArchivePath}");
		sb.AppendLine(inv, $"- Game version: {oldLabel} -> {newLabel}");
		sb.AppendLine(inv, $"- Index SHA-256: {Short(update.OldSha256)} -> {Short(update.NewSha256)}");
		sb.AppendLine(inv, $"- Detected: {update.DetectedUtc:yyyy-MM-dd HH:mm} UTC");
		sb.AppendLine(inv, $"- PoeFree: {UpdateChecker.CurrentVersion.ToString(3)} (https://github.com/{UpdateChecker.DefaultRepository})");
		if (settings is not null) {
			var enabled = PatchRegistry.All.Where(p => p.IsEnabled(settings)).Select(p => p.Id).ToList();
			sb.AppendLine(inv, $"- Options in use ({enabled.Count}): {(enabled.Count == 0 ? "none" : string.Join(", ", enabled))}");
		}
		sb.AppendLine();

		// ---- options
		sb.AppendLine("## Options");
		sb.AppendLine();
		if (after is null) {
			sb.AppendLine("The compatibility check could not run on the new game files (they are modified or could not be read).");
		} else {
			var broken = after.Results.Where(r => r.Status == HealthStatus.Broken).ToList();
			var warn = after.Results.Where(r => r.Status == HealthStatus.Warning).ToList();
			sb.AppendLine(inv, $"Every option was run on the new game files without writing anything: {after.Results.Count(r => r.Status == HealthStatus.Ok)} OK, {broken.Count} broken, {warn.Count} with errors.");
			if (before is null)
				sb.AppendLine("There is no check of the previous game version to compare with.");
			sb.AppendLine();
			sb.AppendLine("`status option: files found before -> after, files changed before -> after` (* = in use)");
			sb.AppendLine();
			foreach (var r in after.Results.OrderBy(r => r.Status switch { HealthStatus.Broken => 0, HealthStatus.Warning => 1, HealthStatus.Ok => 2, _ => 3 })
				.ThenBy(r => r.Id, StringComparer.Ordinal)) {
				if (r.Status == HealthStatus.Skipped)
					continue;
				var b = before?.Results.FirstOrDefault(x => x.Id == r.Id);
				var inUse = settings is not null && PatchRegistry.Get(r.Id)?.IsEnabled(settings) == true ? "*" : "";
				var was = b is null ? "" : b.Status != r.Status ? $" (was {b.Status})" : "";
				var note = r.Status == HealthStatus.Broken ? $" [{r.Reason}]"
					: b is not null && r.Status == HealthStatus.Ok && Shrunk(b.Changes, r.Changes) ? " [changes far fewer files than before]" : "";
				sb.AppendLine(inv, $"- {r.Status.ToString().ToUpperInvariant()}{was} {r.Id}{inUse}: found {Count(b?.Targets)} -> {r.Targets}, changed {Count(b?.Changes)} -> {r.Changes}{note}");
				foreach (var e in r.Errors.Take(3))
					sb.AppendLine(inv, $"  - error: {e}");
			}
		}
		sb.AppendLine();

		// ---- files
		sb.AppendLine("## Game files");
		sb.AppendLine();
		sb.AppendLine(inv, $"- Files: {diff.OldFiles:N0} -> {diff.NewFiles:N0} (bundles: {diff.OldBundles:N0} -> {diff.NewBundles:N0})");
		sb.AppendLine(inv, $"- Added: {diff.Added.Count:N0}, removed: {diff.Removed.Count:N0}, changed size: {diff.Resized.Count:N0}, moved with the same size: {diff.Moved:N0}");
		sb.AppendLine("- A file that kept its size may still have changed: the index does not tell.");
		sb.AppendLine();

		var groups = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
		void Tally(string path, int column) {
			if (!PoeText.IsTextPath(path))
				return;
			var key = Group(path);
			if (!groups.TryGetValue(key, out var row))
				groups[key] = row = new int[3];
			row[column]++;
		}
		foreach (var p in diff.Added)
			Tally(p, 0);
		foreach (var p in diff.Removed)
			Tally(p, 1);
		foreach (var r in diff.Resized)
			Tally(r.Path, 2);
		if (groups.Count > 0) {
			sb.AppendLine("### Text files by folder and type (added / removed / changed size)");
			sb.AppendLine();
			foreach (var (key, row) in groups.OrderByDescending(g => g.Value.Sum()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(60))
				sb.AppendLine(inv, $"- {key}: +{row[0]} / -{row[1]} / ~{row[2]}");
			if (groups.Count > 60)
				sb.AppendLine(inv, $"- ... {groups.Count - 60} more group(s)");
			sb.AppendLine();
		}
		List(sb, "Added", TextFirst(diff.Added, p => p), p => p, sample);
		List(sb, "Removed", TextFirst(diff.Removed, p => p), p => p, sample);
		List(sb, "Changed size", TextFirst(diff.Resized, r => r.Path), r => string.Create(inv, $"{r.Path} ({r.OldSize} -> {r.NewSize})"), sample);
		return sb.ToString();
	}

	static string Short(string sha) => sha.Length > 16 ? sha[..16] : sha;
	static string Count(int? n) => n is { } v ? v.ToString(CultureInfo.InvariantCulture) : "?";
	static bool Shrunk(int before, int after) => before >= 20 && after < before / 2;

	/// <summary>Two folder levels plus the extension: <c>metadata/effects (.aoc)</c>.</summary>
	static string Group(string path) {
		var parts = path.Split('/');
		var folder = parts.Length <= 1 ? "(root)" : string.Join('/', parts.Take(Math.Min(2, parts.Length - 1)));
		return $"{folder} ({Path.GetExtension(path)})";
	}

	/// <summary>Files PoeFree can edit come first: they are the ones an option may depend on.</summary>
	static List<T> TextFirst<T>(IReadOnlyList<T> items, Func<T, string> path) =>
		[.. items.Where(i => PoeText.IsTextPath(path(i))), .. items.Where(i => !PoeText.IsTextPath(path(i)))];

	static void List<T>(StringBuilder sb, string title, List<T> items, Func<T, string> text, int sample) {
		if (items.Count == 0)
			return;
		sb.AppendLine(CultureInfo.InvariantCulture, $"### {title} ({items.Count:N0}{(items.Count > sample ? $", first {sample}" : "")})");
		sb.AppendLine();
		foreach (var i in items.Take(sample))
			sb.AppendLine(text(i));
		sb.AppendLine();
	}

	/// <summary>Cuts every list of paths of a report down to <paramref name="perList"/> lines (for the clipboard).</summary>
	public static string Shorten(string report, int perList = 40) {
		var sb = new StringBuilder();
		int kept = 0, dropped = 0;
		var inList = false;
		void Close() {
			if (dropped > 0)
				sb.AppendLine(CultureInfo.InvariantCulture, $"... {dropped:N0} more in the full report").AppendLine();
			dropped = 0;
		}
		foreach (var line in report.Split('\n').Select(l => l.TrimEnd('\r'))) {
			if (line.StartsWith('#')) {
				Close();
				inList = line.StartsWith("### Added", StringComparison.Ordinal) || line.StartsWith("### Removed", StringComparison.Ordinal)
					|| line.StartsWith("### Changed size", StringComparison.Ordinal);
				kept = 0;
				sb.AppendLine(line);
			} else if (inList && line.Length > 0) {
				if (kept++ < perList)
					sb.AppendLine(line);
				else
					dropped++;
			} else if (!(inList && dropped > 0)) {
				sb.AppendLine(line);
			}
		}
		Close();
		return sb.ToString().TrimEnd() + Environment.NewLine;
	}

	/// <summary>Writes the report next to the baseline of the install and returns its path.</summary>
	public static string Save(GameUpdate update, string text) {
		var dir = GameUpdateTracker.FolderFor(update.Install.ArchivePath);
		Directory.CreateDirectory(dir);
		var file = Path.Combine(dir, $"update-{update.DetectedUtc.ToLocalTime():yyyyMMdd-HHmmss}.md");
		File.WriteAllText(file, text);
		return file;
	}
}
