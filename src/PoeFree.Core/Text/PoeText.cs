using System.Text;

namespace PoeFree.Core.Text;

/// <summary>How a text file inside the game archive was encoded, so it can be written back identically.</summary>
public enum TextEncodingKind {
	Utf16LeBom,
	Utf16Le,
	Utf8Bom,
	Utf8,
}

/// <summary>
/// Game text files (.ot, .otc, .ao, .aoc, .env, .txt, .mat, ...) are mostly UTF-16LE with a BOM,
/// shaders (.hlsl) are plain ASCII/UTF-8. This helper detects and preserves the original encoding.
/// </summary>
public static class PoeText {
	static readonly UnicodeEncoding Utf16 = new(false, false);
	static readonly UTF8Encoding Utf8NoBom = new(false);

	public static string Decode(ReadOnlySpan<byte> data, out TextEncodingKind kind) {
		if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) {
			kind = TextEncodingKind.Utf16LeBom;
			return Utf16.GetString(data[2..]);
		}
		if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) {
			kind = TextEncodingKind.Utf8Bom;
			return Utf8NoBom.GetString(data[3..]);
		}
		if (LooksLikeUtf16(data)) {
			kind = TextEncodingKind.Utf16Le;
			return Utf16.GetString(data);
		}
		kind = TextEncodingKind.Utf8;
		return Utf8NoBom.GetString(data);
	}

	public static string Decode(ReadOnlySpan<byte> data) => Decode(data, out _);

	public static byte[] Encode(string text, TextEncodingKind kind) {
		switch (kind) {
			case TextEncodingKind.Utf16LeBom: {
				var bytes = new byte[2 + Utf16.GetByteCount(text)];
				bytes[0] = 0xFF;
				bytes[1] = 0xFE;
				Utf16.GetBytes(text, bytes.AsSpan(2));
				return bytes;
			}
			case TextEncodingKind.Utf16Le:
				return Utf16.GetBytes(text);
			case TextEncodingKind.Utf8Bom: {
				var bytes = new byte[3 + Utf8NoBom.GetByteCount(text)];
				bytes[0] = 0xEF;
				bytes[1] = 0xBB;
				bytes[2] = 0xBF;
				Utf8NoBom.GetBytes(text, bytes.AsSpan(3));
				return bytes;
			}
			default:
				return Utf8NoBom.GetBytes(text);
		}
	}

	/// <summary>Heuristic: UTF-16LE ASCII-range text has a zero in most odd positions.</summary>
	static bool LooksLikeUtf16(ReadOnlySpan<byte> data) {
		if (data.Length < 4 || data.Length % 2 != 0)
			return false;
		var sample = Math.Min(data.Length, 512);
		int zeros = 0, pairs = 0;
		for (var i = 1; i < sample; i += 2, pairs++)
			if (data[i] == 0)
				zeros++;
		return zeros > pairs * 0.6;
	}

	/// <summary>Whether the file at this path is a text format we know how to edit.</summary>
	public static bool IsTextPath(string path) {
		var ext = Path.GetExtension(path).ToLowerInvariant();
		return ext is ".ot" or ".otc" or ".ao" or ".aoc" or ".env" or ".pet" or ".trl" or ".txt" or ".xml" or ".mat"
			or ".hlsl" or ".fxgraph" or ".epk" or ".ui" or ".json" or ".csv" or ".amd" or ".arm" or ".tgr" or ".tgt"
			or ".dlp" or ".gft" or ".gt" or ".mtd" or ".sm" or ".ast" or ".atlas" or ".dct" or ".ddt" or ".ecf" or ".et"
			or ".filter" or ".fmt" or ".rs" or ".tsi" or ".tmo" or ".toy" or ".clt" or ".cht" or ".red" or ".pjd"
			or ".mel" or ".slt" or ".aoe" or ".act" or ".edp" or ".ffx" or ".h" or ".inc" or ".cfg";
	}
}
