using LibBundle3;
using LibBundle3.Records;

namespace PoeFree.Core.Archive;

/// <summary>
/// Bundle factory for the Steam/Epic layout (loose <c>Bundles2/*.bundle.bin</c> files).
/// Original game bundles are opened read-only with full sharing, so the archive can be inspected while the game
/// (or its launcher) holds the files open. Only bundles PoeFree creates itself are opened for writing.
/// Optionally downloads missing bundles from the official patch CDN (used by the sandbox / test mode).
/// </summary>
public sealed class SteamBundleFactory : IBundleFactory, IDisposable {
	public string BaseDirectory { get; }
	readonly HttpClient? http;

	/// <param name="baseDirectory">The <c>Bundles2</c> folder.</param>
	/// <param name="patchCdnUrl">If set, bundles missing on disk are downloaded from this CDN (e.g. https://patch.poecdn.com/3.29.3.3/).</param>
	public SteamBundleFactory(string baseDirectory, string? patchCdnUrl = null) {
		BaseDirectory = Path.GetFullPath(baseDirectory);
		if (!BaseDirectory.EndsWith(Path.DirectorySeparatorChar))
			BaseDirectory += Path.DirectorySeparatorChar;
		if (patchCdnUrl is not null)
			http = new HttpClient { BaseAddress = new Uri(patchCdnUrl) };
	}

	public Bundle GetBundle(BundleRecord record) {
		var path = BaseDirectory + record.Path;
		if (!File.Exists(path) && http is not null)
			Download(record.Path, path);
		var writable = CustomBundles.IsCustom(record.Path);
		var stream = writable
			? File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)
			: File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		return new Bundle(stream, false, record);
	}

	public Stream CreateBundle(string bundlePath) {
		var path = BaseDirectory + bundlePath;
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		return File.Create(path);
	}

	public bool DeleteBundle(string bundlePath) {
		var path = BaseDirectory + bundlePath;
		if (!File.Exists(path))
			return false;
		File.Delete(path);
		return true;
	}

	void Download(string bundlePath, string target) {
		using var res = http!.GetAsync("Bundles2/" + bundlePath).GetAwaiter().GetResult();
		res.EnsureSuccessStatusCode();
		Directory.CreateDirectory(Path.GetDirectoryName(target)!);
		var tmp = target + ".part";
		using (var fs = File.Create(tmp))
			res.Content.ReadAsStream().CopyTo(fs);
		File.Move(tmp, target, true);
	}

	public void Dispose() => http?.Dispose();
}

/// <summary>Recognises bundles that were added by modding tools rather than shipped by the game.</summary>
public static class CustomBundles {
	/// <summary>LibGGPK3 (used by PoeFree, VisualGGPK3, SneedSmoother...) writes changes to <c>LibGGPK3/N.bundle.bin</c>.</summary>
	public const string LibGgpkPrefix = "LibGGPK3/";

	static readonly string[] KnownPrefixes = [LibGgpkPrefix, "PsCoreLib", "PoeFree"];

	public static bool IsCustom(string bundlePath) {
		foreach (var p in KnownPrefixes)
			if (bundlePath.StartsWith(p, StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}
}
