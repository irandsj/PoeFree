using PoeFree.Core;
using PoeFree.Core.Archive;
using PoeFree.Core.Patching;

namespace PoeFree.Tests;

/// <summary>
/// End to end tests against a real (copied) game index. They are skipped unless POEFREE_SANDBOX points to a
/// Steam-layout folder containing Bundles2/_.index.bin plus the bundles needed by the tested options, and Oodle is available
/// (POEFREE_OODLE or the usual locations). The tests never touch an installed game: everything runs on temporary copies.
/// </summary>
public class ArchiveIntegrationTests {
	static string? Sandbox => Environment.GetEnvironmentVariable("POEFREE_SANDBOX");

	static bool Ready(out string bundles2) {
		bundles2 = "";
		if (string.IsNullOrEmpty(Sandbox))
			return false;
		bundles2 = Path.Combine(Sandbox, "Bundles2");
		if (!File.Exists(Path.Combine(bundles2, "_.index.bin")))
			return false;
		OodleResolver.PreferredPath = Environment.GetEnvironmentVariable("POEFREE_OODLE");
		OodleResolver.Install();
		return OodleResolver.IsAvailable();
	}

	static string NewHome() {
		var home = Path.Combine(Path.GetTempPath(), "poefree-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(home);
		AppPaths.SetDataDirectory(home);
		return home;
	}

	static PatchSettings Options() {
		var s = new PatchSettings();
		s.Enabled.UnionWith(["env.fog", "shader.bloom", "camera.zoom", "minimap.rarity"]);
		s.CameraZoom = 2.2;
		return s;
	}

	static void AssertPatched(string archivePath) {
		using var a = GameArchive.Open(archivePath);
		Assert.True(a.IsModified);
		Assert.Contains("\"xog\"", a.ReadText("metadata/environmentsettings/1_1_1.env"));
		Assert.Contains("return 0;", a.ReadText("shaders/bloomgather.hlsl"));
		Assert.Contains("CreateCameraZoomNode(5000.0, 5000.0, 2.2)", a.ReadText("metadata/characters/character.ot"));
		// New files are resolvable by hash.
		Assert.Contains("EnemiesNearbyTracker", a.ReadText("metadata/miscellaneousobjects/poefreerarityraretracker.ot"));
	}

	[Fact]
	public async Task Steam_layout_apply_and_restore() {
		if (!Ready(out var source))
			return;
		NewHome();
		var dir = Path.Combine(Path.GetTempPath(), "poefree-steam-" + Guid.NewGuid().ToString("N"), "Bundles2");
		CopyDirectory(source, dir);
		var index = Path.Combine(dir, "_.index.bin");
		var original = RestoreService.Sha256(File.ReadAllBytes(index));
		try {
			var summary = await PatchEngine.ApplyAsync(index, Options(), new IgnoreList());
			Assert.True(summary.FilesChanged > 1000);
			Assert.Equal(6, summary.FilesAdded); // tracker + marker for magic, rare and unique
			AssertPatched(index);

			// Applying again restores first, so the result is the same.
			await PatchEngine.ApplyAsync(index, Options(), new IgnoreList());
			AssertPatched(index);

			var outcome = await RestoreService.RestoreAsync(index, Core.Game.GameVersion.Poe1);
			Assert.Equal(RestoreOutcome.RestoredFromBackup, outcome);
			Assert.Equal(original, RestoreService.Sha256(File.ReadAllBytes(index)));
			Assert.False(Directory.Exists(Path.Combine(dir, "LibGGPK3")) && Directory.EnumerateFiles(Path.Combine(dir, "LibGGPK3")).Any());
		} finally {
			Directory.Delete(Path.GetDirectoryName(dir)!, true);
		}
	}

	[Fact]
	public async Task Ggpk_layout_apply_and_restore() {
		if (!Ready(out var source))
			return;
		NewHome();
		var ggpkPath = Path.Combine(Path.GetTempPath(), "poefree-" + Guid.NewGuid().ToString("N") + ".ggpk");
		try {
			BuildGgpk(ggpkPath, source);
			var original = RestoreService.Sha256(GameArchive.ReadRawIndex(ggpkPath));

			var summary = await PatchEngine.ApplyAsync(ggpkPath, Options(), new IgnoreList());
			Assert.True(summary.FilesChanged > 1000);
			AssertPatched(ggpkPath);

			var outcome = await RestoreService.RestoreAsync(ggpkPath, Core.Game.GameVersion.Poe1);
			Assert.Equal(RestoreOutcome.RestoredFromBackup, outcome);
			Assert.Equal(original, RestoreService.Sha256(GameArchive.ReadRawIndex(ggpkPath)));
			using var g = new LibGGPK3.GGPK(ggpkPath);
			var bundles2 = (LibGGPK3.Records.DirectoryRecord)g.Root["Bundles2"]!;
			Assert.Null(bundles2["LibGGPK3"]);
		} finally {
			File.Delete(ggpkPath);
		}
	}

	/// <summary>Creates a minimal Content.ggpk (version 3) containing a Bundles2 folder copied from disk.</summary>
	static void BuildGgpk(string path, string bundles2) {
		using (var fs = File.Create(path)) {
			using var w = new BinaryWriter(fs);
			// GGPK record: length, tag, version, root offset, first free record offset
			w.Write(28u);
			w.Write(0x4B504747);
			w.Write(3u);
			w.Write(28L);
			w.Write(0L);
			// Root PDIR record: length, tag, name length (with '\0'), entries, sha256, name "\0" (UTF-16)
			w.Write(8u + 4 + 4 + 32 + 2);
			w.Write(0x52494450);
			w.Write(1);
			w.Write(0);
			w.Write(new byte[32]);
			w.Write((short)0);
		}
		using var ggpk = new LibGGPK3.GGPK(path);
		foreach (var file in Directory.EnumerateFiles(bundles2, "*", SearchOption.AllDirectories)) {
			var rel = Path.GetRelativePath(bundles2, file).Replace('\\', '/');
			if (rel.EndsWith(".part", StringComparison.Ordinal))
				continue;
			ggpk.Root.FindOrAddFile("Bundles2/" + rel, out var record);
			record.Write(File.ReadAllBytes(file));
		}
	}

	static void CopyDirectory(string from, string to) {
		foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) {
			var dest = Path.Combine(to, Path.GetRelativePath(from, file));
			Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
			File.Copy(file, dest);
		}
	}
}
