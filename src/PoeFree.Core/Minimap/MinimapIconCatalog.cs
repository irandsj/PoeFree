using System.Buffers.Binary;
using System.Text;
using PoeFree.Core.Archive;

namespace PoeFree.Core.Minimap;

/// <summary>
/// Reads the names of the minimap icons from the game's MinimapIcons table (.datc64). The icon number used in
/// <c>MinimapIcon { icon = N }</c> is the row index of this table.
/// </summary>
public static class MinimapIconCatalog {
	static readonly string[] Candidates = [
		"data/minimapicons.datc64",
		"data/balance/minimapicons.datc64",
	];

	public sealed record Icon(int Index, string Name);

	public static List<Icon> Load(GameArchive archive) {
		foreach (var path in Candidates) {
			var data = archive.ReadBytes(path);
			if (data is null)
				continue;
			try {
				return ReadFirstStringColumn(data).Select((name, i) => new Icon(i, name)).ToList();
			} catch {
				// unknown layout
			}
		}
		return [];
	}

	static readonly byte[] Magic = [0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB];

	/// <summary>Reads the first column (assumed to be a string Id) of every row of a 64-bit .dat table.</summary>
	public static List<string> ReadFirstStringColumn(ReadOnlySpan<byte> data) {
		var rows = BinaryPrimitives.ReadInt32LittleEndian(data);
		var magicAt = data.IndexOf(Magic);
		if (rows <= 0 || magicAt < 4)
			throw new InvalidDataException("Not a .datc64 table");
		var rowSize = (magicAt - 4) / rows;
		var result = new List<string>(rows);
		for (var i = 0; i < rows; i++) {
			var offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[(4 + i * rowSize)..]);
			result.Add(ReadString(data, magicAt + offset));
		}
		return result;
	}

	static string ReadString(ReadOnlySpan<byte> data, long at) {
		if (at < 0 || at >= data.Length)
			return "";
		var start = (int)at;
		var end = start;
		while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0))
			end += 2;
		return Encoding.Unicode.GetString(data[start..end]);
	}
}
