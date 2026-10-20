using PoeFree.Core.Archive;
using PoeFree.Core.Game;
using PoeFree.Core.Text;

namespace PoeFree.Core.Patching;

/// <summary>A pending change to one file.</summary>
public sealed class FileChange(string path, string text, TextEncodingKind encoding, bool isNew) {
	public string Path { get; } = path;
	public string Text { get; set; } = text;
	public TextEncodingKind Encoding { get; } = encoding;
	public bool IsNew { get; } = isNew;
	public HashSet<string> Patches { get; } = new(StringComparer.Ordinal);
}

/// <summary>State shared by all patches during one apply run. Changes are kept in memory until committed.</summary>
public sealed class PatchContext {
	public GameArchive Archive { get; }
	public PatchSettings Settings { get; }
	public IgnoreList IgnoreList { get; }
	public GameVersion Version => Archive.Version;
	public IProgress<string>? Log { get; }

	readonly Dictionary<string, FileChange> changes = new(StringComparer.Ordinal);
	public IReadOnlyDictionary<string, FileChange> Changes => changes;

	/// <summary>Patch currently running; used to attribute changes.</summary>
	internal string? CurrentPatch { get; set; }

	/// <summary>Per patch: number of files selected by its folders/files (after the ignore list).</summary>
	public Dictionary<string, int> TargetCounts { get; } = new(StringComparer.Ordinal);

	/// <summary>Per patch: number of selected files the patch would change on its own (from the original text).</summary>
	public Dictionary<string, int> IndependentChanges { get; } = new(StringComparer.Ordinal);

	/// <summary>Per patch: errors raised by Transform/Generate.</summary>
	public Dictionary<string, List<string>> Errors { get; } = new(StringComparer.Ordinal);

	/// <summary>Patches that ran in this context.</summary>
	public List<Patch> ActivePatches { get; } = [];

	internal void AddError(string patch, string message) {
		if (!Errors.TryGetValue(patch, out var list))
			Errors[patch] = list = [];
		if (list.Count < 20)
			list.Add(message);
	}

	public PatchContext(GameArchive archive, PatchSettings settings, IgnoreList ignoreList, IProgress<string>? log = null) {
		Archive = archive;
		Settings = settings;
		IgnoreList = ignoreList;
		Log = log;
	}

	public bool IsProtected(string path) => IgnoreList.IsProtected(GameArchive.Normalize(path));

	public bool Exists(string path) {
		path = GameArchive.Normalize(path);
		return changes.ContainsKey(path) || Archive.Exists(path);
	}

	/// <summary>Current text of a file, including changes made earlier in this run.</summary>
	public string? GetText(string path) {
		path = GameArchive.Normalize(path);
		if (changes.TryGetValue(path, out var c))
			return c.Text;
		return Archive.ReadText(path);
	}

	/// <summary>Sets the new text of an existing file. Protected files are skipped (returns false).</summary>
	public bool SetText(string path, string text) {
		path = GameArchive.Normalize(path);
		if (IsProtected(path))
			return false;
		if (changes.TryGetValue(path, out var c)) {
			c.Text = text;
		} else {
			var original = Archive.ReadText(path, out var kind) ?? throw new FileNotFoundException("File not found in the game archive", path);
			if (original == text)
				return false;
			changes[path] = c = new FileChange(path, text, kind, false);
		}
		if (CurrentPatch is not null)
			c.Patches.Add(CurrentPatch);
		return true;
	}

	/// <summary>Creates a new file (UTF-16LE with BOM, like the game's own scripts). Overwrites a pending new file with the same path.</summary>
	public void CreateFile(string path, string text, TextEncodingKind encoding = TextEncodingKind.Utf16LeBom) {
		path = GameArchive.Normalize(path);
		if (Archive.Exists(path)) {
			SetText(path, text);
			return;
		}
		var c = changes[path] = new FileChange(path, text, encoding, true);
		if (CurrentPatch is not null)
			c.Patches.Add(CurrentPatch);
	}

	internal void RecordTransform(string path, string text, TextEncodingKind kind, IEnumerable<string> patches) {
		var c = new FileChange(path, text, kind, false);
		foreach (var p in patches)
			c.Patches.Add(p);
		changes[path] = c;
	}

	/// <summary>Enumerates archive paths under a folder with one of the given extensions.</summary>
	public IEnumerable<string> FilesUnder(string folder, params string[] extensions) {
		folder = GameArchive.Normalize(folder);
		if (folder.Length > 0 && !folder.EndsWith('/') && !Path.HasExtension(folder))
			folder += "/";
		foreach (var p in Archive.PathsUnder(folder))
			if (extensions.Length == 0 || extensions.Any(e => p.EndsWith(e, StringComparison.Ordinal)))
				yield return p;
	}

	public void Info(string message) => Log?.Report(message);

	/// <summary>Reports a problem of the running patch (shown by the compatibility check and after applying).</summary>
	public void Warning(string message) {
		if (CurrentPatch is not null)
			AddError(CurrentPatch, message);
		Log?.Report($"[{CurrentPatch}] {message}");
	}
}
