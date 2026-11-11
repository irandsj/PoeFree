using System.Text.Json;
using System.Text.Json.Serialization;
using PoeFree.Core;
using PoeFree.Core.Patching;

namespace PoeFree.App;

/// <summary>Application state persisted in %APPDATA%\PoeFree\settings.json.</summary>
public sealed class AppSettings {
	public string Language { get; set; } = "";
	public string? LastArchive { get; set; }
	public string? OodlePath { get; set; }
	public PatchSettings Patches { get; set; } = new();
	/// <summary>Look for new PoeFree releases on GitHub at start-up.</summary>
	public bool CheckUpdates { get; set; } = true;
	public string UpdateRepository { get; set; } = Core.Diagnostics.UpdateChecker.DefaultRepository;
	public DateTime? LastUpdateCheckUtc { get; set; }
	/// <summary>Release the user chose to ignore.</summary>
	public string? IgnoredUpdate { get; set; }
	/// <summary>While PoeFree is open, notice game updates (by looking at the game files) and write a report of what changed.</summary>
	public bool WatchGameUpdates { get; set; } = true;
	/// <summary>After a game update removed the applied options, apply them again once the game is closed.</summary>
	public bool AutoReapply { get; set; } = true;
	/// <summary>Closing the window keeps PoeFree running in the notification area.</summary>
	public bool KeepInTray { get; set; }
	/// <summary>Start with Windows, hidden in the notification area.</summary>
	public bool StartWithWindows { get; set; }

	/// <summary>Error raised while reading settings.json (shown in the log).</summary>
	public static string? LoadError { get; private set; }

	public static AppSettings Load() {
		try {
			if (File.Exists(AppPaths.SettingsFile)) {
				var s = JsonSerializer.Deserialize(File.ReadAllText(AppPaths.SettingsFile), AppJsonContext.Default.AppSettings);
				if (s is not null) {
					s.Patches = PatchSettings.FromJson(s.Patches.ToJson()); // normalise
					return s;
				}
			}
		} catch (Exception e) {
			LoadError = e.Message; // corrupted settings: start fresh
		}
		var fresh = new AppSettings();
		fresh.Patches.Enabled.UnionWith(PatchRegistry.Presets["performance"]);
		return fresh;
	}

	public void Save() {
		Directory.CreateDirectory(AppPaths.DataDirectory);
		File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, AppJsonContext.Default.AppSettings));
	}
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
internal partial class AppJsonContext : JsonSerializerContext;
