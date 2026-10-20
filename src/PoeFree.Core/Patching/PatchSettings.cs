using System.Text.Json;
using System.Text.Json.Serialization;
using PoeFree.Core.ColorMods;
using PoeFree.Core.Minimap;

namespace PoeFree.Core.Patching;

/// <summary>Everything that describes what to apply. Saved as a JSON profile.</summary>
public sealed class PatchSettings {
	/// <summary>Ids of enabled patches (see <see cref="PatchRegistry"/>).</summary>
	public HashSet<string> Enabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Camera zoom multiplier used by <c>camera.zoom</c>. The game default is 1.0; larger values zoom out.</summary>
	public double CameraZoom { get; set; } = 1.6;

	/// <summary>Effect folders (e.g. <c>metadata/effects/spells/fireball</c>) whose visuals are removed by <c>skills.disable</c>.</summary>
	public List<string> DisabledSkillEffects { get; set; } = [];

	/// <summary>Stat description lines to colour (used by <c>colormods</c>).</summary>
	public List<ColorModEntry> ColorMods { get; set; } = [];

	/// <summary>Custom minimap icons for objects (used by <c>minimap.markers</c>).</summary>
	public List<MinimapMarker> MinimapMarkers { get; set; } = [];

	/// <summary>Minimap icons per monster rarity (used by <c>minimap.rarity</c>).</summary>
	public RarityIconSettings RarityIcons { get; set; } = new();

	/// <summary>Restore original files automatically before applying (recommended).</summary>
	public bool RestoreBeforeApply { get; set; } = true;

	public bool IsEnabled(string id) => Enabled.Contains(id);

	public PatchSettings Set(string id, bool enabled) {
		if (enabled)
			Enabled.Add(id);
		else
			Enabled.Remove(id);
		return this;
	}

	public string ToJson() => JsonSerializer.Serialize(this, PoeFreeJsonContext.Default.PatchSettings);

	public static PatchSettings FromJson(string json) {
		var s = JsonSerializer.Deserialize(json, PoeFreeJsonContext.Default.PatchSettings) ?? new PatchSettings();
		s.Enabled = new HashSet<string>(s.Enabled, StringComparer.OrdinalIgnoreCase);
		return s;
	}

	public static PatchSettings Load(string file) => FromJson(File.ReadAllText(file));

	public void Save(string file) {
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
		File.WriteAllText(file, ToJson());
	}

	public PatchSettings Clone() => FromJson(ToJson());
}

[JsonSerializable(typeof(PatchSettings))]
[JsonSerializable(typeof(List<ColorModEntry>))]
[JsonSerializable(typeof(List<MinimapMarker>))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
public partial class PoeFreeJsonContext : JsonSerializerContext;
