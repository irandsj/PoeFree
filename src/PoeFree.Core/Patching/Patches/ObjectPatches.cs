using System.Globalization;
using System.Text.RegularExpressions;
using PoeFree.Core.Text;

namespace PoeFree.Core.Patching.Patches;

/// <summary>Monster corpses disappear (with their effects) as soon as monsters die; they reappear if revived.</summary>
public sealed class CorpsesPatch : Patch {
	public override string Id => "monsters.corpses";
	public override PatchCategory Category => PatchCategory.Performance;
	public override string Name => "Remove corpses";
	public override string Description => "Hides monster corpses and their effects on death (revived monsters become visible again). Not recommended for corpse builds.";
	public override IEnumerable<string> Files(PatchContext context) => ["metadata/monsters/monster.ot"];

	public override string? Transform(string path, string text, PatchContext context) {
		if (text.Contains("DisableRendering", StringComparison.Ordinal))
			return null;
		var life = ScriptBlocks.FindTopLevel(text, "Life");
		if (life is null)
			return null;
		text = ScriptBlocks.InsertAtBlockEnd(text, life.Value, [
			"on_spawned_dead = { RemoveEffects(); DisableRendering(); }",
			"on_death = { RemoveEffects(); DisableRendering(); }",
			"on_revive = { RemoveEffects(); EnableRendering(); }",
		]);
		// The Actor block owns the revive animation event.
		foreach (var block in ScriptBlocks.TopLevelBlocks(text)) {
			if (block.Content(text).Contains("slow_animations_go_to_idle", StringComparison.Ordinal)) {
				text = ScriptBlocks.InsertAtBlockEnd(text, block, ["on_start_Revive = { RemoveEffects(); EnableRendering(); }"]);
				break;
			}
		}
		return text;
	}
}

/// <summary>Frozen/shattered monster pieces and gore explosions are not rendered.</summary>
public sealed class ShatterPatch : Patch {
	public override string Id => "effects.shatter";
	public override PatchCategory Category => PatchCategory.Effects;
	public override string Name => "Remove shatter and gore";
	public override string Description => "Disables rendering of shatter (frozen kills) and gore explosion pieces.";
	public override IEnumerable<string> Folders(PatchContext context) => [
		"metadata/effects/spells/shatter/",
		"metadata/effects/spells/monsters_effects/gore_explode/",
	];
	public override IReadOnlyCollection<string> Extensions => [".ot", ".otc"];

	public override string? Transform(string path, string text, PatchContext context) {
		var render = ScriptBlocks.Find(text, "Render").FirstOrDefault();
		if (render == default || render.Content(text).Contains("disable_rendering", StringComparison.Ordinal))
			return null;
		return ScriptBlocks.InsertAtBlockEnd(text, render, ["disable_rendering = true"]);
	}
}

/// <summary>
/// Custom camera zoom: adds a huge camera zoom node around the player and removes the scripted zoom/pan changes
/// that bosses and special objects apply, so the zoom stays constant.
/// </summary>
public sealed partial class CameraZoomPatch : Patch {
	public override string Id => "camera.zoom";
	public override PatchCategory Category => PatchCategory.Camera;
	public override string Name => "Camera zoom";
	public override string Description => "Sets a custom camera zoom (1.0 = game default, higher = further away) and removes scripted zoom changes of bosses/objects.";

	public const double Min = 1.0;
	public const double Max = 3.0;

	/// <summary>Folders scanned for scripted camera changes.</summary>
	public override IEnumerable<string> Folders(PatchContext context) => [
		"metadata/characters/",
		"metadata/miscellaneousobjects/",
		"metadata/monsters/",
		"metadata/npc/",
		"metadata/questobjects/",
		"metadata/terrain/",
		"metadata/chests/",
		"metadata/effects/",
	];
	public override IReadOnlyCollection<string> Extensions => [".ot", ".otc"];

	public static string ZoomLine(double zoom) =>
		$"on_initial_position_set = {{ CreateCameraZoomNode(5000.0, 5000.0, {Math.Clamp(zoom, Min, Max).ToString("0.0#", CultureInfo.InvariantCulture)}); }}";

	public override string? Transform(string path, string text, PatchContext context) {
		var changed = false;
		if (text.Contains("Camera", StringComparison.Ordinal) && CameraCallRegex().IsMatch(text)) {
			text = RemoveCameraCalls(text);
			changed = true;
		}
		if (IsCharacterFile(path)) {
			var zoomed = AddPlayerZoom(text, context.Settings.CameraZoom);
			if (zoomed is not null) {
				text = zoomed;
				changed = true;
			}
		}
		return changed ? text : null;
	}

	static bool IsCharacterFile(string path) =>
		path == "metadata/characters/character.ot" || CharacterClassRegex().IsMatch(path);

	/// <summary>Adds (or updates) the player zoom node in the top level Positioned block.</summary>
	public static string? AddPlayerZoom(string text, double zoom) {
		var line = ZoomLine(zoom);
		var existing = OurZoomRegex().Match(text);
		if (existing.Success)
			return existing.Value == line ? null : text.Replace(existing.Value, line);
		var positioned = ScriptBlocks.FindTopLevel(text, "Positioned");
		if (positioned is null)
			return null;
		return ScriptBlocks.InsertAtBlockEnd(text, positioned.Value, [line]);
	}

	/// <summary>
	/// Removes camera zoom/pan/speed statements. A whole line is removed only when its braces and parentheses are balanced,
	/// otherwise just the call expression is removed, so the script structure always stays valid.
	/// </summary>
	public static string RemoveCameraCalls(string text) {
		var lines = text.Split('\n');
		var result = new List<string>(lines.Length);
		foreach (var line in lines) {
			if (!CameraCallRegex().IsMatch(line)) {
				result.Add(line);
				continue;
			}
			if (Balanced(line)) {
				// Dropping the line entirely, unless it is a "name = { ... }" handler containing other statements.
				var stripped = CameraCallStatementRegex().Replace(line, "");
				if (HandlerRegex().IsMatch(stripped) && !EmptyHandlerRegex().IsMatch(stripped))
					result.Add(stripped);
				continue;
			}
			result.Add(CameraCallStatementRegex().Replace(line, ""));
		}
		return string.Join('\n', result);
	}

	static bool Balanced(string line) {
		int braces = 0, parens = 0;
		foreach (var c in line) {
			switch (c) {
				case '{': braces++; break;
				case '}': braces--; break;
				case '(': parens++; break;
				case ')': parens--; break;
			}
		}
		return braces == 0 && parens == 0;
	}

	const string Calls = "CreateCameraZoomNode|ClearCameraZoomNodes|ClearCameraZoomNode|CreateCameraLookAtNode|CreateCameraPanNode|ClearCameraPanNode|ClearCameraPanNodes|CreateCameraRotationNode|ClearCameraRotationNode|RemoveCustomCameraSpeed|SetCustomCameraSpeed";

	[GeneratedRegex(@"\b(?:this\.|parent\.|camera_controller\.)?(?:" + Calls + @")\b")]
	private static partial Regex CameraCallRegex();

	[GeneratedRegex(@"\b(?:this\.|parent\.|camera_controller\.)?(?:" + Calls + @")\s*\((?:[^()]|\([^()]*\))*\)\s*;?")]
	private static partial Regex CameraCallStatementRegex();

	[GeneratedRegex(@"^\s*\w+\s*=\s*\{.*\}\s*$")]
	private static partial Regex HandlerRegex();

	[GeneratedRegex(@"^\s*\w+\s*=\s*\{\s*(?:/\*.*\*/)?\s*\}\s*$")]
	private static partial Regex EmptyHandlerRegex();

	[GeneratedRegex(@"on_initial_position_set = \{ CreateCameraZoomNode\(5000\.0, 5000\.0, [0-9.]+\); \}")]
	private static partial Regex OurZoomRegex();

	[GeneratedRegex(@"^metadata/characters/[a-z]+/[a-z]+\.ot$")]
	private static partial Regex CharacterClassRegex();
}
