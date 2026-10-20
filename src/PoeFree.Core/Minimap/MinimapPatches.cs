using System.Security.Cryptography;
using System.Text;
using PoeFree.Core.Patching;
using PoeFree.Core.Text;

namespace PoeFree.Core.Minimap;

/// <summary>A custom minimap icon shown on top of every instance of an object (chest, shrine, strongbox, NPC...).</summary>
public sealed class MinimapMarker {
	/// <summary>Object template path, e.g. <c>metadata/chests/leaguedeepwater/goldtreasurechest.ot</c>.</summary>
	public string ObjectPath { get; set; } = "";
	/// <summary>Row of the minimap icon atlas (MinimapIcons.dat).</summary>
	public int Icon { get; set; }
	public bool Enabled { get; set; } = true;
	/// <summary>Optional object state that hides the icon when reached (e.g. <c>activated</c> for opened chests).</summary>
	public string HideOnState { get; set; } = "";
	public string Note { get; set; } = "";

	public string NormalizedPath {
		get {
			var p = ObjectPath.Trim().Replace('\\', '/').TrimStart('/').ToLowerInvariant();
			if (!p.EndsWith(".ot", StringComparison.Ordinal))
				p = Path.ChangeExtension(p, null) + ".ot";
			return p;
		}
	}

	/// <summary>Stable short id derived from the object path.</summary>
	public string Key => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(NormalizedPath)))[..12].ToLowerInvariant();
}

public sealed class RarityIconSettings {
	public bool Normal { get; set; }
	public bool Magic { get; set; } = true;
	public bool Rare { get; set; } = true;
	public bool Unique { get; set; } = true;
	public int NormalIcon { get; set; } = 196;
	public int MagicIcon { get; set; } = 48;
	public int RareIcon { get; set; } = 218;
	public int UniqueIcon { get; set; } = 201;
	/// <summary>Search radius (game units) of the enemy tracker.</summary>
	public int Radius { get; set; } = 320;

	public IEnumerable<(string Name, int Mask, int Icon)> Active() {
		if (Normal)
			yield return ("normal", 1, NormalIcon);
		if (Magic)
			yield return ("magic", 2, MagicIcon);
		if (Rare)
			yield return ("rare", 4, RareIcon);
		if (Unique)
			yield return ("unique", 8, UniqueIcon);
	}
}

/// <summary>Shared script snippets for the minimap features.</summary>
internal static class MarkerScripts {
	/// <summary>Variable set on objects that already have a custom marker, so the rarity markers skip them.</summary>
	public const string SkipVariable = "pf_skip_rarity_marker";

	/// <summary>An invisible object that only shows a minimap icon and deletes itself unless refreshed.</summary>
	public static string MarkerObject(int icon, bool selfExpire) {
		var sb = new StringBuilder();
		sb.Append("client\n{\n\tBaseEvents\n\t{\n");
		if (selfExpire)
			sb.Append("\t\ton_sleep = { HideMinimapIcon(); Delete(); }\n");
		sb.Append("\t\ton_delete = { HideMinimapIcon(); }\n\t}\n");
		if (selfExpire)
			sb.Append("\n\tStateMachine\n\t{\n\t\tdefine_state = \"pf_marker_expire\"\n\n\t\ton_timer_pf_marker_expire = { HideMinimapIcon(); Delete(); }\n\t}\n");
		sb.Append("}\n\nMinimapIcon\n{\n\ticon = ").Append(icon).Append("\n\tview_distance = \"9999\"\n}\n");
		return ObjectScript.NewObject("Metadata/MiscellaneousObjects/MissionMarker", sb.ToString());
	}
}

/// <summary>Adds custom minimap icons to objects chosen by the user (Minimap tab).</summary>
public sealed class MinimapMarkersPatch : Patch {
	public override string Id => "minimap.markers";
	public override PatchCategory Category => PatchCategory.Minimap;
	public override string Name => "Custom minimap icons";
	public override string Description => "Shows a minimap icon on every object configured in the Minimap tab (chests, shrines, NPCs...).";
	public override bool Experimental => true;

	static IEnumerable<MinimapMarker> Active(PatchSettings s) => s.MinimapMarkers.Where(m => m.Enabled && m.ObjectPath.Length > 0);

	public override bool IsEnabled(PatchSettings settings) => settings.IsEnabled(Id) && Active(settings).Any();

	public static string MarkerObjectPath(MinimapMarker m) => $"metadata/miscellaneousobjects/poefreemarker_{m.Key}.ot";
	static string MarkerSpawnName(MinimapMarker m) => $"Metadata/MiscellaneousObjects/PoeFreeMarker_{m.Key}";

	public override void Generate(PatchContext context) {
		foreach (var marker in Active(context.Settings)) {
			var target = marker.NormalizedPath;
			var text = context.GetText(target);
			if (text is null) {
				context.Warning($"object not found: {target}");
				continue;
			}
			if (context.IsProtected(target))
				continue;
			context.CreateFile(MarkerObjectPath(marker), MarkerScripts.MarkerObject(marker.Icon, true));
			context.SetText(target, Inject(text, marker));
		}
	}

	/// <summary>Adds the spawn/follow logic for <paramref name="m"/> to an object script.</summary>
	public static string Inject(string text, MinimapMarker m) {
		var v = $"pf_marker_{m.Key}";
		var follow = $"{v}_follow";
		if (text.Contains(follow, StringComparison.Ordinal))
			return text;
		var spawn = $"IfExists( this.{v}_done, {{}}, () {{ IfExists( this.{v}, {{}}, () {{ this.{v} = SpawnObject( '{MarkerSpawnName(m)}' ); this.{v}.SetLocation( this.GetLocation(), 1 ); }} ); this.{MarkerScripts.SkipVariable} = true; StartStateTimer( {follow}, 0.01 ); }} );";
		var keep = $"IfExists( this.{v}, () {{ this.{v}.SetLocation( this.GetLocation(), 1 ); this.{v}.StartStateTimer( pf_marker_expire, 2.0 ); }} ); StartStateTimer( {follow}, 0.01 );";
		var remove = $"this.{v}_done = true; IfExists( this.{v}, () {{ local marker = this.{v}; marker.HideMinimapIcon(); this.{v} = null; Delay( 0.05, {{ marker.Delete(); }} ); }} );";
		var timerBody = string.IsNullOrWhiteSpace(m.HideOnState)
			? keep
			: $"IfState( {m.HideOnState.Trim()}, 1, () {{ {remove} }}, () {{ {keep} }} );";

		text = ObjectScript.AddLinesToBlock(text, "client/StateMachine", [
			$"define_state = \"{follow}\"",
			$"on_timer_{follow} = {{ {timerBody} }}",
		]);
		text = ObjectScript.AppendToEvent(text, "client/BaseEvents", "on_construction_complete", spawn);
		text = ObjectScript.AppendToEvent(text, "client/BaseEvents", "on_wake", spawn);
		return text;
	}
}

/// <summary>
/// Minimap icons by monster rarity. The player spawns one invisible tracker per rarity that follows them; each tracker
/// uses the game's EnemiesNearbyTracker task (filtered by rarity) and attaches a marker object to every enemy in range.
/// </summary>
public sealed class RarityIconsPatch : Patch {
	public override string Id => "minimap.rarity";
	public override PatchCategory Category => PatchCategory.Minimap;
	public override string Name => "Minimap icons by monster rarity";
	public override string Description => "Shows minimap icons for magic, rare and unique monsters around you.";
	public override bool Experimental => true;

	public const string CharacterPath = "metadata/characters/character.ot";

	public override bool IsEnabled(PatchSettings settings) => settings.IsEnabled(Id) && settings.RarityIcons.Active().Any();

	static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];
	public static string TrackerPath(string rarity) => $"metadata/miscellaneousobjects/poefreerarity{rarity}tracker.ot";
	public static string MarkerPath(string rarity) => $"metadata/miscellaneousobjects/poefreerarity{rarity}marker.ot";

	public override void Generate(PatchContext context) {
		var character = context.GetText(CharacterPath);
		if (character is null) {
			context.Warning("character.ot not found.");
			return;
		}
		if (context.IsProtected(CharacterPath))
			return;
		var settings = context.Settings.RarityIcons;
		var followBody = new StringBuilder();
		var spawnBody = new StringBuilder();
		foreach (var (rarity, mask, icon) in settings.Active()) {
			context.CreateFile(MarkerPath(rarity), MarkerScripts.MarkerObject(icon, false));
			context.CreateFile(TrackerPath(rarity), Tracker(rarity, mask, settings.Radius));
			var t = $"pf_rarity_{rarity}_tracker";
			followBody.Append($"IfExists( this.{t}, () {{ this.{t}.SetLocation( this.GetLocation(), 1 ); }} ); ");
			spawnBody.Append($"IfExists( this.{t}, {{}}, () {{ this.{t} = SpawnObject( 'Metadata/MiscellaneousObjects/PoeFreeRarity{Cap(rarity)}Tracker' ); this.{t}.SetLocation( this.GetLocation(), 1 ); }} ); ");
		}
		if (character.Contains("pf_rarity_follow", StringComparison.Ordinal))
			return;
		character = ObjectScript.AddLinesToBlock(character, "client/StateMachine", [
			"define_state = \"pf_rarity_follow\"",
			$"on_timer_pf_rarity_follow = {{ {followBody}StartStateTimer( pf_rarity_follow, 0.10 ); }}",
		]);
		character = ObjectScript.AppendToEvent(character, "client/BaseEvents", "on_construction_complete",
			spawnBody + "StartStateTimer( pf_rarity_follow, 0.10 );");
		context.SetText(CharacterPath, character);
	}

	static string Tracker(string rarity, int mask, int radius) {
		var set = $"pf_{rarity}_in_range";
		var mk = $"pf_{rarity}_marker";
		var follow = $"pf_{rarity}_marker_follow";
		var spawn = $"Metadata/MiscellaneousObjects/PoeFreeRarity{Cap(rarity)}Marker";
		return ObjectScript.NewObject("Metadata/MiscellaneousObjects/Doodad", $$"""
			BaseEvents
			{
				client_only = true
				no_serialise = true
			}

			Positioned
			{
				object_size = 0
				blocking = false
				team = 1
			}

			client
			{
				StateMachine
				{
					define_state = "{{follow}}"

					on_timer_{{follow}} =
					{
						IfGroupSize( this.{{set}}, >0, ()
						{
							this.{{set}}.Do( ( enemy )
							{
								IfExists( enemy.{{mk}}, ()
								{
									IfExists( enemy.{{MarkerScripts.SkipVariable}}, ()
									{
										enemy.{{mk}}.Delete();
										enemy.{{mk}} = null;
										this.{{set}} -= enemy;
									}, ()
									{
										enemy.{{mk}}.SetLocation( enemy.GetLocation(), 1 );
									} );
								} );
							} );
							StartStateTimer( {{follow}}, 0.01 );
						} );
					}
				}

				BaseEvents
				{
					on_construction_complete =
					{
						AddNamedTask( EnemiesNearbyTracker,
							250,
							{{radius}},
							{{mask}},
							1,
							250
						);
					}
				}

				Functions
				{
					on_enemy_entered_range =
					{
						local enemy = target;
						IfExists( enemy.{{MarkerScripts.SkipVariable}}, {}, ()
						{
							this.{{set}} += enemy;
							IfExists( enemy.{{mk}}, {}, ()
							{
								enemy.{{mk}} = SpawnObject( '{{spawn}}' );
								enemy.{{mk}}.SetLocation( enemy.GetLocation(), 1 );
							} );
							StartStateTimer( {{follow}}, 0.01 );
						} );
					}

					on_enemy_exited_range =
					{
						local enemy = target;
						this.{{set}} -= enemy;
						IfExists( enemy.{{mk}}, ()
						{
							enemy.{{mk}}.Delete();
							enemy.{{mk}} = null;
						} );
					}
				}
			}
			""");
	}
}
