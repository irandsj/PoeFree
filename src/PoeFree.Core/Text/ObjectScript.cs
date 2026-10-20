using System.Text;
using System.Text.RegularExpressions;

namespace PoeFree.Core.Text;

/// <summary>Higher level edits of object scripts: add lines to (possibly missing) blocks and extend event handlers.</summary>
public static class ObjectScript {
	/// <summary>
	/// Adds <paramref name="lines"/> at the end of the block at <paramref name="path"/> (e.g. <c>client/StateMachine</c>),
	/// creating the missing blocks of the path.
	/// </summary>
	public static string AddLinesToBlock(string text, string path, IEnumerable<string> lines) {
		var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
		var lineList = lines.ToList();
		// Find the deepest existing block of the path.
		ScriptBlock? deepest = null;
		var depth = 0;
		for (; depth < parts.Length; depth++) {
			var b = ScriptBlocks.FindPath(text, string.Join('/', parts.Take(depth + 1)));
			if (b is null)
				break;
			deepest = b;
		}
		if (depth == parts.Length)
			return ScriptBlocks.InsertAtBlockEnd(text, deepest!.Value, lineList);

		// Build the missing nested blocks as lines.
		var built = new List<string>(lineList);
		for (var i = parts.Length - 1; i > depth; i--) {
			var wrapped = new List<string> { parts[i], "{" };
			wrapped.AddRange(built.Select(l => "\t" + l));
			wrapped.Add("}");
			built = wrapped;
		}
		if (deepest is null)
			return ScriptBlocks.AppendTopLevelBlock(text, parts[depth], built);
		var block = new List<string> { parts[depth], "{" };
		block.AddRange(built.Select(l => "\t" + l));
		block.Add("}");
		if (!string.IsNullOrWhiteSpace(deepest.Value.Content(text)))
			block.Insert(0, "");
		return ScriptBlocks.InsertAtBlockEnd(text, deepest.Value, block);
	}

	/// <summary>
	/// Appends <paramref name="statements"/> to the handler <paramref name="eventName"/> in the block at <paramref name="blockPath"/>.
	/// Creates the handler (and blocks) if missing. Handlers written as strings (<c>name = "..."</c>) are converted to brace form.
	/// </summary>
	public static string AppendToEvent(string text, string blockPath, string eventName, string statements) {
		var block = ScriptBlocks.FindPath(text, blockPath);
		if (block is null)
			return AddLinesToBlock(text, blockPath, [$"{eventName} = {{ {statements} }}"]);
		var b = block.Value;
		var region = text.Substring(b.ContentStart, b.ContentLength);
		var m = Regex.Match(region, @"(?m)^[ \t]*" + Regex.Escape(eventName) + @"[ \t]*=[ \t\r\n]*");
		if (!m.Success)
			return ScriptBlocks.InsertAtBlockEnd(text, b, [$"{eventName} = {{ {statements} }}"]);
		var valueAt = b.ContentStart + m.Index + m.Length;
		if (valueAt >= text.Length)
			return text;
		if (text[valueAt] == '{') {
			var close = ScriptBlocks.FindMatchingBrace(text, valueAt);
			if (close < 0)
				return text;
			var inner = text[(valueAt + 1)..close];
			if (inner.Contains('\n')) {
				var nl = ScriptBlocks.DetectNewLine(text);
				var indent = ScriptBlocks.IndentOf(text, close);
				var lineStart = text.LastIndexOf('\n', close - 1) + 1;
				if (string.IsNullOrWhiteSpace(text[lineStart..close]))
					return text.Insert(lineStart, indent + "\t" + statements + nl);
				return text.Insert(close, nl + indent + "\t" + statements + nl + indent);
			}
			return text.Insert(close, " " + statements + " ");
		}
		if (text[valueAt] == '"') {
			var endQuote = text.IndexOf('"', valueAt + 1);
			if (endQuote < 0)
				return text;
			var existing = text[(valueAt + 1)..endQuote];
			return text[..valueAt] + "{ " + existing + " " + statements + " }" + text[(endQuote + 1)..];
		}
		return text;
	}

	/// <summary>Builds a "version 3" object file.</summary>
	public static string NewObject(string extends, string body) {
		var sb = new StringBuilder();
		sb.Append("version 3\r\n");
		sb.Append("extends \"").Append(extends).Append("\"\r\n\r\n");
		sb.Append(body.Replace("\r\n", "\n").Replace("\n", "\r\n").TrimEnd()).Append("\r\n");
		return sb.ToString();
	}
}
