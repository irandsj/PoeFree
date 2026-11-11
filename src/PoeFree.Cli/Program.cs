using System.Diagnostics;
using System.Text.Json;
using PoeFree.Core;
using PoeFree.Core.Archive;
using PoeFree.Core.Diagnostics;
using PoeFree.Core.Game;
using PoeFree.Core.Patching;
using PoeFree.Core.Text;

// PoeFree command line. Run without arguments for help.

var log = new Progress<string>(Console.WriteLine);
var a = args.ToList();
if (a.Count == 0 || a[0] is "-h" or "--help" or "help") {
	PrintHelp();
	return 0;
}

string? Option(string name) {
	var i = a.IndexOf(name);
	if (i < 0 || i + 1 >= a.Count)
		return null;
	var v = a[i + 1];
	a.RemoveRange(i, 2);
	return v;
}
bool Flag(string name) => a.Remove(name);

var home = Option("--home");
if (home is not null)
	AppPaths.SetDataDirectory(home);
OodleResolver.PreferredPath = Option("--oodle");
OodleResolver.Install();
SelfUpdater.CleanupOldFiles();

try {
	switch (a[0]) {
		case "detect": {
			var installs = GameDetector.FindInstalls();
			if (installs.Count == 0)
				Console.WriteLine("No Path of Exile install found.");
			foreach (var i in installs)
				Console.WriteLine(i.DisplayName);
			return 0;
		}
		case "patches": {
			foreach (var p in PatchRegistry.All)
				Console.WriteLine($"{p.Id,-24} {(p.Experimental ? "[experimental] " : "")}{p.Name} - {p.Description}");
			Console.WriteLine();
			foreach (var (name, ids) in PatchRegistry.Presets)
				Console.WriteLine($"preset {name}: {string.Join(", ", ids)}");
			return 0;
		}
		case "oodle": {
			if (a.Count > 1 && a[1] == "download")
				await OodleResolver.DownloadAsync(log);
			else if (a.Count > 2 && a[1] == "import")
				Console.WriteLine("Imported to " + OodleResolver.ImportLibrary(a[2]));
			OodleResolver.EnsureLoaded();
			Console.WriteLine("Oodle loaded from: " + OodleResolver.LoadedPath);
			return 0;
		}
		case "info": {
			var path = Arg(1);
			using var ar = GameArchive.Open(path);
			Console.WriteLine($"Archive : {ar.ArchivePath}");
			Console.WriteLine($"Game    : {ar.Version}");
			Console.WriteLine($"Bundles : {ar.Index.Bundles.Length}");
			Console.WriteLine($"Files   : {ar.Index.Files.Count} ({ar.UnnamedFiles} without path)");
			Console.WriteLine($"Oodle   : {OodleResolver.LoadedPath}");
			var custom = ar.CustomBundleNames().ToList();
			Console.WriteLine($"Modified: {(custom.Count > 0 ? "yes - " + string.Join(", ", custom) : "no")}");
			var state = RestoreService.LoadState(path);
			Console.WriteLine($"Backup  : {(state is null ? "none" : $"{state.CreatedUtc:u}, patched {(state.PatchedUtc?.ToString("u") ?? "-")}")}");
			return 0;
		}
		case "ls": {
			using var ar = GameArchive.Open(Arg(1));
			var prefix = a.Count > 2 ? a[2] : "";
			var limit = int.Parse(Option("--limit") ?? "200");
			foreach (var p in ar.PathsUnder(prefix).Take(limit))
				Console.WriteLine(p);
			return 0;
		}
		case "cat": {
			using var ar = GameArchive.Open(Arg(1));
			var text = ar.ReadText(Arg(2)) ?? throw new FileNotFoundException("Not found: " + Arg(2));
			Console.WriteLine(text);
			return 0;
		}
		case "grep": {
			using var ar = GameArchive.Open(Arg(1));
			var regex = new System.Text.RegularExpressions.Regex(Arg(2), System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);
			var context = int.Parse(Option("--context") ?? "0");
			var exts = Option("--ext")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			var limit = int.Parse(Option("--limit") ?? "500");
			var prefix = a.Count > 3 ? a[3] : "";
			var paths = ar.PathsUnder(prefix).Where(p => exts is null ? PoeText.IsTextPath(p) : exts.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase))).ToList();
			var hits = 0;
			var gate = new object();
			ar.ReadMany(paths, (p, bytes) => {
				var text = PoeText.Decode(bytes.Span);
				if (!regex.IsMatch(text))
					return;
				var lines = text.Split('\n');
				lock (gate) {
					for (var i = 0; i < lines.Length && hits < limit; i++) {
						if (!regex.IsMatch(lines[i]))
							continue;
						hits++;
						for (var j = Math.Max(0, i - context); j <= Math.Min(lines.Length - 1, i + context); j++)
							Console.WriteLine($"{p}:{j + 1}{(j == i ? ':' : '-')} {lines[j].TrimEnd()}");
						if (context > 0)
							Console.WriteLine("--");
					}
				}
			});
			Console.Error.WriteLine($"{hits} match(es) in {paths.Count} file(s) searched");
			return 0;
		}
		case "check": {
			using var ar = GameArchive.Open(Arg(1), true, Option("--cdn"));
			if (ar.IsModified)
				Console.WriteLine("Note: the game files are modified (restore them first for an accurate check).");
			var sw = Stopwatch.StartNew();
			var report = CompatibilityCheck.Run(ar, Flag("--verbose") ? log : null);
			if (!ar.IsModified)
				CompatibilityCheck.Save(report);
			foreach (var r in report.Results)
				Console.WriteLine($"{r.Status,-8} {r.Id,-24} files={r.Targets,-6} changes={r.Changes,-6} {(r.Reason == HealthReason.None ? "" : r.Reason.ToString())} {string.Join("; ", r.Errors.Take(2))}");
			var broken = report.Results.Count(r => r.Status == HealthStatus.Broken);
			Console.WriteLine($"{report.Results.Count(r => r.Status == HealthStatus.Ok)} ok, {broken} broken, {report.Results.Count(r => r.Status == HealthStatus.Warning)} warning(s) in {sw.Elapsed.TotalSeconds:0}s");
			return broken > 0 ? 3 : 0;
		}
		case "update": {
			var install = Flag("--install");
			var dir = Option("--dir");
			Console.WriteLine("Current version: " + UpdateChecker.CurrentVersion);
			var info = await UpdateChecker.CheckAsync(Option("--repo"));
			if (info is null) {
				Console.WriteLine("PoeFree is up to date.");
				return 0;
			}
			Console.WriteLine($"New version {info.Tag}: {info.Url}");
			if (!install) {
				Console.WriteLine("Run 'poefree-cli update --install' to download and install it.");
				return 0;
			}
			dir ??= SelfUpdater.InstallDirectory
				?? throw new InvalidOperationException("This is a development build: it is not replaced. Use --dir <folder> to update a PoeFree folder.");
			var lastShown = -1;
			var files = await SelfUpdater.UpdateAsync(info, dir, new SyncProgress<double>(p => {
				var step = (int)(p * 10);
				if (step > lastShown) {
					lastShown = step;
					Console.WriteLine($"Downloading {SelfUpdater.PackageName}: {step * 10}%");
				}
			}));
			Console.WriteLine($"PoeFree {info.Tag} installed in {dir} ({string.Join(", ", files)}).");
			return 0;
		}
		case "status": {
			// Read-only: what the update watcher of the app sees for this install.
			var path = Arg(1);
			var game = new GameInstall(GameDetector.GuessVersion(path), GameDetector.GuessPlatform(path), path);
			var probe = new GameProbe();
			var raw = GameArchive.ReadRawIndex(path);
			var sha = RestoreService.Sha256(raw);
			var custom = RestoreService.CustomBundlesIn(raw, out _).Count;
			var state = RestoreService.LoadState(path);
			var baseline = GameUpdateTracker.LoadBaseline(path);
			var files = custom == 0 ? "original" : state?.PatchedSha256 == sha ? "patched by PoeFree" : "modified by another tool";
			Console.WriteLine($"Game running    : {(probe.IsRunning(game) ? "yes" : "no")}");
			Console.WriteLine($"Game version    : {GameProbe.VersionLabel(probe.InstalledVersion(game)) ?? "unknown (no client log)"}");
			Console.WriteLine($"Game files      : {files} (index {sha[..16]})");
			Console.WriteLine($"Options removed : {(custom == 0 && state?.PatchedSha256 is not null ? "yes (the game updated: apply again)" : "no")}");
			Console.WriteLine($"Last seen index : {(baseline is null ? "none yet" : $"{baseline.IndexSha256[..16]} ({GameProbe.VersionLabel(baseline.Version) ?? "version unknown"}, {baseline.SeenUtc:yyyy-MM-dd HH:mm} UTC)")}");
			if (baseline?.PendingReportFile is { } pending)
				Console.WriteLine($"Update report   : {pending}");
			return 0;
		}
		case "diff": {
			// Compares two saved _.index.bin files (what the update report is made of).
			var diff = IndexDiff.Compare(File.ReadAllBytes(Arg(1)), File.ReadAllBytes(Arg(2)));
			Console.WriteLine($"Files: {diff.OldFiles:N0} -> {diff.NewFiles:N0}; added {diff.Added.Count:N0}, removed {diff.Removed.Count:N0}, changed size {diff.Resized.Count:N0}, moved {diff.Moved:N0}");
			var limit = int.Parse(Option("--limit") ?? "50");
			foreach (var f in diff.Added.Take(limit))
				Console.WriteLine("+ " + f);
			foreach (var f in diff.Removed.Take(limit))
				Console.WriteLine("- " + f);
			foreach (var f in diff.Resized.Take(limit))
				Console.WriteLine($"~ {f.Path} ({f.OldSize} -> {f.NewSize})");
			return 0;
		}
		case "icons": {
			using var ar = GameArchive.Open(Arg(1), true, Option("--cdn"));
			foreach (var icon in PoeFree.Core.Minimap.MinimapIconCatalog.Load(ar))
				Console.WriteLine($"{icon.Index,4} {icon.Name}");
			return 0;
		}
		case "hash": {
			using var ar = GameArchive.Open(Arg(1));
			foreach (var p in a.Skip(2))
				Console.WriteLine($"{ar.Index.NameHash(GameArchive.Normalize(p)):X16} {GameArchive.Normalize(p)}");
			return 0;
		}
		case "extract": {
			using var ar = GameArchive.Open(Arg(1));
			var prefix = GameArchive.Normalize(Arg(2));
			var outDir = Arg(3);
			var paths = ar.Exists(prefix) ? [prefix] : ar.PathsUnder(prefix).ToList();
			var n = ar.ReadMany(paths, (p, bytes) => {
				var dest = Path.Combine(outDir, p.Replace('/', Path.DirectorySeparatorChar));
				Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
				File.WriteAllBytes(dest, bytes.ToArray());
			});
			Console.WriteLine($"Extracted {n} file(s) to {outDir}");
			return 0;
		}
		case "apply": {
			var path = Arg(1);
			var settings = BuildSettings();
			var ignore = IgnoreList.Parse(File.Exists(AppPaths.IgnoreListFile) ? File.ReadAllText(AppPaths.IgnoreListFile) : null);
			var ignoreFile = Option("--ignore");
			if (ignoreFile is not null)
				ignore = IgnoreList.Parse(File.ReadAllText(ignoreFile));
			var dumpDir = Option("--dump");
			var cdn = Option("--cdn");
			if (Flag("--dry-run")) {
				var sw = Stopwatch.StartNew();
				using var ar = GameArchive.Open(path, readOnly: true, cdn);
				var ctx = PatchEngine.Prepare(ar, settings, ignore, log: log);
				var byPatch = ctx.Changes.Values.SelectMany(c => c.Patches).GroupBy(x => x).Select(g => $"{g.Key}={g.Count()}");
				Console.WriteLine($"Dry run: {ctx.Changes.Count} file(s) would change ({ctx.Changes.Values.Count(c => c.IsNew)} new) in {sw.Elapsed.TotalSeconds:0.0}s");
				Console.WriteLine("Per option: " + string.Join(", ", byPatch));
				if (dumpDir is not null)
					foreach (var c in ctx.Changes.Values) {
						var dest = Path.Combine(dumpDir, c.Path.Replace('/', Path.DirectorySeparatorChar));
						Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
						File.WriteAllBytes(dest, PoeText.Encode(c.Text, c.Encoding));
					}
				return 0;
			}
			var summary = await PatchEngine.ApplyAsync(path, settings, ignore, log, patchCdnUrl: cdn);
			Console.WriteLine("Per option: " + string.Join(", ", summary.FilesPerPatch.Select(kv => $"{kv.Key}={kv.Value}")));
			return 0;
		}
		case "restore": {
			var path = Arg(1);
			var version = GameDetector.GuessVersion(path);
			if (path.EndsWith(".ggpk", StringComparison.OrdinalIgnoreCase)) {
				using var ar = GameArchive.Open(path);
				version = ar.Version;
			}
			var outcome = await RestoreService.RestoreAsync(path, version, log);
			Console.WriteLine("Result: " + outcome);
			return 0;
		}
		default:
			Console.Error.WriteLine("Unknown command: " + a[0]);
			PrintHelp();
			return 2;
	}
} catch (Exception e) {
	Console.Error.WriteLine("Error: " + e.Message);
	if (Environment.GetEnvironmentVariable("POEFREE_DEBUG") == "1")
		Console.Error.WriteLine(e);
	return 1;
}

string Arg(int i) => i < a.Count ? a[i] : throw new ArgumentException("Missing argument. See 'poefree-cli help'.");

PatchSettings BuildSettings() {
	var profile = Option("--profile");
	var settings = profile is not null ? PatchSettings.Load(profile) : new PatchSettings();
	var preset = Option("--preset");
	if (preset is not null) {
		if (!PatchRegistry.Presets.TryGetValue(preset, out var ids))
			throw new ArgumentException("Unknown preset: " + preset);
		foreach (var id in ids)
			settings.Enabled.Add(id);
	}
	var enable = Option("--enable");
	if (enable is not null)
		foreach (var id in enable.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			if (PatchRegistry.Get(id) is null)
				throw new ArgumentException("Unknown option id: " + id + " (see 'poefree-cli patches')");
			settings.Enabled.Add(id);
		}
	var zoom = Option("--zoom");
	if (zoom is not null)
		settings.CameraZoom = double.Parse(zoom, System.Globalization.CultureInfo.InvariantCulture);
	var skills = Option("--skills");
	if (skills is not null)
		settings.DisabledSkillEffects.AddRange(skills.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
	if (Flag("--no-restore"))
		settings.RestoreBeforeApply = false;
	return settings;
}

static void PrintHelp() {
	Console.WriteLine("""
		PoeFree - free and open source Path of Exile performance/visibility patcher (AGPL-3.0)

		Usage: poefree-cli <command> [arguments] [options]

		Commands:
		  detect                              List detected game installs
		  patches                             List available options and presets
		  info <archive>                      Show archive info (archive = Content.ggpk or Bundles2/_.index.bin)
		  ls <archive> [prefix]               List files (--limit N)
		  cat <archive> <path>                Print a text file
		  grep <archive> <regex> [prefix]     Search text files (--ext .ot,.otc  --context N  --limit N)
		  extract <archive> <path> <outdir>   Extract a file or folder
		  apply <archive> [options]           Restore original files if needed, then apply options
		  restore <archive>                   Restore the original game files
		  check <archive>                     Test every option on the current game files (read-only); exit code 3 if some are broken
		  status <archive>                    Game running? version, state of the files, last game update seen (read-only)
		  diff <old index> <new index>        Compare two saved _.index.bin files (--limit N)
		  update [--install]                  Check GitHub for a newer PoeFree release; --install downloads, verifies
		                                      (SHA-256) and installs it over this copy  (--repo owner/name)
		  oodle [download | import <dll>]     Check / download / import the Oodle library (oo2core)

		Apply options:
		  --preset <name>        performance | maximum | visibility
		  --enable a,b,c         Option ids (see 'patches')
		  --profile <file.json>  Profile saved by the PoeFree app
		  --zoom <1.0-3.0>       Camera zoom for camera.zoom
		  --skills dir1,dir2     Effect folders to remove (skills.disable)
		  --ignore <file>        Ignore list file (default: %APPDATA%\PoeFree\ignore-list.txt)
		  --dry-run              Only compute changes (archive opened read-only); --dump <dir> writes them to disk
		  --no-restore           Do not restore modified files before applying

		Global options:
		  --oodle <path>         Path to oo2core.dll
		  --home <dir>           Data folder (default %APPDATA%\PoeFree)

		Close the game before 'apply' or 'restore'.
		""");
}

/// <summary>Progress that calls back on the reporting thread (Progress&lt;T&gt; would print out of order in a console app).</summary>
sealed class SyncProgress<T>(Action<T> handler) : IProgress<T> {
	public void Report(T value) => handler(value);
}
