using PoeFree.Core.Archive;
using PoeFree.Core.Diagnostics;
using PoeFree.Core.Patching;

namespace PoeFree.Tests;

public class DiagnosticsTests {
	[Theory]
	[InlineData("v1.2.3", "1.2.3")]
	[InlineData("1.2", "1.2.0")]
	[InlineData("V2.0.0-beta.1", "2.0.0")]
	[InlineData("v3", "3.0.0")]
	public void Tags_are_parsed(string tag, string expected) {
		Assert.True(UpdateChecker.TryParseTag(tag, out var v));
		Assert.Equal(Version.Parse(expected), v);
	}

	[Fact]
	public void Invalid_tags_are_rejected() {
		Assert.False(UpdateChecker.TryParseTag("latest", out _));
		Assert.False(UpdateChecker.TryParseTag("", out _));
	}

	[Fact]
	public void Newer_versions_are_detected() {
		Assert.True(UpdateChecker.IsNewer(new Version(1, 0, 1), new Version(1, 0, 0)));
		Assert.False(UpdateChecker.IsNewer(new Version(1, 0, 0), new Version(1, 0, 0, 0)));
		Assert.False(UpdateChecker.IsNewer(new Version(0, 9), new Version(1, 0, 0)));
	}

	[Fact]
	public void Health_is_evaluated() {
		Assert.Equal(HealthStatus.Ok, CompatibilityCheck.Evaluate("a", "a", 10, 3, []).Status);
		var noTargets = CompatibilityCheck.Evaluate("a", "a", 0, 0, []);
		Assert.Equal((HealthStatus.Broken, HealthReason.NoTargets), (noTargets.Status, noTargets.Reason));
		var noChanges = CompatibilityCheck.Evaluate("a", "a", 50, 0, []);
		Assert.Equal((HealthStatus.Broken, HealthReason.NoChanges), (noChanges.Status, noChanges.Reason));
		Assert.Equal(HealthStatus.Warning, CompatibilityCheck.Evaluate("a", "a", 10, 3, ["boom"]).Status);
		// Generator-only options (no file selection) are judged by what they produced.
		Assert.Equal(HealthStatus.Ok, CompatibilityCheck.Evaluate("a", "a", 0, 2, [], selectsFiles: false).Status);
		// An option that selects files but found none is broken even if a generator wrote something.
		Assert.Equal(HealthStatus.Broken, CompatibilityCheck.Evaluate("a", "a", 0, 1, [], selectsFiles: true).Status);
	}

	sealed class MovedFolderPatch : Patch {
		public override string Id => "test.moved";
		public override PatchCategory Category => PatchCategory.Custom;
		public override string Name => "moved";
		public override string Description => "";
		public override IEnumerable<string> Folders(PatchContext c) => ["metadata/this_folder_does_not_exist/"];
		public override IReadOnlyCollection<string> Extensions => [".env"];
		public override string? Transform(string path, string text, PatchContext c) => text + " ";
	}

	sealed class RenamedKeyPatch : Patch {
		public override string Id => "test.renamed";
		public override PatchCategory Category => PatchCategory.Custom;
		public override string Name => "renamed";
		public override string Description => "";
		public override IEnumerable<string> Folders(PatchContext c) => ["metadata/environmentsettings/"];
		public override IReadOnlyCollection<string> Extensions => [".env"];
		public override string? Transform(string path, string text, PatchContext c) =>
			text.Contains("\"a_key_that_does_not_exist\"") ? text.Replace("\"a_key_that_does_not_exist\"", "\"x\"") : null;
	}

	/// <summary>Simulates options broken by a game update against a real index (skipped without POEFREE_SANDBOX).</summary>
	[Fact]
	public void Broken_options_are_reported_on_real_files() {
		var sandbox = Environment.GetEnvironmentVariable("POEFREE_SANDBOX");
		var index = sandbox is null ? null : Path.Combine(sandbox, "Bundles2", "_.index.bin");
		if (index is null || !File.Exists(index))
			return;
		OodleResolver.PreferredPath = Environment.GetEnvironmentVariable("POEFREE_OODLE");
		OodleResolver.Install();
		if (!OodleResolver.IsAvailable())
			return;
		using var archive = GameArchive.Open(index);
		Patch[] patches = [new MovedFolderPatch(), new RenamedKeyPatch(), PatchRegistry.Get("env.fog")!];
		var ctx = PatchEngine.Prepare(archive, new PatchSettings(), new IgnoreList(), patches, isActive: _ => true, measureIndependently: true);
		var health = CompatibilityCheck.Evaluate(ctx).ToDictionary(h => h.Id);
		Assert.Equal(HealthReason.NoTargets, health["test.moved"].Reason);
		Assert.Equal(HealthReason.NoChanges, health["test.renamed"].Reason);
		Assert.True(health["test.renamed"].Targets > 100);
		Assert.Equal(HealthStatus.Ok, health["env.fog"].Status);
	}
}
