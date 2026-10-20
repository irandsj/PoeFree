using System.Text.RegularExpressions;

namespace PoeFree.Core.Patching;

/// <summary>
/// Paths protected from every patch. One entry per line; a line protects a file or a whole folder (prefix match).
/// Lines starting with <c>#</c>, <c>//</c> or <c>•</c> are comments, so a line can be toggled by adding/removing <c>#</c>.
/// <c>*</c> can be used as a wildcard.
/// </summary>
public sealed class IgnoreList {
	readonly List<string> prefixes = [];
	readonly List<Regex> patterns = [];

	public IReadOnlyList<string> Prefixes => prefixes;
	public int Count => prefixes.Count + patterns.Count;

	public static IgnoreList Parse(string? text) {
		var list = new IgnoreList();
		if (string.IsNullOrEmpty(text))
			return list;
		foreach (var raw in text.Split('\n')) {
			var line = raw.Trim();
			if (line.Length == 0 || line[0] is '#' or '•' or ';' || line.StartsWith("//", StringComparison.Ordinal))
				continue;
			line = line.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
			if (line.Contains('*'))
				list.patterns.Add(new Regex("^" + Regex.Escape(line).Replace(@"\*", ".*"), RegexOptions.CultureInvariant));
			else
				list.prefixes.Add(line);
		}
		return list;
	}

	public bool IsProtected(string path) {
		foreach (var p in prefixes)
			if (path.StartsWith(p, StringComparison.Ordinal))
				return true;
		foreach (var r in patterns)
			if (r.IsMatch(path))
				return true;
		return false;
	}

	/// <summary>The default ignore list shipped with PoeFree (all entries commented out).</summary>
	public static string DefaultText => """
		# PoeFree ignore list
		# Paths listed here are never modified by any option. Use paths from the Explorer tab (Bundles2 paths).
		# A line protects a single file or a whole folder. Remove the leading # to activate a line, add it back to disable.
		# Linhas listadas aqui nunca sao modificadas. Remova o # do inicio para ativar a linha.

		# Keep rare monster mod effects visible / manter efeitos de mods de monstros raros
		# metadata/effects/spells/monsters_effects/monster_mods

		# Keep monster skill effects visible / manter efeitos de habilidades de monstros
		# metadata/effects/spells/monsters_effects

		# Keep Delirium fog visible / manter a nevoa do Delirium
		# metadata/effects/environment/league_affliction
		# metadata/effects/weather_attachments/league_delirium

		# Keep the Settlers/Deepwater sea visible / manter o mar visivel
		# metadata/effects/environment/league_deepwater/water

		# Keep the Simulacrum object original
		# metadata/terrain/leagues/affliction/objects/afflictionator.ot
		""";
}
