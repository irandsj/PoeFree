using PoeFree.Core.Text;

namespace PoeFree.Core.Patching.Patches;

/// <summary>
/// Removes the visuals of effect objects inside some folders:
/// particle files become empty and the rendering components of .ao/.aoc objects are emptied (<c>ParticleEffects {}</c>, <c>Lights {}</c>, ...).
/// The objects keep existing, so gameplay and scripts referencing them are unaffected.
/// </summary>
public abstract class EffectFolderPatch : Patch {
	public static readonly string[] VisualComponents = ["ParticleEffects", "Lights", "TrailsEffects"];
	public static readonly string[] MeshComponents = ["SkinMesh", "FixedMesh", "AnimatedRender", "WindEvents"];

	public override PatchCategory Category => PatchCategory.Effects;
	public override IReadOnlyCollection<string> Extensions => [".ao", ".aoc", ".pet", ".trl"];

	/// <summary>Components emptied in .ao/.aoc files.</summary>
	protected virtual IReadOnlyCollection<string> Components => VisualComponents;

	public override string? Transform(string path, string text, PatchContext context) {
		if (path.EndsWith(".pet", StringComparison.Ordinal) || path.EndsWith(".trl", StringComparison.Ordinal))
			return Particles.Zero(path, text);
		var result = ScriptBlocks.EmptyBlocks(text, Components, out var n);
		return n > 0 ? result : null;
	}
}

public sealed class WeatherEffectsPatch : EffectFolderPatch {
	public override string Id => "effects.weather";
	public override string Name => "Remove weather effects";
	public override string Description => "Removes rain, snow, ash, falling leaves and other weather attachments (meshes included).";
	protected override IReadOnlyCollection<string> Components => [.. VisualComponents, .. MeshComponents];
	public override IEnumerable<string> Folders(PatchContext context) => ["metadata/effects/weather_attachments/"];
}

public sealed class EnvironmentEffectsPatch : EffectFolderPatch {
	public override string Id => "effects.environment";
	public override string Name => "Remove environment effects";
	public override string Description => "Removes decorative environment effects (fog cards, waterfalls mist, torches glow, league ambience).";
	protected override IReadOnlyCollection<string> Components => [.. VisualComponents, .. MeshComponents];
	public override IEnumerable<string> Folders(PatchContext context) => ["metadata/effects/environment/"];
}

public sealed class MtxSoftPatch : EffectFolderPatch {
	public override string Id => "mtx.soft";
	public override string Name => "Remove MTX particles (soft)";
	public override string Description => "Removes particles and lights of microtransaction effects. Skill shapes/meshes stay visible.";
	public override IEnumerable<string> Folders(PatchContext context) => ["metadata/effects/microtransactions/", "metadata/particles/microtransactions/"];
	public override bool IsEnabled(PatchSettings settings) => settings.IsEnabled(Id) && !settings.IsEnabled("mtx.full");
}

public sealed class MtxFullPatch : EffectFolderPatch {
	public override string Id => "mtx.full";
	public override string Name => "Remove MTX effects (full)";
	public override string Description => "Removes microtransaction effect particles, lights and meshes (pets, auras, skill skins).";
	protected override IReadOnlyCollection<string> Components => [.. VisualComponents, .. MeshComponents];
	public override IEnumerable<string> Folders(PatchContext context) => ["metadata/effects/microtransactions/", "metadata/particles/microtransactions/"];
}

/// <summary>Removes visuals of the skill effect folders the user picked in the Skills tab.</summary>
public sealed class SkillEffectsPatch : EffectFolderPatch {
	public override string Id => "skills.disable";
	public override PatchCategory Category => PatchCategory.Custom;
	public override string Name => "Remove selected skill effects";
	public override string Description => "Removes particles and lights of the skills selected in the Skills tab.";
	public override bool IsEnabled(PatchSettings settings) => settings.DisabledSkillEffects.Count > 0;
	public override IEnumerable<string> Folders(PatchContext context) =>
		context.Settings.DisabledSkillEffects.Select(f => f.Replace('\\', '/').TrimEnd('/') + "/");
}

/// <summary>Delirium fog, its weather and the doodads that pop out of the ground.</summary>
public sealed class DeliriumPatch : EffectFolderPatch {
	public override string Id => "league.delirium";
	public override string Name => "Remove Delirium fog";
	public override string Description => "Removes the Delirium fog, its weather and the spawning doodads (spikes, eyes, blood bags).";
	protected override IReadOnlyCollection<string> Components => [.. VisualComponents, .. MeshComponents];
	public override IReadOnlyCollection<string> Extensions => [".ao", ".aoc", ".pet", ".trl", ".ot"];

	public override IEnumerable<string> Folders(PatchContext context) => [
		"metadata/effects/environment/league_affliction/",
		"metadata/effects/weather_attachments/league_delirium/",
		"metadata/monsters/leagueaffliction/doodaddaemons/",
	];

	public override string? Transform(string path, string text, PatchContext context) {
		if (!path.EndsWith(".ot", StringComparison.Ordinal))
			return base.Transform(path, text, context);
		// Doodads: drop the statements that make them visible when they spawn.
		var lines = text.Split('\n');
		var kept = lines.Where(l => {
			var t = l.Trim();
			return !(t.StartsWith("EnableRendering();", StringComparison.Ordinal)
				|| t.StartsWith("AddAttached(", StringComparison.Ordinal) && t.EndsWith(");", StringComparison.Ordinal)
				|| t.StartsWith("AddEffectPackAttached(", StringComparison.Ordinal) && t.EndsWith(");", StringComparison.Ordinal));
		}).ToArray();
		return kept.Length == lines.Length ? null : string.Join('\n', kept);
	}
}
