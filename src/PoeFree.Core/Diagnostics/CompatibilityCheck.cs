using System.Text.Json;
using System.Text.Json.Serialization;
using PoeFree.Core.Archive;
using PoeFree.Core.ColorMods;
using PoeFree.Core.Game;
using PoeFree.Core.Minimap;
using PoeFree.Core.Patching;

namespace PoeFree.Core.Diagnostics;

public enum HealthStatus {
	Ok,
	/// <summary>Works, but some files raised errors.</summary>
	Warning,
	/// <summary>Did not change anything: the game probably changed and the option needs an update.</summary>
	Broken,
	/// <summary>Not available for this game version.</summary>
	Skipped,
}

public enum HealthReason {
	None,
	/// <summary>None of the folders/files the option works on exist any more.</summary>
	NoTargets,
	/// <summary>Files were found but none of them matched what the option looks for (format changed).</summary>
	NoChanges,
	/// <summary>The option raised errors.</summary>
	Errors,
	Unsupported,
}

public sealed record PatchHealth(string Id, string Name, HealthStatus Status, HealthReason Reason, int Targets, int Changes, IReadOnlyList<string> Errors) {
	public bool IsProblem => Status is HealthStatus.Warning or HealthStatus.Broken;
}

public sealed record CompatibilityReport(string ArchivePath, GameVersion Version, bool ArchiveModified, string IndexSha256, DateTime CheckedUtc,
	string ToolVersion, IReadOnlyList<PatchHealth> Results) {
	[JsonIgnore]
	public IEnumerable<PatchHealth> Problems => Results.Where(r => r.IsProblem);
	[JsonIgnore]
	public bool HasProblems => Problems.Any();
}

/// <summary>
/// Detects options that stopped working after a game update: every option is run (without writing anything) on the
/// current game files and must find and change at least one file. Also used after each apply for the selected options.
/// </summary>
public static class CompatibilityCheck {
	/// <summary>Settings with every option enabled and sample data for the options that need user input.</summary>
	public static PatchSettings SampleSettings(GameArchive archive) {
		var s = new PatchSettings();
		foreach (var p in PatchRegistry.All)
			s.Enabled.Add(p.Id);
		s.CameraZoom = 2.0;
		var skill = archive.PathsUnder("metadata/effects/spells/")
			.Where(p => p.EndsWith(".pet", StringComparison.Ordinal) || p.EndsWith(".ao", StringComparison.Ordinal))
			.Select(p => p[..p.LastIndexOf('/')]).FirstOrDefault();
		if (skill is not null)
			s.DisabledSkillEffects.Add(skill);
		s.ColorMods = SuggestedColorMods.For(archive.Version);
		// Plus one line that surely exists in this version of the game.
		var statText = archive.ReadText("metadata/statdescriptions/stat_descriptions.txt") ?? archive.ReadText("data/statdescriptions/stat_descriptions.csd");
		var sample = statText is null ? null : StatDescriptions.Parse("stat_descriptions.txt", statText).FirstOrDefault(d => d.StatIds.Count > 0 && d.Lines.Count > 0);
		if (sample is not null)
			s.ColorMods.Add(new ColorModEntry { StatId = sample.StatIds[0], Line = sample.Lines[0], Red = 255 });
		var chest = archive.PathsUnder("metadata/chests/").FirstOrDefault(p => p.EndsWith(".ot", StringComparison.Ordinal));
		if (chest is not null)
			s.MinimapMarkers.Add(new MinimapMarker { ObjectPath = chest, Icon = 79 });
		s.RarityIcons = new RarityIconSettings { Normal = true, Magic = true, Rare = true, Unique = true };
		return s;
	}

	/// <summary>Runs every option read-only and reports which ones still work. Takes a while (reads tens of thousands of files).</summary>
	public static CompatibilityReport Run(GameArchive archive, IProgress<string>? log = null, IProgress<double>? progress = null, CancellationToken ct = default) {
		var settings = SampleSettings(archive);
		var ctx = PatchEngine.Prepare(archive, settings, new IgnoreList(), null, log, progress, ct, isActive: _ => true, measureIndependently: true);
		var results = Evaluate(ctx);
		foreach (var p in PatchRegistry.All.Where(p => !p.Supports(archive.Version)))
			results.Add(new PatchHealth(p.Id, p.Name, HealthStatus.Skipped, HealthReason.Unsupported, 0, 0, []));
		string sha;
		try {
			sha = RestoreService.Sha256(GameArchive.ReadRawIndex(archive.ArchivePath));
		} catch {
			sha = "";
		}
		return new CompatibilityReport(archive.ArchivePath, archive.Version, archive.IsModified, sha, DateTime.UtcNow,
			typeof(CompatibilityCheck).Assembly.GetName().Version?.ToString(3) ?? "", results);
	}

	/// <summary>Evaluates the options that ran in <paramref name="ctx"/>.</summary>
	public static List<PatchHealth> Evaluate(PatchContext ctx) {
		var changesByPatch = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var c in ctx.Changes.Values)
			foreach (var p in c.Patches)
				changesByPatch[p] = changesByPatch.GetValueOrDefault(p) + 1;
		return ctx.ActivePatches.Select(p => {
			var targets = ctx.TargetCounts.GetValueOrDefault(p.Id);
			// An option that selects files must change some of them by itself; generator-only options are judged by what they produced.
			var changes = targets > 0 ? ctx.IndependentChanges.GetValueOrDefault(p.Id) : changesByPatch.GetValueOrDefault(p.Id);
			var selectsFiles = p.Extensions.Count > 0 || p.Files(ctx).Any();
			return Evaluate(p.Id, p.Name, targets, changes, ctx.Errors.GetValueOrDefault(p.Id) ?? [], selectsFiles);
		}).ToList();
	}

	public static PatchHealth Evaluate(string id, string name, int targets, int changes, IReadOnlyList<string> errors, bool selectsFiles = true) {
		if (changes > 0 && !(selectsFiles && targets == 0))
			return new PatchHealth(id, name, errors.Count > 0 ? HealthStatus.Warning : HealthStatus.Ok,
				errors.Count > 0 ? HealthReason.Errors : HealthReason.None, targets, changes, errors);
		var reason = targets == 0 && selectsFiles ? HealthReason.NoTargets : errors.Count > 0 ? HealthReason.Errors : HealthReason.NoChanges;
		return new PatchHealth(id, name, HealthStatus.Broken, reason, targets, 0, errors);
	}

	#region Last report per install

	static string StorePath(string archivePath) {
		var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
			System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(archivePath).ToLowerInvariant())))[..12];
		return Path.Combine(AppPaths.DataDirectory, "compatibility", key + ".json");
	}

	public static void Save(CompatibilityReport report) {
		var file = StorePath(report.ArchivePath);
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllText(file, JsonSerializer.Serialize(report, DiagnosticsJsonContext.Default.CompatibilityReport));
	}

	public static CompatibilityReport? Load(string archivePath) {
		var file = StorePath(archivePath);
		if (!File.Exists(file))
			return null;
		try {
			return JsonSerializer.Deserialize(File.ReadAllText(file), DiagnosticsJsonContext.Default.CompatibilityReport);
		} catch {
			return null;
		}
	}

	/// <summary>
	/// Whether the game files changed since the last check (i.e. the game was updated), so a new check should run.
	/// Only original (unmodified) game files can be checked reliably.
	/// </summary>
	public static bool NeedsCheck(string archivePath, out string currentSha) {
		currentSha = RestoreService.Sha256(GameArchive.ReadRawIndex(archivePath));
		var last = Load(archivePath);
		var tool = typeof(CompatibilityCheck).Assembly.GetName().Version?.ToString(3) ?? "";
		return last is null || last.IndexSha256 != currentSha || last.ToolVersion != tool;
	}

	#endregion
}

[JsonSerializable(typeof(CompatibilityReport))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
internal partial class DiagnosticsJsonContext : JsonSerializerContext;
