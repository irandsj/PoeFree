using System.IO.Compression;
using PoeFree.Core;
using PoeFree.Core.Archive;
using PoeFree.Core.Diagnostics;
using PoeFree.Core.Game;
using PoeFree.Core.Patching;

namespace PoeFree.Tests;

[Collection("data-directory")] // these tests redirect the global PoeFree data folder
public class WatchAndUpdateTests {
	// ---------------------------------------------------------------- client log

	const string V1 = "https://patch.poecdn.com/3.29.3.6/";
	const string V2 = "https://patch.poecdn.com/3.30.0.1/";

	const string Session = """
		2026/11/11 15:06:37 ***** LOG FILE OPENING *****
		2026/11/11 15:06:37 99814281 587dda2f [INFO Client 29172] Send patching protocol version 7
		2026/11/11 15:06:37 99814578 587ddd6f [INFO Client 29172] Web root: https://patch.poecdn.com/3.29.3.6/
		2026/11/11 15:06:37 99814578 587ddd6e [INFO Client 29172] Backup Web root: https://patch.poecdn.com/3.29.3.5/
		2026/11/11 15:06:38 99814734 587dde2e [INFO Client 29172] Got file list for "" 0
		2026/11/11 15:06:38 99815062 587df54a [INFO Client 29172] Finished checking files
		2026/11/11 15:06:38 99815234 3b79bdd4 [INFO Client 29172] [JOB] Start

		""";

	[Fact]
	public void Installed_version_is_read_from_the_client_log() {
		Assert.Equal(V1, GameProbe.ParseInstalledVersion(Session));
		Assert.Null(GameProbe.ParseInstalledVersion("nothing useful"));
	}

	[Fact]
	public void A_start_that_did_not_finish_updating_is_ignored() {
		var interrupted = """
			2026/11/12 10:00:00 ***** LOG FILE OPENING *****
			2026/11/12 10:00:00 1 2 [INFO Client 1] Web root: https://patch.poecdn.com/3.30.0.1/
			2026/11/12 10:00:01 1 2 [INFO Client 1] Requesting folder Bundles2 1

			""";
		// The last start was closed while downloading 3.30: the install is still the previous version.
		Assert.Equal(V1, GameProbe.ParseInstalledVersion(Session + interrupted));
		// A later start that completed wins.
		Assert.Equal(V2, GameProbe.ParseInstalledVersion(Session + interrupted + interrupted.Replace("Requesting folder Bundles2 1", "Finished checking files")));
		Assert.Null(GameProbe.ParseInstalledVersion(interrupted));
	}

	[Fact]
	public void Versions_are_compared_loosely_and_shown_short() {
		Assert.True(GameProbe.SameVersion(V1, "https://patch.poecdn.com/3.29.3.6"));
		Assert.False(GameProbe.SameVersion(V1, "https://patch.poecdn.com/3.29.3.7/"));
		Assert.Equal("3.29.3.6", GameProbe.VersionLabel(V1));
		Assert.Equal("3.29.3.6", GameProbe.VersionLabel("3.29.3.6"));
		Assert.Null(GameProbe.VersionLabel(" "));
		Assert.Null(GameProbe.VersionLabel(null));
	}

	[Fact]
	public void Version_is_read_from_the_log_files_of_an_install() {
		var game = NewTempDir("poefree-log-");
		try {
			var install = new GameInstall(GameVersion.Poe1, GamePlatform.Standalone, Path.Combine(game, "Content.ggpk"));
			var probe = new GameProbe();
			Assert.Null(probe.InstalledVersion(install));
			Directory.CreateDirectory(Path.Combine(game, "logs"));
			File.WriteAllText(Path.Combine(game, "logs", "Client.txt"), new string('x', 3 << 20) + "\n" + Session);
			Assert.Equal(V1, probe.InstalledVersion(install));
			// The log of the last session wins over the long one.
			File.WriteAllText(Path.Combine(game, "logs", "LatestClient.txt"), Session.Replace("3.29.3.6", "3.30.0.1"));
			Assert.Equal(V2, probe.InstalledVersion(install));
			Assert.False(probe.IsRunning(install)); // no game runs from a temporary folder
		} finally {
			Directory.Delete(game, true);
		}
	}

	// ---------------------------------------------------------------- fingerprint

	[Fact]
	public void Fingerprint_follows_what_changes_the_result() {
		var a = new PatchSettings().Set("env.fog", true).Set("camera.zoom", true);
		var b = new PatchSettings().Set("CAMERA.ZOOM", true).Set("env.fog", true);
		var none = new IgnoreList();
		Assert.Equal(a.Fingerprint(none), b.Fingerprint(none)); // order and case of ids do not matter
		Assert.Equal(a.Fingerprint(none), a.Fingerprint(IgnoreList.Parse(IgnoreList.DefaultText))); // comments do not matter
		b.CameraZoom = 2.5;
		Assert.NotEqual(a.Fingerprint(none), b.Fingerprint(none));
		Assert.NotEqual(a.Fingerprint(none), a.Fingerprint(IgnoreList.Parse("metadata/effects/x")));
		Assert.NotEqual(a.Fingerprint(none), new PatchSettings().Set("env.fog", true).Fingerprint(none));
	}

	// ---------------------------------------------------------------- self update

	static string NewTempDir(string prefix) {
		var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	static string MakePackage(string dir, params (string Name, string Content)[] files) {
		var zip = Path.Combine(dir, "package.zip");
		using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
		foreach (var (name, content) in files) {
			using var w = new StreamWriter(archive.CreateEntry(name).Open());
			w.Write(content);
		}
		return zip;
	}

	static readonly string Gui = OperatingSystem.IsWindows() ? "PoeFree.exe" : "PoeFree";

	[Fact]
	public void Package_replaces_the_install_and_old_files_are_cleaned() {
		var work = NewTempDir("poefree-upd-");
		var install = Path.Combine(work, "install");
		Directory.CreateDirectory(install);
		var min = SelfUpdater.MinimumGuiSize;
		SelfUpdater.MinimumGuiSize = 1;
		try {
			File.WriteAllText(Path.Combine(install, Gui), "old gui");
			File.WriteAllText(Path.Combine(install, "poefree-cli.exe"), "old cli");
			File.WriteAllText(Path.Combine(install, "notes.old"), "user file"); // not ours: must survive
			File.WriteAllText(Path.Combine(install, "settings.json"), "keep");
			var zip = MakePackage(work, (Gui, "new gui"), ("poefree-cli.exe", "new cli"), ("README.md", "readme"));

			// The running executable is locked for writing/deleting but can be renamed: simulate it.
			using (new FileStream(Path.Combine(install, Gui), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) {
				var installed = SelfUpdater.InstallPackage(zip, install);
				Assert.Equal(3, installed.Count);
			}
			Assert.Equal("new gui", File.ReadAllText(Path.Combine(install, Gui)));
			Assert.Equal("new cli", File.ReadAllText(Path.Combine(install, "poefree-cli.exe")));
			Assert.Equal("readme", File.ReadAllText(Path.Combine(install, "README.md")));
			Assert.Equal("old gui", File.ReadAllText(Path.Combine(install, Gui + ".old")));

			Assert.Equal(2, SelfUpdater.CleanupOldFiles(install));
			Assert.False(File.Exists(Path.Combine(install, Gui + ".old")));
			Assert.True(File.Exists(Path.Combine(install, "notes.old")));
			Assert.Equal("keep", File.ReadAllText(Path.Combine(install, "settings.json")));
		} finally {
			SelfUpdater.MinimumGuiSize = min;
			Directory.Delete(work, true);
		}
	}

	[Fact]
	public void Bad_package_changes_nothing() {
		var work = NewTempDir("poefree-upd-");
		var install = Path.Combine(work, "install");
		Directory.CreateDirectory(install);
		try {
			File.WriteAllText(Path.Combine(install, Gui), "old gui");
			var zip = MakePackage(work, ("something-else.txt", "x"));
			Assert.Throws<InvalidDataException>(() => SelfUpdater.InstallPackage(zip, install));
			// A package whose app executable is implausibly small is refused as well.
			File.Delete(zip);
			zip = MakePackage(work, (Gui, "tiny"));
			Assert.Throws<InvalidDataException>(() => SelfUpdater.InstallPackage(zip, install));
			Assert.Equal("old gui", File.ReadAllText(Path.Combine(install, Gui)));
			Assert.Single(Directory.GetFiles(install));
		} finally {
			Directory.Delete(work, true);
		}
	}

	[Fact]
	public async Task Package_without_download_link_is_refused() {
		var info = new UpdateInfo(new Version(9, 0, 0), "v9.0.0", "https://example.invalid", "");
		await Assert.ThrowsAsync<InvalidOperationException>(() => SelfUpdater.DownloadAsync(info));
		await Assert.ThrowsAsync<InvalidOperationException>(() => SelfUpdater.DownloadAsync(info with { PackageUrl = "http://example.invalid/x.zip" }));
	}

	// ---------------------------------------------------------------- game update report

	static PatchHealth Health(string id, HealthStatus status, int targets, int changes, HealthReason reason = HealthReason.None, params string[] errors) =>
		new(id, id, status, reason, targets, changes, errors);

	[Fact]
	public void Report_tells_what_changed_and_which_options_broke() {
		var install = new GameInstall(GameVersion.Poe1, GamePlatform.Standalone, @"C:\Games\PoE\Content.ggpk");
		var update = new GameUpdate(install, new string('A', 64), new string('B', 64), V1, V2, [], [], new DateTime(2026, 11, 11, 18, 30, 0, DateTimeKind.Utc));
		var added = Enumerable.Range(0, 90).Select(i => $"metadata/effects/new/fx{i:000}.aoc").Append("art/textures/new.dds").ToList();
		var diff = new IndexDiff(1000, 1088, added, ["metadata/environmentsettings/old.env", "art/gone.dds", "shaders/gone.hlsl"],
			[new ResizedFile("shaders/bloomgather.hlsl", 900, 1200)], 5, 40, 41);
		var before = new CompatibilityReport(install.ArchivePath, GameVersion.Poe1, false, update.OldSha256, DateTime.UtcNow, "1.1.0", [
			Health("env.fog", HealthStatus.Ok, 300, 290),
			Health("shader.bloom", HealthStatus.Ok, 2, 2),
			Health("camera.zoom", HealthStatus.Ok, 1, 1),
		]);
		var after = new CompatibilityReport(install.ArchivePath, GameVersion.Poe1, false, update.NewSha256, DateTime.UtcNow, "1.1.0", [
			Health("env.fog", HealthStatus.Ok, 300, 40),
			Health("shader.bloom", HealthStatus.Broken, 2, 0, HealthReason.NoChanges),
			Health("camera.zoom", HealthStatus.Warning, 1, 1, HealthReason.Errors, "character.ot: unexpected text"),
			Health("poe2.only", HealthStatus.Skipped, 0, 0, HealthReason.Unsupported),
		]);
		var settings = new PatchSettings().Set("shader.bloom", true).Set("env.fog", true);

		var text = GameUpdateReport.Build(update, diff, before, after, settings);
		Assert.Contains("Game version: 3.29.3.6 -> 3.30.0.1", text);
		Assert.Contains("Path of Exile 1 (Standalone)", text);
		Assert.Contains("Index SHA-256: AAAAAAAAAAAAAAAA -> BBBBBBBBBBBBBBBB", text);
		Assert.Contains("Options in use (2):", text);
		Assert.Contains("1 OK, 1 broken, 1 with errors", text);
		Assert.Contains("- BROKEN (was Ok) shader.bloom*: found 2 -> 2, changed 2 -> 0 [NoChanges]", text);
		Assert.Contains("- WARNING (was Ok) camera.zoom: found 1 -> 1, changed 1 -> 1", text);
		Assert.Contains("  - error: character.ot: unexpected text", text);
		Assert.Contains("- OK env.fog*: found 300 -> 300, changed 290 -> 40 [changes far fewer files than before]", text);
		Assert.DoesNotContain("poe2.only", text);
		// Broken options come first.
		Assert.True(text.IndexOf("shader.bloom*", StringComparison.Ordinal) < text.IndexOf("env.fog*:", StringComparison.Ordinal));
		Assert.Contains("- Files: 1,000 -> 1,088 (bundles: 40 -> 41)", text);
		Assert.Contains("- Added: 91, removed: 3, changed size: 1, moved with the same size: 5", text);
		Assert.Contains("- metadata/effects (.aoc): +90 / -0 / ~0", text);
		Assert.Contains("- shaders (.hlsl): +0 / -1 / ~1", text);
		Assert.Contains("shaders/bloomgather.hlsl (900 -> 1200)", text);
		// Text files (the ones an option can depend on) are listed before the others.
		Assert.True(text.IndexOf("shaders/gone.hlsl", StringComparison.Ordinal) < text.IndexOf("art/gone.dds", StringComparison.Ordinal));
		Assert.Contains("art/textures/new.dds", text);

		// A check made on another index is not used for the comparison.
		var stale = GameUpdateReport.Build(update, diff, before with { IndexSha256 = "other" }, after, settings);
		Assert.Contains("no check of the previous game version", stale);
		Assert.Contains("- BROKEN shader.bloom*: found ? -> 2, changed ? -> 0 [NoChanges]", stale);
		// Without a usable check of the new files the report still lists the files.
		var blind = GameUpdateReport.Build(update with { NewVersion = null }, diff, before, after with { ArchiveModified = true });
		Assert.Contains("could not run on the new game files", blind);
		Assert.Contains("3.29.3.6 -> unknown", blind);
		Assert.Contains("### Added (91)", blind);

		// The clipboard version keeps every section but cuts the long lists.
		var brief = GameUpdateReport.Shorten(text, 10);
		Assert.Contains("fx009.aoc", brief);
		Assert.DoesNotContain("fx010.aoc", brief);
		Assert.Contains("... 81 more in the full report", brief);
		Assert.Contains("### Removed (3)", brief);
		Assert.Contains("art/gone.dds", brief);
		Assert.Contains("shader.bloom*", brief);
		Assert.Contains("- metadata/effects (.aoc): +90 / -0 / ~0", brief);
		Assert.True(brief.Length < text.Length);
		Assert.Equal(text.TrimEnd() + Environment.NewLine, GameUpdateReport.Shorten(text, 1000));

		// A small sample is announced in the title.
		Assert.Contains("### Added (91, first 5)", GameUpdateReport.Build(update, diff, before, after, settings, 5));
	}

	// ---------------------------------------------------------------- watching the game files

	sealed class FakeProbe : IGameProbe {
		public string? Version = V1;
		public bool IsRunning(GameInstall install) => false;
		public string? InstalledVersion(GameInstall install) => Version;
	}

	/// <summary>
	/// Makes the index a game update would leave: still original (no mod bundles), but with one file of another size
	/// and one file somewhere else.
	/// </summary>
	static byte[] UpdatedIndex(byte[] original, out string resized) {
		using var factory = new SteamBundleFactory(AppContext.BaseDirectory); // never used: no bundle is opened
		var stream = new MemoryStream();
		stream.Write(original);
		stream.Position = 0;
		using var index = new LibBundle3.Index(stream, true, true, factory);
		var files = index.Files.Values.Where(f => f.Path!.EndsWith(".dds", StringComparison.Ordinal) && f.Size > 64 && f.Offset > 0)
			.OrderBy(f => f.Path, StringComparer.Ordinal).Take(2).ToList();
		resized = files[0].Path!;
		files[0].Redirect(files[0].BundleRecord, files[0].Offset, files[0].Size - 1);
		files[1].Redirect(files[1].BundleRecord, files[1].Offset - 1, files[1].Size);
		index.Save();
		return stream.ToArray();
	}

	/// <summary>Whole flow on a copy of real game files (skipped without POEFREE_SANDBOX, like the other integration tests).</summary>
	[Fact]
	public async Task Game_update_is_noticed_from_the_files_alone() {
		var sandbox = Environment.GetEnvironmentVariable("POEFREE_SANDBOX");
		if (string.IsNullOrEmpty(sandbox) || !File.Exists(Path.Combine(sandbox, "Bundles2", "_.index.bin")))
			return;
		OodleResolver.PreferredPath = Environment.GetEnvironmentVariable("POEFREE_OODLE");
		OodleResolver.Install();
		if (!OodleResolver.IsAvailable())
			return;
		var home = NewTempDir("poefree-test-");
		AppPaths.SetDataDirectory(home);
		var game = NewTempDir("poefree-watch-");
		try {
			foreach (var file in Directory.EnumerateFiles(Path.Combine(sandbox, "Bundles2"), "*", SearchOption.AllDirectories)) {
				var dest = Path.Combine(game, "Bundles2", Path.GetRelativePath(Path.Combine(sandbox, "Bundles2"), file));
				Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
				File.Copy(file, dest);
			}
			var index = Path.Combine(game, "Bundles2", "_.index.bin");
			var vanilla = File.ReadAllBytes(index);
			var vanillaSha = RestoreService.Sha256(vanilla);
			var install = new GameInstall(GameVersion.Poe1, GamePlatform.Steam, index);
			var settings = new PatchSettings().Set("env.fog", true).Set("camera.zoom", true).Set("minimap.rarity", true);
			var ignore = new IgnoreList();
			var probe = new FakeProbe();
			var tracker = new GameUpdateTracker(install, probe);
			Assert.Equal(WatchedFiles.Unknown, tracker.Files);

			// 1. First look: the index is remembered and nothing is reported.
			Assert.Null(GameUpdateTracker.LoadBaseline(index));
			Assert.Null(tracker.Poll());
			Assert.Equal(WatchedFiles.Original, tracker.Files);
			Assert.False(tracker.OptionsWereRemoved);
			var baseline = GameUpdateTracker.LoadBaseline(index)!;
			Assert.Equal(vanillaSha, baseline.IndexSha256);
			Assert.Equal(V1, baseline.Version);
			Assert.StartsWith(home, GameUpdateTracker.FolderFor(index));
			for (var i = 0; i < 30; i++)
				Assert.Null(tracker.Poll());

			// 2. PoeFree's own patch is not an update: the original index of this version is the backup.
			var summary = await PatchEngine.ApplyAsync(index, settings, ignore);
			Assert.True(summary.FilesChanged > 100);
			var patched = File.ReadAllBytes(index);
			Assert.Null(tracker.Poll());
			Assert.Null(tracker.Poll());
			Assert.Equal(WatchedFiles.PatchedByPoeFree, tracker.Files);
			Assert.False(tracker.OptionsWereRemoved);
			Assert.Equal(vanillaSha, GameUpdateTracker.LoadBaseline(index)!.IndexSha256);
			// A watcher started on a patched game (no baseline yet) takes the backup as the original.
			Directory.Delete(GameUpdateTracker.FolderFor(index), true);
			Assert.Null(new GameUpdateTracker(install, probe).Poll());
			Assert.Equal(vanillaSha, GameUpdateTracker.LoadBaseline(index)!.IndexSha256);

			// The index tells which files PoeFree added and changed.
			var own = IndexDiff.Compare(vanilla, patched);
			Assert.Equal(6, own.Added.Count); // tracker + marker for magic, rare and unique
			Assert.All(own.Added, p => Assert.StartsWith("#", p)); // added by hash only: the index holds no path for them
			Assert.Empty(own.Removed);
			Assert.Equal(own.OldFiles + 6, own.NewFiles);
			Assert.True(own.NewBundles > own.OldBundles);
			Assert.True(own.Resized.Count + own.Moved >= summary.FilesChanged - 6);
			Assert.Contains(own.Resized, r => r.Path == "metadata/characters/character.ot");
			Assert.Equal(6, IndexDiff.Compare(patched, vanilla).Removed.Count);
			Assert.True(IndexDiff.Compare(vanilla, vanilla).IsEmpty);

			// 3. The game updates itself: the index is original again, and different.
			var updated = UpdatedIndex(vanilla, out var resized);
			var updatedSha = RestoreService.Sha256(updated);
			Assert.NotEqual(vanillaSha, updatedSha);
			Assert.Empty(RestoreService.CustomBundlesIn(updated, out _));
			GameArchive.WriteRawIndex(index, updated);
			probe.Version = V2;
			Assert.Null(tracker.Poll()); // seen once: it may still be changing
			Assert.Equal(WatchedFiles.Original, tracker.Files);
			Assert.True(tracker.OptionsWereRemoved);
			var update = tracker.Poll();
			Assert.NotNull(update);
			Assert.Equal((vanillaSha, updatedSha), (update.OldSha256, update.NewSha256));
			Assert.Equal((V1, V2), (update.OldVersion, update.NewVersion));
			Assert.Equal(vanilla, update.OldIndex);
			Assert.Equal(updated, update.NewIndex);
			var diff = IndexDiff.Compare(update.OldIndex, update.NewIndex);
			Assert.Equal((0, 0, 1, 1), (diff.Added.Count, diff.Removed.Count, diff.Resized.Count, diff.Moved));
			Assert.Equal(resized, diff.Resized[0].Path);
			Assert.Equal(diff.Resized[0].OldSize - 1, diff.Resized[0].NewSize);

			// Until it is accepted the update is reported again (e.g. PoeFree was closed in between).
			var restarted = new GameUpdateTracker(install, probe);
			Assert.Null(restarted.Poll());
			Assert.NotNull(restarted.Poll());

			// 4. The report is written and the new index becomes the baseline.
			var report = GameUpdateReport.Save(update, GameUpdateReport.Build(update, diff, null, null, settings));
			Assert.True(File.Exists(report));
			Assert.Contains(resized, File.ReadAllText(report));
			GameUpdateTracker.Accept(update, report, reapplyPending: true);
			baseline = GameUpdateTracker.LoadBaseline(index)!;
			Assert.Equal(updatedSha, baseline.IndexSha256);
			Assert.Equal((V2, V1), (baseline.Version, baseline.PreviousVersion));
			Assert.Equal((report, V1, V2, true), (baseline.PendingReportFile, baseline.PendingOldVersion, baseline.PendingNewVersion, baseline.ReapplyPending));
			Assert.Null(tracker.Poll());
			Assert.Null(tracker.Poll());

			// 5. The options are applied again on the new version: still no update.
			await PatchEngine.ApplyAsync(index, settings, ignore);
			GameUpdateTracker.SetReapplyPending(index, false);
			Assert.Null(tracker.Poll());
			Assert.Null(tracker.Poll());
			Assert.Equal(WatchedFiles.PatchedByPoeFree, tracker.Files);
			baseline = GameUpdateTracker.LoadBaseline(index)!;
			Assert.False(baseline.ReapplyPending);
			Assert.Equal(report, baseline.PendingReportFile);
			GameUpdateTracker.DismissReport(index);
			baseline = GameUpdateTracker.LoadBaseline(index)!;
			Assert.Null(baseline.PendingReportFile);
			Assert.Null(baseline.PendingDetectedUtc);
			Assert.Equal(updatedSha, baseline.IndexSha256);
			Assert.True(File.Exists(report)); // dismissing keeps the file

			// 6. Files changed by something else: nothing can be compared, nothing is reported.
			var foreign = File.ReadAllBytes(index);
			var state = RestoreService.LoadState(index)!;
			Assert.Equal(RestoreService.Sha256(foreign), state.PatchedSha256);
			GameArchive.WriteRawIndex(index, patched); // a patched index PoeFree did not record (made on the old version)
			Assert.Null(tracker.Poll());
			Assert.Null(tracker.Poll());
			Assert.Equal(WatchedFiles.ModifiedByOtherTool, tracker.Files);
			Assert.Equal(updatedSha, GameUpdateTracker.LoadBaseline(index)!.IndexSha256);

			// 7. A second update, seen before the game ran with it: the log still shows the old version.
			GameArchive.WriteRawIndex(index, vanilla);
			Assert.Null(tracker.Poll());
			update = tracker.Poll();
			Assert.NotNull(update);
			Assert.Equal((updatedSha, vanillaSha), (update.OldSha256, update.NewSha256));
			Assert.Equal(V2, update.OldVersion);
			Assert.Null(update.NewVersion);
			GameUpdateTracker.Accept(update, null);
			baseline = GameUpdateTracker.LoadBaseline(index)!;
			Assert.Null(baseline.Version);
			Assert.Null(baseline.PendingReportFile);
			// The version is filled in once the log shows a new one.
			Assert.Null(tracker.Poll(force: true));
			Assert.Null(GameUpdateTracker.LoadBaseline(index)!.Version);
			probe.Version = "https://patch.poecdn.com/3.30.0.2/";
			Assert.Null(tracker.Poll(force: true));
			Assert.Equal("https://patch.poecdn.com/3.30.0.2/", GameUpdateTracker.LoadBaseline(index)!.Version);

			// 8. An index that cannot be read (the game is writing it) is not an update.
			File.Move(index, index + ".away");
			Assert.Null(tracker.Poll());
			Assert.Equal(WatchedFiles.Unknown, tracker.Files);
			File.Move(index + ".away", index);
			Assert.Null(tracker.Poll());
			Assert.Equal(WatchedFiles.Original, tracker.Files);
		} finally {
			Directory.Delete(game, true);
			Directory.Delete(home, true);
		}
	}
}
