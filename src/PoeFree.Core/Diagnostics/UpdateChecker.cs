using System.Text.Json;

namespace PoeFree.Core.Diagnostics;

public sealed record UpdateInfo(Version Version, string Tag, string Url, string Notes);

/// <summary>
/// Looks for a newer PoeFree release on GitHub (anonymous request to the public releases API; nothing about the user is sent).
/// </summary>
public static class UpdateChecker {
	/// <summary>Repository that publishes the releases ("owner/name"). Forks can change it in settings.</summary>
	public const string DefaultRepository = "irandsj/PoeFree";

	public static Version CurrentVersion => Normalize(typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0));

	/// <summary>Parses tags such as <c>v1.2.3</c>, <c>1.2</c> or <c>v2.0.0-beta</c>.</summary>
	public static bool TryParseTag(string? tag, out Version version) {
		version = new Version(0, 0, 0);
		if (string.IsNullOrWhiteSpace(tag))
			return false;
		var t = tag.Trim().TrimStart('v', 'V');
		var cut = t.IndexOfAny(['-', '+', ' ']);
		if (cut >= 0)
			t = t[..cut];
		if (!Version.TryParse(t.Contains('.') ? t : t + ".0", out var v))
			return false;
		version = Normalize(v);
		return true;
	}

	static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

	public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

	/// <summary>Returns the latest release if it is newer than <paramref name="current"/>, otherwise null.</summary>
	public static async Task<UpdateInfo?> CheckAsync(string? repository = null, Version? current = null, CancellationToken ct = default) {
		repository = string.IsNullOrWhiteSpace(repository) ? DefaultRepository : repository.Trim();
		current ??= CurrentVersion;
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
		http.DefaultRequestHeaders.UserAgent.ParseAdd("PoeFree/" + current);
		http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		using var res = await http.GetAsync($"https://api.github.com/repos/{repository}/releases/latest", ct);
		if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
			return null; // no release published yet
		res.EnsureSuccessStatusCode();
		using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
		var root = doc.RootElement;
		var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
		if (!TryParseTag(tag, out var version) || !IsNewer(version, current))
			return null;
		var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : $"https://github.com/{repository}/releases/latest";
		var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
		return new UpdateInfo(version, tag!, url, notes);
	}
}
