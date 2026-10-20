using System.Diagnostics;
using PoeFree.Core.Archive;
using PoeFree.Core.Text;

namespace PoeFree.Core.Patching;

public sealed record ApplySummary(int FilesChanged, int FilesAdded, IReadOnlyDictionary<string, int> FilesPerPatch, TimeSpan Elapsed) {
	/// <summary>Selected options that did not change anything (probably broken by a game update).</summary>
	public IReadOnlyList<Diagnostics.PatchHealth> Problems { get; init; } = [];
}

/// <summary>Runs patches against an archive: select files, transform them in memory, generate new files, then commit.</summary>
public static class PatchEngine {
	/// <summary>
	/// Runs every enabled patch and returns the pending changes without writing anything (also used as a dry run).
	/// </summary>
	/// <param name="isActive">Overrides <see cref="Patch.IsEnabled"/> (used by the compatibility check).</param>
	/// <param name="measureIndependently">
	/// Also evaluates each patch on the original text of every file, so <see cref="PatchContext.IndependentChanges"/> tells whether a patch
	/// still works by itself even when another patch already changed the same file. Slower; used by the compatibility check.
	/// </param>
	public static PatchContext Prepare(GameArchive archive, PatchSettings settings, IgnoreList ignore, IEnumerable<Patch>? patches = null,
		IProgress<string>? log = null, IProgress<double>? progress = null, CancellationToken ct = default,
		Func<Patch, bool>? isActive = null, bool measureIndependently = false) {
		var active = (patches ?? PatchRegistry.All)
			.Where(p => p.Supports(archive.Version) && (isActive?.Invoke(p) ?? p.IsEnabled(settings)))
			.ToList();
		var ctx = new PatchContext(archive, settings, ignore, log);
		ctx.ActivePatches.AddRange(active);
		if (active.Count == 0) {
			log?.Report("No option selected.");
			return ctx;
		}
		log?.Report("Options: " + string.Join(", ", active.Select(p => p.Id)));

		// 1. Select files.
		var targets = new Dictionary<string, List<Patch>>(StringComparer.Ordinal);
		void Add(string path, Patch patch) {
			if (!targets.TryGetValue(path, out var list))
				targets[path] = list = [];
			if (!list.Contains(patch))
				list.Add(patch);
		}
		foreach (var patch in active) {
			ctx.CurrentPatch = patch.Id;
			var exts = patch.Extensions.ToArray();
			foreach (var folder in patch.Folders(ctx))
				foreach (var p in ctx.FilesUnder(folder, exts))
					Add(p, patch);
			foreach (var f in patch.Files(ctx)) {
				var p = GameArchive.Normalize(f);
				if (archive.Exists(p))
					Add(p, patch);
			}
		}
		var protectedCount = 0;
		foreach (var p in targets.Keys.ToList())
			if (ignore.IsProtected(p)) {
				targets.Remove(p);
				protectedCount++;
			}
		if (protectedCount > 0)
			log?.Report($"Ignore list protected {protectedCount} file(s).");
		foreach (var patch in active)
			ctx.TargetCounts[patch.Id] = 0;
		foreach (var list in targets.Values)
			foreach (var patch in list)
				ctx.TargetCounts[patch.Id]++;
		log?.Report($"Reading {targets.Count} file(s)...");

		// 2. Transform, grouped by bundle for speed. Patches run in registry order on each file.
		var order = active.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
		var done = 0;
		var total = Math.Max(1, targets.Count);
		var errors = 0;
		archive.ReadMany(targets.Keys, (path, bytes) => {
			var original = PoeText.Decode(bytes.Span, out var kind);
			var text = original;
			var applied = new List<string>();
			foreach (var patch in targets[path].OrderBy(p => order[p])) {
				try {
					var result = patch.Transform(path, text, ctx);
					var changed = result is not null && result != text;
					if (measureIndependently && !ReferenceEquals(text, original)) {
						var alone = patch.Transform(path, original, ctx);
						if (alone is not null && alone != original)
							ctx.IndependentChanges[patch.Id] = ctx.IndependentChanges.GetValueOrDefault(patch.Id) + 1;
					} else if (changed) {
						ctx.IndependentChanges[patch.Id] = ctx.IndependentChanges.GetValueOrDefault(patch.Id) + 1;
					}
					if (changed) {
						text = result!;
						applied.Add(patch.Id);
					}
				} catch (Exception e) {
					ctx.AddError(patch.Id, $"{path}: {e.Message}");
					if (errors++ < 20)
						log?.Report($"[{patch.Id}] failed on {path}: {e.Message}");
				}
			}
			if (text != original)
				ctx.RecordTransform(path, text, kind, applied);
			if (++done % 500 == 0)
				progress?.Report(0.8 * done / total);
		}, ct);
		ct.ThrowIfCancellationRequested();

		// 3. Generators (cross file edits, new files).
		foreach (var patch in active) {
			ct.ThrowIfCancellationRequested();
			ctx.CurrentPatch = patch.Id;
			try {
				patch.Generate(ctx);
			} catch (Exception e) {
				ctx.AddError(patch.Id, e.Message);
				log?.Report($"[{patch.Id}] failed: {e.Message}");
			}
		}
		ctx.CurrentPatch = null;
		progress?.Report(0.85);
		return ctx;
	}

	/// <summary>Writes the pending changes of <paramref name="ctx"/> into the archive and saves the index.</summary>
	public static ApplySummary Commit(PatchContext ctx, IProgress<string>? log = null, IProgress<double>? progress = null, Stopwatch? sw = null) {
		sw ??= Stopwatch.StartNew();
		var archive = ctx.Archive;
		var perPatch = new Dictionary<string, int>(StringComparer.Ordinal);
		int changed = 0, added = 0, i = 0;
		var total = Math.Max(1, ctx.Changes.Count);
		foreach (var change in ctx.Changes.Values.OrderBy(c => c.IsNew)) {
			archive.Write(change.Path, PoeText.Encode(change.Text, change.Encoding));
			if (change.IsNew)
				added++;
			else
				changed++;
			foreach (var p in change.Patches)
				perPatch[p] = perPatch.GetValueOrDefault(p) + 1;
			if (++i % 200 == 0)
				progress?.Report(0.85 + 0.1 * i / total);
		}
		if (ctx.Changes.Count > 0) {
			log?.Report("Saving index...");
			archive.Save();
		}
		progress?.Report(1);
		return new ApplySummary(changed, added, perPatch, sw.Elapsed);
	}

	/// <summary>
	/// Full apply flow: restore original files if needed, back up the vanilla index, apply all options and remember the result.
	/// The game must be closed.
	/// </summary>
	public static async Task<ApplySummary> ApplyAsync(string archivePath, PatchSettings settings, IgnoreList ignore,
		IProgress<string>? log = null, IProgress<double>? progress = null, CancellationToken ct = default, string? patchCdnUrl = null) {
		var sw = Stopwatch.StartNew();
		OodleResolver.EnsureLoaded();
		var version = Game.GameDetector.GuessVersion(archivePath);
		using (var probe = GameArchive.Open(archivePath, readOnly: true))
			version = probe.Version;

		if (!RestoreService.IsOriginal(archivePath)) {
			if (!settings.RestoreBeforeApply)
				throw new InvalidOperationException("The game files are already modified. Restore them first (or enable 'Restore before apply').");
			log?.Report("Restoring original files before applying...");
			await RestoreService.RestoreAsync(archivePath, version, log, ct);
		}
		RestoreService.EnsureBackup(archivePath);
		progress?.Report(0.02);

		ApplySummary summary;
		using (var archive = GameArchive.Open(archivePath, readOnly: false, patchCdnUrl)) {
			var ctx = Prepare(archive, settings, ignore, null, log, progress, ct);
			var problems = Diagnostics.CompatibilityCheck.Evaluate(ctx).Where(h => h.IsProblem).ToList();
			foreach (var p in problems)
				log?.Report($"WARNING: option '{p.Id}' {p.Reason switch {
					Diagnostics.HealthReason.NoTargets => "found no files to change",
					Diagnostics.HealthReason.NoChanges => $"found {p.Targets} file(s) but changed none",
					_ => "raised errors: " + string.Join("; ", p.Errors.Take(3)),
				}}. The game may have changed; check for a PoeFree update.");
			log?.Report($"Writing {ctx.Changes.Count} file(s)...");
			summary = Commit(ctx, log, progress, sw) with { Problems = problems };
		}
		if (summary.FilesChanged + summary.FilesAdded > 0)
			RestoreService.RecordPatched(archivePath);
		log?.Report($"Done: {summary.FilesChanged} file(s) modified, {summary.FilesAdded} added in {summary.Elapsed.TotalSeconds:0.0}s.");
		return summary;
	}
}
