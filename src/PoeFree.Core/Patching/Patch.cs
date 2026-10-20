using PoeFree.Core.Game;

namespace PoeFree.Core.Patching;

public enum PatchCategory {
	Performance,
	Environment,
	Effects,
	Sound,
	Camera,
	Minimap,
	Interface,
	Custom,
}

/// <summary>
/// One modification of the game files. A patch selects files (by folder prefix + extension, or explicit paths)
/// and transforms their text. Patches that need to create files or combine several files override <see cref="Generate"/>.
/// </summary>
public abstract class Patch {
	/// <summary>Stable identifier used in settings/profiles, e.g. <c>env.fog</c>.</summary>
	public abstract string Id { get; }
	public abstract PatchCategory Category { get; }
	/// <summary>Short English name. The UI may translate it using <see cref="Id"/>.</summary>
	public abstract string Name { get; }
	public abstract string Description { get; }

	/// <summary>Patches that are known to be risky or are not verified in game yet.</summary>
	public virtual bool Experimental => false;

	public virtual bool Supports(GameVersion version) => true;

	/// <summary>Whether this patch should run with the given settings. Default: its id is enabled.</summary>
	public virtual bool IsEnabled(PatchSettings settings) => settings.IsEnabled(Id);

	/// <summary>Folder prefixes scanned by <see cref="Transform"/>.</summary>
	public virtual IEnumerable<string> Folders(PatchContext context) => [];

	/// <summary>File extensions (with dot) of files under <see cref="Folders"/> to transform.</summary>
	public virtual IReadOnlyCollection<string> Extensions => [];

	/// <summary>Explicit files to transform.</summary>
	public virtual IEnumerable<string> Files(PatchContext context) => [];

	/// <summary>Returns the new text, or null to leave the file unchanged.</summary>
	public virtual string? Transform(string path, string text, PatchContext context) => null;

	/// <summary>Runs after all transforms. Can read/write/create arbitrary files through <paramref name="context"/>.</summary>
	public virtual void Generate(PatchContext context) { }

	public override string ToString() => Id;
}
