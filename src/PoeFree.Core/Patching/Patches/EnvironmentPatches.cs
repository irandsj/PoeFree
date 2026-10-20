using System.Text.RegularExpressions;

namespace PoeFree.Core.Patching.Patches;

/// <summary>
/// Area environment settings (<c>metadata/environmentsettings/*.env</c>) are JSON documents with one object per feature
/// (fog, area, post_processing, effect_spawner...). Renaming a key makes the game ignore that section and fall back to
/// "no effect". Renaming keeps the file valid JSON and is trivially reversible.
/// </summary>
public abstract class EnvKeyRenamePatch : Patch {
	/// <summary>JSON keys to disable.</summary>
	protected abstract string[] Keys { get; }

	public override PatchCategory Category => PatchCategory.Environment;
	public override IEnumerable<string> Folders(PatchContext context) => ["metadata/environmentsettings/"];
	public override IReadOnlyCollection<string> Extensions => [".env"];

	public override string? Transform(string path, string text, PatchContext context) => DisableKeys(text, Keys);

	public static string DisableKeys(string text, IEnumerable<string> keys) {
		foreach (var key in keys)
			text = Regex.Replace(text, "\"" + Regex.Escape(key) + "\"(\\s*:)", m => "\"x" + key[1..] + "\"" + m.Groups[1].Value);
		return text;
	}
}

public sealed class FogPatch : EnvKeyRenamePatch {
	public override string Id => "env.fog";
	public override string Name => "Remove fog";
	public override string Description => "Disables distance fog and screen space fog in every area.";
	protected override string[] Keys => ["fog", "screenspace_fog"];
}

public sealed class WeatherPatch : EnvKeyRenamePatch {
	public override string Id => "env.weather";
	public override string Name => "Remove area weather";
	public override string Description => "Disables the per-area 'area' section (rain, snow and other weather attached to the player).";
	protected override string[] Keys => ["area"];
}

public sealed class PostProcessingPatch : EnvKeyRenamePatch {
	public override string Id => "env.postprocessing";
	public override string Name => "Remove post-processing";
	public override string Description => "Disables area post-processing (colour grading, vignette, depth of field) defined by the environment.";
	protected override string[] Keys => ["post_processing"];
}

public sealed class EffectSpawnerPatch : EnvKeyRenamePatch {
	public override string Id => "env.effectspawner";
	public override string Name => "Remove ambient effect spawners";
	public override string Description => "Disables random ambient effects spawned by the environment (falling leaves, embers, sparks...).";
	protected override string[] Keys => ["effect_spawner"];
}

public sealed class DynamicLightsPatch : EnvKeyRenamePatch {
	public override string Id => "env.lights";
	public override string Name => "Remove dynamic lighting";
	public override string Description => "Disables sun light, player light, environment mapping and global illumination. Big FPS gain, flat look.";
	public override bool Experimental => true;
	protected override string[] Keys => ["directional_light", "player_light", "environment_mapping", "global_illumination"];
}

/// <summary>Turns off the shadow casting of the area's directional light.</summary>
public sealed partial class ShadowsPatch : Patch {
	public override string Id => "env.shadows";
	public override PatchCategory Category => PatchCategory.Environment;
	public override string Name => "Remove shadows";
	public override string Description => "Disables directional light shadows in every area.";
	public override IEnumerable<string> Folders(PatchContext context) => ["metadata/environmentsettings/"];
	public override IReadOnlyCollection<string> Extensions => [".env"];
	public override string? Transform(string path, string text, PatchContext context) => ShadowsRegex().Replace(text, "$1false");

	[GeneratedRegex("(\"shadows_enabled\"\\s*:\\s*)true")]
	private static partial Regex ShadowsRegex();
}
