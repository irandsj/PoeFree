using System.Text;
using PoeFree.Core.Patching;
using PoeFree.Core.Text;

namespace PoeFree.Core.ColorMods;

/// <summary>
/// Colours chosen map/item mod lines (e.g. reflect, no regen, extra currency) so they stand out in tooltips.
/// <para>
/// Each distinct colour is registered as a named markup colour in <c>metadata/ui/uisettings.xml</c> (<c>MarkupColours</c>),
/// then every translation of the matching stat description is wrapped as <c>"&lt;poefreeRRGGBB&gt;{{text}}"</c>.
/// </para>
/// </summary>
public sealed class ColorModsPatch : Patch {
	public const string UiSettingsPath = "metadata/ui/uisettings.xml";

	public override string Id => "colormods";
	public override PatchCategory Category => PatchCategory.Interface;
	public override string Name => "Color mods";
	public override string Description => "Colours the map/item mod lines configured in the Color Mods tab.";

	static IEnumerable<ColorModEntry> Active(PatchSettings s) => s.ColorMods.Where(c => !c.IsDisabled && (c.Line.Length > 0 || c.StatId.Length > 0));

	public override bool IsEnabled(PatchSettings settings) => settings.IsEnabled(Id) && Active(settings).Any();

	public override IEnumerable<string> Folders(PatchContext context) => StatDescriptions.Folders;
	public override IReadOnlyCollection<string> Extensions => StatDescriptions.Extensions;

	public override string? Transform(string path, string text, PatchContext context) {
		var entries = Active(context.Settings).ToList();
		var file = Path.GetFileName(path);
		if (!entries.Any(e => e.MatchesFile(file)))
			return null;
		return Apply(file, text, entries, out var n) is { } r && n > 0 ? r : null;
	}

	/// <summary>Colours the matching blocks of one stat description file. <paramref name="count"/> = blocks coloured.</summary>
	public static string Apply(string fileName, string text, IReadOnlyList<ColorModEntry> entries, out int count) {
		count = 0;
		var lines = text.Split('\n');
		foreach (var block in StatDescriptions.Parse(fileName, text)) {
			ColorModEntry? match = null;
			foreach (var e in entries)
				if (e.Matches(block))
					match = e; // later entries win
			if (match is null)
				continue;
			count++;
			for (var i = block.StartLine + 2; i < block.EndLine; i++)
				lines[i] = WrapLine(lines[i], match.ColourId);
		}
		return string.Join('\n', lines);
	}

	/// <summary>Wraps the quoted text of a data line with the colour tag (replacing a previous colour tag).</summary>
	public static string WrapLine(string line, string? colourId) {
		var cr = line.EndsWith('\r');
		var body = cr ? line[..^1] : line;
		var m = StatDescriptions.DataLineRegex().Match(body);
		if (!m.Success)
			return line;
		var inner = StatDescriptions.StripMarkup(m.Groups["text"].Value);
		var text = colourId is null ? inner : $"<{colourId}>{{{{{inner}}}}}";
		return m.Groups["pre"].Value + "\"" + text + "\"" + m.Groups["post"].Value + (cr ? "\r" : "");
	}

	public override void Generate(PatchContext context) {
		var colours = Active(context.Settings).Select(e => (e.ColourId, e.Red, e.Green, e.Blue)).Distinct().ToList();
		var xml = context.GetText(UiSettingsPath);
		if (xml is null) {
			context.Warning($"{UiSettingsPath} not found; colours will not be registered.");
			return;
		}
		var updated = RegisterColours(xml, colours);
		if (updated is null)
			context.Warning("MarkupColours section not found in uisettings.xml.");
		else
			context.SetText(UiSettingsPath, updated);
	}

	/// <summary>Adds <c>&lt;Colour id=... value="r,g,b"/&gt;</c> entries to the MarkupColours props.</summary>
	public static string? RegisterColours(string xml, IEnumerable<(string Id, byte R, byte G, byte B)> colours) {
		const string marker = "<Props id=\"MarkupColours\">";
		var at = xml.IndexOf(marker, StringComparison.Ordinal);
		if (at < 0)
			return null;
		var nl = ScriptBlocks.DetectNewLine(xml);
		var insertAt = xml.IndexOf('\n', at);
		insertAt = insertAt < 0 ? at + marker.Length : insertAt + 1;
		var sb = new StringBuilder();
		sb.Append("\t\t<!-- PoeFree colours -->").Append(nl);
		foreach (var (id, r, g, b) in colours) {
			if (xml.Contains($"id=\"{id}\"", StringComparison.Ordinal))
				continue;
			sb.Append($"\t\t<Colour id=\"{id}\" value=\"{r},{g},{b}\"/>").Append(nl);
		}
		return xml.Insert(insertAt, sb.ToString());
	}
}
