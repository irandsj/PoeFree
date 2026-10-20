namespace PoeFree.Core.ColorMods;

/// <summary>
/// A stat description line to colour. Field names match the JSON used by other tools (Line/Red/Green/Blue/FileName/IsDisabled/StatId),
/// so existing colour mod lists can be imported directly.
/// </summary>
public sealed class ColorModEntry {
	/// <summary>English text of the line as written in the stat description file, e.g. <c>{0}% increased Quantity of Items found in this Area</c>.</summary>
	public string Line { get; set; } = "";
	public byte Red { get; set; } = 255;
	public byte Green { get; set; } = 255;
	public byte Blue { get; set; } = 255;
	/// <summary>Stat description file name (e.g. <c>map_stat_descriptions.txt</c>). Empty or <c>*</c> = any file.</summary>
	public string FileName { get; set; } = "";
	public bool IsDisabled { get; set; }
	/// <summary>Optional stat id to match instead of the text (e.g. <c>map_monsters_reflect_%_physical_damage</c>).</summary>
	public string StatId { get; set; } = "";

	public string Hex => $"{Red:X2}{Green:X2}{Blue:X2}";

	/// <summary>Markup colour id registered in uisettings.xml for this colour.</summary>
	public string ColourId => "poefree" + Hex.ToLowerInvariant();

	public bool MatchesFile(string fileName) =>
		string.IsNullOrWhiteSpace(FileName) || FileName == "*"
		|| Path.GetFileNameWithoutExtension(FileName).Equals(Path.GetFileNameWithoutExtension(fileName), StringComparison.OrdinalIgnoreCase); // .txt (PoE1) = .csd (PoE2)

	public bool Matches(StatDescription d) {
		if (!MatchesFile(d.File))
			return false;
		if (!string.IsNullOrWhiteSpace(StatId))
			return d.StatIds.Contains(StatId, StringComparer.OrdinalIgnoreCase);
		return d.Lines.Any(l => l.Equals(Line, StringComparison.OrdinalIgnoreCase));
	}

	public ColorModEntry Clone() => (ColorModEntry)MemberwiseClone();

	public static ColorModEntry Create(string line, byte r, byte g, byte b, string fileName = "", string statId = "") =>
		new() { Line = line, Red = r, Green = g, Blue = b, FileName = fileName, StatId = statId };
}
