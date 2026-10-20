namespace PoeFree.Core.Patching.Patches;

/// <summary>Helpers to neutralise particle (.pet) and trail (.trl) definitions.</summary>
public static class Particles {
	/// <summary>
	/// A .pet file starts with the number of emitters followed by their definitions. A file containing only "0"
	/// is a valid particle effect with no emitters, so the effect still exists (no missing file errors) but draws nothing.
	/// </summary>
	public const string EmptyParticle = "0";

	public static string? ZeroPet(string text) => text.Trim() == EmptyParticle ? null : EmptyParticle;

	/// <summary>Trails use the same "count first" layout; replace only the leading count when it is a plain number.</summary>
	public static string? ZeroTrail(string text) {
		var body = text.TrimStart('﻿');
		var lineEnd = body.IndexOfAny(['\r', '\n']);
		if (lineEnd < 0)
			lineEnd = body.Length;
		if (!int.TryParse(body[..lineEnd].Trim(), out var count) || count == 0)
			return null;
		return EmptyParticle + body[lineEnd..];
	}

	public static string? Zero(string path, string text) =>
		path.EndsWith(".pet", StringComparison.Ordinal) ? ZeroPet(text)
		: path.EndsWith(".trl", StringComparison.Ordinal) ? ZeroTrail(text)
		: null;
}

/// <summary>Removes every particle effect in the game. Biggest FPS gain, but skills become hard to read.</summary>
public sealed class ZeroParticlesPatch : Patch {
	public override string Id => "particles.all";
	public override PatchCategory Category => PatchCategory.Performance;
	public override string Name => "Zero particles (all)";
	public override string Description => "Removes every particle effect (.pet/.trl). Maximum performance; protect skills you need to see with the ignore list.";
	public override IEnumerable<string> Folders(PatchContext context) => ["metadata/"];
	public override IReadOnlyCollection<string> Extensions => [".pet", ".trl"];
	public override string? Transform(string path, string text, PatchContext context) => Particles.Zero(path, text);
}

/// <summary>Removes ambient environment particles (dust, embers, fireflies, smoke columns...). Safe for gameplay.</summary>
public sealed class EnvironmentParticlesPatch : Patch {
	public override string Id => "particles.environment";
	public override PatchCategory Category => PatchCategory.Performance;
	public override string Name => "Remove environment particles";
	public override string Description => "Removes ambient particles of the maps (dust, embers, fireflies, smoke). Does not affect skills or monsters.";
	public override IEnumerable<string> Folders(PatchContext context) => ["metadata/particles/enviro_effects/", "metadata/particles/environment_effects/"];
	public override IReadOnlyCollection<string> Extensions => [".pet", ".trl"];
	public override string? Transform(string path, string text, PatchContext context) => Particles.Zero(path, text);
}
