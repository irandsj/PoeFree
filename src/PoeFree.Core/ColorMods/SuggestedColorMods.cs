using PoeFree.Core.Game;

namespace PoeFree.Core.ColorMods;

/// <summary>A starter list of map mods worth highlighting (matched by stat id, so it works in every client language).</summary>
public static class SuggestedColorMods {
	static ColorModEntry Id(string statId, byte r, byte g, byte b) => new() { StatId = statId, Red = r, Green = g, Blue = b, Line = statId };

	public static List<ColorModEntry> For(GameVersion version) => version == GameVersion.Poe1 ? Poe1() : [];

	static List<ColorModEntry> Poe1() => [
		// Build breaking (red)
		Id("map_monsters_reflect_%_physical_damage", 255, 40, 40),
		Id("map_monsters_reflect_%_elemental_damage", 255, 40, 40),
		Id("map_player_no_regeneration", 255, 40, 40),
		Id("map_players_no_regeneration_including_es", 255, 40, 40),
		Id("map_player_life_and_es_recovery_speed_+%_final", 255, 40, 40),
		Id("map_monsters_cannot_be_leeched_from", 255, 40, 40),
		Id("map_monsters_unaffected_by_curses", 255, 110, 110),
		Id("map_monsters_reflect_curses", 255, 110, 110),
		// Annoying (orange)
		Id("map_monsters_avoid_ailments_%", 255, 155, 0),
		Id("map_monsters_avoid_elemental_ailments_%", 255, 155, 0),
		Id("map_monsters_steal_charges", 255, 155, 0),
		Id("map_player_has_random_level_X_curse_every_10_seconds", 255, 155, 0),
		Id("map_player_curse_effect_on_self_+%", 255, 155, 0),
		Id("map_players_cannot_gain_flask_charges", 255, 155, 0),
		Id("map_player_cooldown_speed_+%_final", 255, 155, 0),
		Id("map_players_have_point_blank", 255, 155, 0),
		Id("map_monsters_additional_elemental_resistance", 255, 155, 0),
		// Rewards (gold)
		Id("map_item_drop_quantity_+%", 255, 215, 0),
		Id("map_item_drop_rarity_+%", 255, 215, 0),
		Id("map_pack_size_+%", 255, 215, 0),
	];
}
