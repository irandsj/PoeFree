using System.Text.RegularExpressions;
using PoeFree.Core.Archive;

namespace PoeFree.Core.ColorMods;

/// <summary>One <c>description</c> block of a stat description file.</summary>
public sealed class StatDescription {
	public required string File { get; init; }
	public required IReadOnlyList<string> StatIds { get; init; }
	/// <summary>English texts (without colour markup).</summary>
	public required IReadOnlyList<string> Lines { get; init; }
	/// <summary>Line range of the block inside the file (start inclusive, end exclusive).</summary>
	public int StartLine { get; init; }
	public int EndLine { get; init; }
}

/// <summary>
/// Parser for <c>metadata/statdescriptions/*.txt</c>. Format (simplified):
/// <code>
/// description
/// 	1 map_item_drop_quantity_+%
/// 	2
/// 		1|# # "{0}% increased Quantity of Items found in this Area"
/// 		#|-1 # "{0}% reduced Quantity of Items found in this Area" negate 1
/// 	lang "Portuguese"
/// 	...
/// </code>
/// </summary>
public static partial class StatDescriptions {
	/// <summary>PoE1 keeps them in metadata/statdescriptions/*.txt, PoE2 in data/statdescriptions/*.csd (same format).</summary>
	public static readonly string[] Folders = ["metadata/statdescriptions/", "data/statdescriptions/"];
	public static readonly string[] Extensions = [".txt", ".csd"];

	public static bool IsStatDescriptionPath(string path) =>
		Folders.Any(f => path.StartsWith(f, StringComparison.Ordinal)) && Extensions.Any(e => path.EndsWith(e, StringComparison.Ordinal));

	/// <summary>A translatable line: conditions, then the quoted text, then optional handlers.</summary>
	[GeneratedRegex("^(?<pre>\\s*(?!lang\\b)[^\\s\"][^\"]*?\\s)\"(?<text>(?:[^\"\\\\]|\\\\.)*)\"(?<post>.*)$")]
	public static partial Regex DataLineRegex();

	[GeneratedRegex("^<(?<tag>[A-Za-z0-9_]+)>\\{\\{(?<inner>.*)\\}\\}$", RegexOptions.Singleline)]
	public static partial Regex WrappedRegex();

	public static bool IsBlockStart(string line) =>
		line.StartsWith("description", StringComparison.Ordinal) || line.StartsWith("no_description", StringComparison.Ordinal)
		|| line.StartsWith("include", StringComparison.Ordinal) || line.StartsWith("no_identifiers", StringComparison.Ordinal)
		|| line.StartsWith("has_identifiers", StringComparison.Ordinal);

	public static string StripMarkup(string text) {
		var m = WrappedRegex().Match(text);
		return m.Success ? m.Groups["inner"].Value : text;
	}

	/// <summary>Parses all description blocks of one file.</summary>
	public static List<StatDescription> Parse(string file, string text) {
		var result = new List<StatDescription>();
		var lines = text.Split('\n');
		for (var i = 0; i < lines.Length; i++) {
			if (!lines[i].StartsWith("description", StringComparison.Ordinal))
				continue;
			var start = i;
			var end = i + 1;
			while (end < lines.Length && !IsBlockStart(lines[end]))
				end++;
			var ids = new List<string>();
			var english = new List<string>();
			if (start + 1 < lines.Length) {
				var idParts = lines[start + 1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
				if (idParts.Length > 1 && int.TryParse(idParts[0], out _))
					ids.AddRange(idParts.Skip(1));
			}
			for (var j = start + 2; j < end; j++) {
				var t = lines[j].TrimStart();
				if (t.StartsWith("lang", StringComparison.Ordinal))
					break;
				var m = DataLineRegex().Match(lines[j].TrimEnd('\r'));
				if (m.Success)
					english.Add(StripMarkup(m.Groups["text"].Value));
			}
			result.Add(new StatDescription { File = file, StatIds = ids, Lines = english, StartLine = start, EndLine = end });
			i = end - 1;
		}
		return result;
	}

	/// <summary>Loads every stat description of the game, for searching in the UI.</summary>
	public static List<StatDescription> LoadAll(GameArchive archive) {
		var all = new List<StatDescription>();
		var files = Folders.SelectMany(archive.PathsUnder).Where(IsStatDescriptionPath).ToList();
		archive.ReadMany(files, (path, bytes) => {
			var text = Text.PoeText.Decode(bytes.Span);
			all.AddRange(Parse(Path.GetFileName(path), text));
		});
		return all;
	}
}
