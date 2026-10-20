using PoeFree.Core.Text;

namespace PoeFree.Core.Patching.Patches;

/// <summary>Empties the <c>SoundEvents</c> component of animated objects in some folders.</summary>
public abstract class SilencePatch : Patch {
	static readonly string[] Components = ["SoundEvents"];
	public override PatchCategory Category => PatchCategory.Sound;
	public override IReadOnlyCollection<string> Extensions => [".ao", ".aoc"];

	public override string? Transform(string path, string text, PatchContext context) {
		var result = ScriptBlocks.EmptyBlocks(text, Components, out var n);
		return n > 0 ? result : null;
	}
}

public sealed class SilentMonstersPatch : SilencePatch {
	public override string Id => "sound.monsters";
	public override string Name => "Silent monsters";
	public override string Description => "Removes animation sounds of monsters (attacks, footsteps, death cries).";
	public override IEnumerable<string> Folders(PatchContext context) => ["metadata/monsters/"];
}

public sealed class SilentSkillsPatch : SilencePatch {
	public override string Id => "sound.skills";
	public override string Name => "Silent skills";
	public override string Description => "Removes sounds played by skill effect animations.";
	public override IEnumerable<string> Folders(PatchContext context) => ["metadata/effects/spells/", "metadata/effects/microtransactions/"];
}

public sealed class SilentPetsPatch : SilencePatch {
	public override string Id => "sound.pets";
	public override string Name => "Silent pets";
	public override string Description => "Removes sounds of cosmetic pets (e.g. the goblin band).";
	public override IEnumerable<string> Folders(PatchContext context) => ["metadata/pet/"];
}
