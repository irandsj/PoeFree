using System.Text;

namespace PoeFree.Core.Text;

/// <summary>A named <c>Name { ... }</c> block found in a game object script (.ot/.otc/.ao/.aoc).</summary>
/// <param name="NameStart">Index of the first character of the block name.</param>
/// <param name="OpenBrace">Index of the opening <c>{</c>.</param>
/// <param name="CloseBrace">Index of the matching closing <c>}</c>.</param>
public readonly record struct ScriptBlock(string Name, int NameStart, int OpenBrace, int CloseBrace) {
	public int End => CloseBrace + 1;
	public int ContentStart => OpenBrace + 1;
	public int ContentLength => CloseBrace - OpenBrace - 1;
	public string Content(string text) => text.Substring(ContentStart, ContentLength);
}

/// <summary>
/// Minimal, string-aware parser for the brace-structured scripts used by Path of Exile object files.
/// It understands "double quoted" and 'single quoted' strings (which may contain JSON with braces),
/// <c>//</c> line comments and <c>/* */</c> block comments, so braces inside them are not counted.
/// </summary>
public static class ScriptBlocks {
	/// <summary>Finds the index of the brace matching the one at <paramref name="openBrace"/>, or -1.</summary>
	public static int FindMatchingBrace(string text, int openBrace) {
		var depth = 0;
		for (var i = openBrace; i < text.Length; i++) {
			var c = text[i];
			switch (c) {
				case '"':
				case '\'':
					i = SkipString(text, i);
					break;
				case '/' when i + 1 < text.Length && text[i + 1] == '/':
					i = SkipLine(text, i);
					break;
				case '/' when i + 1 < text.Length && text[i + 1] == '*':
					i = SkipBlockComment(text, i);
					break;
				case '{':
					depth++;
					break;
				case '}':
					if (--depth == 0)
						return i;
					break;
			}
		}
		return -1;
	}

	static int SkipString(string text, int start) {
		var quote = text[start];
		for (var i = start + 1; i < text.Length; i++) {
			if (text[i] == '\\') {
				i++;
				continue;
			}
			if (text[i] == quote)
				return i;
		}
		return text.Length - 1;
	}

	static int SkipLine(string text, int start) {
		var nl = text.IndexOf('\n', start);
		return nl < 0 ? text.Length - 1 : nl;
	}

	static int SkipBlockComment(string text, int start) {
		var end = text.IndexOf("*/", start + 2, StringComparison.Ordinal);
		return end < 0 ? text.Length - 1 : end + 1;
	}

	/// <summary>
	/// Enumerates every block called <paramref name="name"/> (case-insensitive) between <paramref name="from"/> and <paramref name="to"/>.
	/// Only matches the name when it is a whole word at the start of a statement and is followed by <c>{</c>.
	/// </summary>
	public static IEnumerable<ScriptBlock> Find(string text, string name, int from = 0, int to = -1) {
		if (to < 0 || to > text.Length)
			to = text.Length;
		for (var i = from; i < to; i++) {
			var c = text[i];
			if (c is '"' or '\'') {
				i = SkipString(text, i);
				continue;
			}
			if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') {
				i = SkipLine(text, i);
				continue;
			}
			if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') {
				i = SkipBlockComment(text, i);
				continue;
			}
			if (!IsWordStart(text, i) || string.Compare(text, i, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0)
				continue;
			var after = i + name.Length;
			if (after < text.Length && IsIdentChar(text[after]))
				continue;
			var j = after;
			while (j < to && char.IsWhiteSpace(text[j]))
				j++;
			if (j >= to || text[j] != '{')
				continue;
			var close = FindMatchingBrace(text, j);
			if (close < 0)
				yield break;
			yield return new ScriptBlock(text.Substring(i, name.Length), i, j, close);
			i = close;
		}
	}

	/// <summary>Finds a top level block (depth 0) by name.</summary>
	public static ScriptBlock? FindTopLevel(string text, string name) {
		foreach (var b in TopLevelBlocks(text))
			if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase))
				return b;
		return null;
	}

	/// <summary>Finds a block by slash separated path, e.g. <c>client/BaseEvents</c>. Each step looks at direct children only.</summary>
	public static ScriptBlock? FindPath(string text, string path) {
		var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
		int from = 0, to = text.Length;
		ScriptBlock? current = null;
		foreach (var part in parts) {
			current = null;
			foreach (var b in ChildBlocks(text, from, to))
				if (string.Equals(b.Name, part, StringComparison.OrdinalIgnoreCase)) {
					current = b;
					break;
				}
			if (current is null)
				return null;
			from = current.Value.ContentStart;
			to = current.Value.CloseBrace;
		}
		return current;
	}

	public static IEnumerable<ScriptBlock> TopLevelBlocks(string text) => ChildBlocks(text, 0, text.Length);

	/// <summary>Enumerates the direct child blocks of the region [from, to).</summary>
	public static IEnumerable<ScriptBlock> ChildBlocks(string text, int from, int to) {
		for (var i = from; i < to; i++) {
			var c = text[i];
			if (c is '"' or '\'') {
				i = SkipString(text, i);
				continue;
			}
			if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') {
				i = SkipLine(text, i);
				continue;
			}
			if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') {
				i = SkipBlockComment(text, i);
				continue;
			}
			if (c == '{') {
				// Anonymous or "x = {" style: skip the whole thing.
				var close = FindMatchingBrace(text, i);
				if (close < 0)
					yield break;
				i = close;
				continue;
			}
			if (!IsWordStart(text, i) || !IsIdentChar(c))
				continue;
			var end = i;
			while (end < to && IsIdentChar(text[end]))
				end++;
			var j = end;
			while (j < to && char.IsWhiteSpace(text[j]))
				j++;
			if (j < to && text[j] == '{') {
				var close = FindMatchingBrace(text, j);
				if (close < 0)
					yield break;
				yield return new ScriptBlock(text[i..end], i, j, close);
				i = close;
			} else {
				i = end - 1;
			}
		}
	}

	/// <summary>
	/// Replaces every block called one of <paramref name="names"/> with an empty <c>Name {}</c>.
	/// Returns the number of blocks emptied.
	/// </summary>
	public static string EmptyBlocks(string text, IReadOnlyCollection<string> names, out int count) {
		count = 0;
		var sb = new StringBuilder(text.Length);
		var last = 0;
		foreach (var block in AllBlocks(text)) {
			if (!names.Contains(block.Name, StringComparer.OrdinalIgnoreCase))
				continue;
			if (block.NameStart < last)
				continue; // nested inside a block we already emptied
			if (string.IsNullOrWhiteSpace(block.Content(text)))
				continue; // already empty
			sb.Append(text, last, block.NameStart - last);
			sb.Append(block.Name).Append(" {}");
			last = block.End;
			count++;
		}
		if (count == 0)
			return text;
		sb.Append(text, last, text.Length - last);
		return sb.ToString();
	}

	/// <summary>Enumerates all named blocks at any depth, outermost first, in document order.</summary>
	public static IEnumerable<ScriptBlock> AllBlocks(string text) {
		var stack = new Stack<(int From, int To)>();
		stack.Push((0, text.Length));
		var result = new List<ScriptBlock>();
		while (stack.Count > 0) {
			var (from, to) = stack.Pop();
			foreach (var b in ChildBlocks(text, from, to)) {
				result.Add(b);
				stack.Push((b.ContentStart, b.CloseBrace));
			}
		}
		result.Sort((a, b) => a.NameStart.CompareTo(b.NameStart));
		return result;
	}

	/// <summary>
	/// Inserts <paramref name="lines"/> right after the opening brace of <paramref name="block"/>, indented one level deeper than the block.
	/// An inline empty block (<c>Name {}</c>) is expanded to multiple lines.
	/// </summary>
	public static string InsertAtBlockStart(string text, ScriptBlock block, IEnumerable<string> lines) {
		var nl = DetectNewLine(text);
		var indent = IndentOf(text, block.NameStart);
		var inner = indent + "\t";
		var sb = new StringBuilder();
		foreach (var line in lines)
			sb.Append(inner).Append(line).Append(nl);
		var content = block.Content(text);
		if (string.IsNullOrWhiteSpace(content)) {
			// Rewrite as "Name\n{\n<lines>}\n"
			return text[..block.NameStart] + block.Name + nl + indent + "{" + nl + sb + indent + "}" + text[block.End..];
		}
		// Keep everything as is, add after "{" + newline.
		var insertAt = block.ContentStart;
		var lineEnd = text.IndexOf('\n', insertAt);
		if (lineEnd >= 0 && lineEnd < block.CloseBrace && string.IsNullOrWhiteSpace(text[insertAt..lineEnd]))
			insertAt = lineEnd + 1;
		else
			sb.Insert(0, nl);
		return text.Insert(insertAt, sb.ToString());
	}

	/// <summary>Inserts <paramref name="lines"/> right before the closing brace of <paramref name="block"/>.</summary>
	public static string InsertAtBlockEnd(string text, ScriptBlock block, IEnumerable<string> lines) {
		var content = block.Content(text);
		if (string.IsNullOrWhiteSpace(content))
			return InsertAtBlockStart(text, block, lines);
		var nl = DetectNewLine(text);
		var indent = IndentOf(text, block.NameStart);
		var inner = indent + "\t";
		var sb = new StringBuilder();
		// Find start of the line holding the closing brace.
		var lineStart = text.LastIndexOf('\n', block.CloseBrace - 1) + 1;
		var onOwnLine = string.IsNullOrWhiteSpace(text[lineStart..block.CloseBrace]);
		if (!onOwnLine)
			sb.Append(nl);
		foreach (var line in lines)
			sb.Append(inner).Append(line).Append(nl);
		var at = onOwnLine ? lineStart : block.CloseBrace;
		if (!onOwnLine)
			sb.Append(indent);
		return text.Insert(at, sb.ToString());
	}

	/// <summary>Appends a new top level block at the end of the file.</summary>
	public static string AppendTopLevelBlock(string text, string name, IEnumerable<string> lines) {
		var nl = DetectNewLine(text);
		var sb = new StringBuilder(text.TrimEnd());
		sb.Append(nl).Append(nl).Append(name).Append(nl).Append('{').Append(nl);
		foreach (var line in lines)
			sb.Append('\t').Append(line).Append(nl);
		sb.Append('}').Append(nl);
		return sb.ToString();
	}

	public static string DetectNewLine(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

	public static string IndentOf(string text, int index) {
		var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
		if (index == 0)
			lineStart = 0;
		var end = lineStart;
		while (end < index && (text[end] == '\t' || text[end] == ' '))
			end++;
		return text[lineStart..end];
	}

	static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

	static bool IsWordStart(string text, int i) => i == 0 || !IsIdentChar(text[i - 1]) && text[i - 1] != '.';
}
