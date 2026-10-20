using PoeFree.Core.ColorMods;
using PoeFree.Core.Minimap;
using PoeFree.Core.Patching.Patches;

namespace PoeFree.Core.Patching;

/// <summary>All patches, in the order they run on each file.</summary>
public static class PatchRegistry {
	public static IReadOnlyList<Patch> All { get; } = [
		// Performance / particles
		new EnvironmentParticlesPatch(),
		new ZeroParticlesPatch(),
		// Environment (.env)
		new FogPatch(),
		new WeatherPatch(),
		new PostProcessingPatch(),
		new EffectSpawnerPatch(),
		new ShadowsPatch(),
		new DynamicLightsPatch(),
		// Effects
		new WeatherEffectsPatch(),
		new EnvironmentEffectsPatch(),
		new DeliriumPatch(),
		new MtxSoftPatch(),
		new MtxFullPatch(),
		new ShatterPatch(),
		new CorpsesPatch(),
		new SkillEffectsPatch(),
		// Shaders
		new BloomPatch(),
		new HeistFogPatch(),
		new TerrainBurnPatch(),
		// Sound
		new SilentMonstersPatch(),
		new SilentSkillsPatch(),
		new SilentPetsPatch(),
		// Camera
		new CameraZoomPatch(),
		// Minimap / UI
		new MapRevealPatch(),
		new MinimapContrastPatch(),
		new ColorModsPatch(),
		new MinimapMarkersPatch(),
		new RarityIconsPatch(),
	];

	public static Patch? Get(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

	/// <summary>Ready made selections.</summary>
	public static IReadOnlyDictionary<string, string[]> Presets { get; } = new Dictionary<string, string[]> {
		["performance"] = ["particles.environment", "env.fog", "env.weather", "env.effectspawner", "effects.weather", "effects.environment", "effects.shatter", "shader.bloom", "shader.terrainburn", "mtx.soft", "league.delirium"],
		["maximum"] = ["particles.all", "env.fog", "env.weather", "env.postprocessing", "env.effectspawner", "env.shadows", "env.lights", "effects.weather", "effects.environment", "effects.shatter", "monsters.corpses", "shader.bloom", "shader.terrainburn", "shader.heistfog", "mtx.full", "league.delirium"],
		["visibility"] = ["env.fog", "env.weather", "effects.weather", "league.delirium", "shader.bloom", "map.reveal", "map.contrast", "camera.zoom"],
	};
}
