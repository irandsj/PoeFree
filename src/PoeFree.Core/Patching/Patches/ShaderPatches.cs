using PoeFree.Core.Game;
using PoeFree.Core.Text;

namespace PoeFree.Core.Patching.Patches;

/// <summary>Shader edits. The client compiles HLSL from <c>shaders/</c> at runtime, so these take effect after the shader cache rebuilds.</summary>
public abstract class ShaderPatch : Patch {
	public override PatchCategory Category => PatchCategory.Performance;
	protected abstract string ShaderPath { get; }
	public override IEnumerable<string> Files(PatchContext context) => [ShaderPath];

	/// <summary>Replaces the body of the function whose signature starts with <paramref name="signatureStart"/>.</summary>
	protected static string? ReplaceFunctionBody(string text, string signatureStart, string newBody) {
		var at = text.IndexOf(signatureStart, StringComparison.Ordinal);
		if (at < 0)
			return null;
		var open = text.IndexOf('{', at);
		if (open < 0)
			return null;
		var close = ScriptBlocks.FindMatchingBrace(text, open);
		if (close < 0)
			return null;
		var nl = ScriptBlocks.DetectNewLine(text);
		var replacement = "{" + nl + string.Join(nl, newBody.Split('\n').Select(l => l.TrimEnd('\r'))) + nl + "}";
		var current = text[open..(close + 1)];
		return current == replacement ? null : text[..open] + replacement + text[(close + 1)..];
	}
}

public sealed class BloomPatch : ShaderPatch {
	public override string Id => "shader.bloom";
	public override string Name => "Remove bloom";
	public override string Description => "Disables the bloom glow post effect.";
	protected override string ShaderPath => "shaders/bloomgather.hlsl";
	public override string? Transform(string path, string text, PatchContext context) =>
		ReplaceFunctionBody(text, "float4 BloomGather(", "\treturn 0;");
}

public sealed class HeistFogPatch : ShaderPatch {
	public override string Id => "shader.heistfog";
	public override string Name => "Remove Heist blueprint fog";
	public override string Description => "Removes the animated fog drawn over Heist blueprints/maps UI.";
	public override bool Supports(GameVersion version) => version == GameVersion.Poe1;
	protected override string ShaderPath => "shaders/heistfogeffect.hlsl";
	public override string? Transform(string path, string text, PatchContext context) {
		// The entry point is the last function: find "PShad_HeistFogEffect(".
		return ReplaceFunctionBody(text, "PShad_HeistFogEffect(", "\treturn float4(0.0f, 0.0f, 0.0f, 0.0f);")
			?? ReplaceFunctionBody(text, "float4 PShad_HeistFogEffect", "\treturn float4(0.0f, 0.0f, 0.0f, 0.0f);");
	}
}

public sealed class TerrainBurnPatch : ShaderPatch {
	public override string Id => "shader.terrainburn";
	public override string Name => "Remove burning ground simulation";
	public override string Description => "Stops the compute shader that simulates burning/wind on grass and terrain.";
	protected override string ShaderPath => "shaders/terrainburn.hlsl";
	public override string? Transform(string path, string text, PatchContext context) {
		string? result = null;
		foreach (var entry in new[] { "void mainProgress(", "void mainClear(" }) {
			var r = ReplaceFunctionBody(result ?? text, entry, "");
			if (r is not null)
				result = r;
		}
		return result;
	}
}

/// <summary>Makes unexplored parts of the minimap faintly visible, like a permanent map reveal.</summary>
public sealed class MapRevealPatch : ShaderPatch {
	public override string Id => "map.reveal";
	public override PatchCategory Category => PatchCategory.Minimap;
	public override string Name => "Reveal map layout";
	public override string Description => "Shows the whole map layout faintly on the minimap from the start.";
	protected override string ShaderPath => "shaders/minimap_visibility_pixel.hlsl";
	const string Line = "res_color = max(res_color, 0.18f);";

	public override string? Transform(string path, string text, PatchContext context) {
		if (text.Contains(Line, StringComparison.Ordinal))
			return null;
		var nl = ScriptBlocks.DetectNewLine(text);
		foreach (var anchor in new[] { "\tif(visibility_walkable_revealed", "\treturn res_color;" }) {
			var at = text.IndexOf(anchor, StringComparison.Ordinal);
			if (at >= 0)
				return text.Insert(at, "\t" + Line + nl);
		}
		return null;
	}
}

/// <summary>Higher contrast walkable area colours on the minimap (dark floor, bright walls).</summary>
public sealed class MinimapContrastPatch : ShaderPatch {
	public override string Id => "map.contrast";
	public override PatchCategory Category => PatchCategory.Minimap;
	public override string Name => "High contrast minimap";
	public override string Description => "Draws walkable areas darker and edges brighter on the minimap so the layout is easier to read.";
	protected override string ShaderPath => "shaders/minimap_blending_pixel.hlsl";

	public override string? Transform(string path, string text, PatchContext context) {
		var r = text
			.Replace("float4 walkable_color = float4(1.0f, 1.0f, 1.0f, 0.01f);", "float4 walkable_color = float4(0.0f, 0.0f, 0.0f, 0.3f);")
			.Replace("lerp(walkable_color, float4(0.5f, 0.5f, 1.0f, 0.5f), walkable_to_edge_ratio)", "lerp(walkable_color, float4(12.0f, 12.0f, 12.0f, 0.1f), walkable_to_edge_ratio)");
		return r == text ? null : r;
	}
}
