using PoeFree.Core.ColorMods;
using PoeFree.Core.Minimap;
using PoeFree.Core.Patching;
using PoeFree.Core.Patching.Patches;
using PoeFree.Core.Text;

namespace PoeFree.Tests;

public class TextTransformTests {
	const string Ao = """
		version 3
		extends "Metadata/Parent"

		client
		{
			SkinMesh
			{
				skin = "Art/foo.sm"
			}

			ParticleEffects
			{
				animations = '[
					{
						"name": "loop",
						"events": [ { "type": "ParticleEffectEventType", "filename": "a.pet" } ]
					}
				]'
			}

			Lights
			{
			}
		}
		""";

	[Fact]
	public void EmptyBlocks_ignores_braces_inside_strings() {
		var result = ScriptBlocks.EmptyBlocks(Ao, ["ParticleEffects", "Lights"], out var n);
		Assert.Equal(1, n); // Lights is already empty
		Assert.Contains("ParticleEffects {}", result);
		Assert.DoesNotContain("a.pet", result);
		Assert.Contains("skin = \"Art/foo.sm\"", result);
		Assert.Equal(CountChar(result, '{'), CountChar(result, '}'));
	}

	[Fact]
	public void FindPath_finds_nested_block() {
		var b = ScriptBlocks.FindPath(Ao, "client/SkinMesh");
		Assert.NotNull(b);
		Assert.Contains("Art/foo.sm", b!.Value.Content(Ao));
		Assert.Null(ScriptBlocks.FindPath(Ao, "client/Nope"));
	}

	[Fact]
	public void Particles_are_zeroed() {
		Assert.Equal("0", Particles.ZeroPet("3\r\n{\r\n\tPointEmitter\r\n}"));
		Assert.Null(Particles.ZeroPet("0"));
		Assert.Equal("0\r\n{\r\n}", Particles.ZeroTrail("2\r\n{\r\n}"));
		Assert.Null(Particles.ZeroTrail("version 2\r\n"));
	}

	[Fact]
	public void Env_keys_are_renamed() {
		var env = "{\n  \"fog\": {\n    \"exp_fog_is_enabled\": true\n  },\n  \"area\": { \"ground_scale\": 1 },\n  \"directional_light\": { \"shadows_enabled\": true }\n}";
		var r = EnvKeyRenamePatch.DisableKeys(env, ["fog", "area"]);
		Assert.Contains("\"xog\":", r);
		Assert.Contains("\"xrea\":", r);
		Assert.Contains("exp_fog_is_enabled", r); // nested keys containing the word stay
		var shadows = new ShadowsPatch().Transform("x.env", env, null!);
		Assert.Contains("\"shadows_enabled\": false", shadows);
	}

	[Fact]
	public void Camera_calls_are_removed_safely() {
		var script = """
			Positioned
			{
				on_initial_position_set = { CreateCameraZoomNode(25.0, 30.0, 1.3); }
			}
			StateMachine
			{
				on_state_spin_1 =
				{
					Delay( 1.25, { ClearCameraZoomNodes(); } );
					Foo();
				}
				on_x = { Bar(); SetCustomCameraSpeed( 2.0 ); }
				IfState( spin, 0, { SetCustomCameraSpeed( 2.0 );
				} );
			}
			""";
		var r = CameraZoomPatch.RemoveCameraCalls(script);
		Assert.DoesNotContain("Camera", r);
		Assert.Contains("Foo();", r);
		Assert.Contains("on_x = { Bar();", r);
		Assert.Equal(CountChar(r, '{'), CountChar(r, '}'));
		Assert.Equal(CountChar(r, '('), CountChar(r, ')'));
	}

	[Fact]
	public void Player_zoom_is_added_once() {
		var ot = "version 3\r\n\r\nPositioned\r\n{\r\n\tteam = 1\r\n}\r\n";
		var r = CameraZoomPatch.AddPlayerZoom(ot, 2.4)!;
		Assert.Contains("CreateCameraZoomNode(5000.0, 5000.0, 2.4)", r);
		Assert.Null(CameraZoomPatch.AddPlayerZoom(r, 2.4));
		var r2 = CameraZoomPatch.AddPlayerZoom(r, 1.8)!;
		Assert.Contains("5000.0, 1.8)", r2);
		Assert.DoesNotContain("2.4", r2);
	}

	[Fact]
	public void Color_mods_wrap_all_translations() {
		var txt = string.Join("\r\n", [
			"description",
			"\t1 map_item_drop_quantity_+%",
			"\t2",
			"\t\t1|# # \"{0}% increased Quantity of Items found in this Area\"",
			"\t\t#|-1 # \"{0}% reduced Quantity of Items found in this Area\" negate 1",
			"\tlang \"Portuguese\"",
			"\t1",
			"\t\t1|# # \"Quantidade aumentada em {0}%\"",
			"",
			"description",
			"\t1 other_stat",
			"\t1",
			"\t\t# \"Other\"",
		]);
		var entry = ColorModEntry.Create("{0}% increased Quantity of Items found in this Area", 255, 155, 0);
		var r = ColorModsPatch.Apply("map_stat_descriptions.txt", txt, [entry], out var n);
		Assert.Equal(1, n);
		Assert.Contains("\"<poefreeff9b00>{{{0}% increased Quantity of Items found in this Area}}\"", r);
		Assert.Contains("\"<poefreeff9b00>{{{0}% reduced Quantity of Items found in this Area}}\" negate 1", r);
		Assert.Contains("\"<poefreeff9b00>{{Quantidade aumentada em {0}%}}\"", r);
		Assert.Contains("lang \"Portuguese\"", r);
		Assert.Contains("# \"Other\"", r);
		// Re-colouring replaces the tag instead of nesting it.
		var again = ColorModsPatch.Apply("map_stat_descriptions.txt", r, [ColorModEntry.Create(entry.Line, 1, 2, 3)], out _);
		Assert.Contains("\"<poefree010203>{{{0}% increased", again);
		Assert.DoesNotContain("ff9b00", again);
	}

	[Fact]
	public void Colours_are_registered_in_uisettings() {
		var xml = "<UISettings>\r\n\t<Props id=\"MarkupColours\">\r\n\t\t<Colour id=\"default\" value=\"127,127,127\"/>\r\n\t</Props>\r\n</UISettings>";
		var r = ColorModsPatch.RegisterColours(xml, [("poefreeff0000", 255, 0, 0)])!;
		Assert.Contains("<Colour id=\"poefreeff0000\" value=\"255,0,0\"/>", r);
		Assert.True(r.IndexOf("poefreeff0000", StringComparison.Ordinal) < r.IndexOf("\"default\"", StringComparison.Ordinal));
	}

	[Fact]
	public void Minimap_marker_injection_keeps_script_balanced() {
		var ot = "version 3\r\nextends \"Metadata/Chests/Chest\"\r\n\r\nclient {}\r\n";
		var m = new MinimapMarker { ObjectPath = "Metadata/Chests/Foo", Icon = 79, HideOnState = "activated" };
		var r = MinimapMarkersPatch.Inject(ot, m);
		Assert.Contains("SpawnObject( 'Metadata/MiscellaneousObjects/PoeFreeMarker_" + m.Key + "' )", r);
		Assert.Contains("IfState( activated, 1,", r);
		Assert.Contains("on_construction_complete", r);
		Assert.Contains("on_wake", r);
		Assert.Equal(CountChar(r, '{'), CountChar(r, '}'));
		Assert.Equal(r, MinimapMarkersPatch.Inject(r, m)); // idempotent
		Assert.Equal("metadata/chests/foo.ot", m.NormalizedPath);
	}

	[Fact]
	public void AppendToEvent_extends_existing_handlers() {
		var ot = "client\r\n{\r\n\tBaseEvents\r\n\t{\r\n\t\ton_wake = \"Foo();\"\r\n\t\ton_construction_complete =\r\n\t\t{\r\n\t\t\tBar();\r\n\t\t}\r\n\t}\r\n}\r\n";
		var r = ObjectScript.AppendToEvent(ot, "client/BaseEvents", "on_wake", "Baz();");
		Assert.Contains("on_wake = { Foo(); Baz(); }", r);
		r = ObjectScript.AppendToEvent(r, "client/BaseEvents", "on_construction_complete", "Qux();");
		Assert.Matches(@"Bar\(\);\s+Qux\(\);\s+\}", r);
		r = ObjectScript.AppendToEvent(r, "client/BaseEvents", "on_sleep", "Zzz();");
		Assert.Contains("on_sleep = { Zzz(); }", r);
		Assert.Equal(CountChar(r, '{'), CountChar(r, '}'));
	}

	[Fact]
	public void Ignore_list_parses_comments_and_wildcards() {
		var list = IgnoreList.Parse("# comment\n• bullet\nmetadata/monsters/boss\n  Metadata\\Effects\\Spells\\Fireball  \n# metadata/x\nmetadata/*/frost*\n");
		Assert.True(list.IsProtected("metadata/monsters/boss/boss.ot"));
		Assert.True(list.IsProtected("metadata/effects/spells/fireball/a.pet"));
		Assert.True(list.IsProtected("metadata/effects/frostbolt/a.pet"));
		Assert.False(list.IsProtected("metadata/x/y.ot"));
		Assert.Equal(0, IgnoreList.Parse(IgnoreList.DefaultText).Count);
	}

	[Fact]
	public void Settings_roundtrip_and_accept_pascal_case() {
		var s = new PatchSettings().Set("env.fog", true);
		s.ColorMods.Add(ColorModEntry.Create("x", 1, 2, 3));
		var back = PatchSettings.FromJson(s.ToJson());
		Assert.True(back.IsEnabled("ENV.FOG"));
		Assert.Equal(3, back.ColorMods[0].Blue);
		var legacy = PatchSettings.FromJson("{\"ColorMods\":[{\"Line\":\"a\",\"Red\":9,\"Green\":8,\"Blue\":7,\"FileName\":\"stat_descriptions.txt\",\"IsDisabled\":false,\"StatId\":\"\"}]}");
		Assert.Equal(9, legacy.ColorMods[0].Red);
	}

	[Fact]
	public void Text_encoding_roundtrips() {
		foreach (var kind in Enum.GetValues<TextEncodingKind>()) {
			var bytes = PoeText.Encode("version 3\r\nÁé", kind);
			Assert.Equal("version 3\r\nÁé", PoeText.Decode(bytes, out var detected));
			if (kind != TextEncodingKind.Utf8)
				Assert.Equal(kind, detected);
		}
	}

	[Fact]
	public void Registry_ids_are_unique() {
		var ids = PatchRegistry.All.Select(p => p.Id).ToList();
		Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
		foreach (var preset in PatchRegistry.Presets.Values)
			Assert.All(preset, id => Assert.NotNull(PatchRegistry.Get(id)));
	}

	static int CountChar(string s, char c) => s.Count(x => x == c);
}
