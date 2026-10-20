using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoeFree.Core;
using PoeFree.Core.Archive;
using PoeFree.Core.ColorMods;
using PoeFree.Core.Game;
using PoeFree.Core.Minimap;
using PoeFree.Core.Patching;
using PoeFree.Core.Text;

namespace PoeFree.App.ViewModels;

/// <summary>UI services implemented by the window.</summary>
public interface IDialogs {
	Task<bool> Confirm(string message);
	Task Info(string message);
	Task Error(string message);
	Task<string?> OpenFile(string title, string filterName, params string[] patterns);
	Task<string?> SaveFile(string title, string suggestedName, string filterName, params string[] patterns);
	Task<string?> PickFolder(string title);
	Task SetClipboard(string text);
	void RestartUi();
}

public sealed partial class MainViewModel : ObservableObject {
	readonly AppSettings app;
	readonly IDialogs dialogs;
	GameArchive? archive;
	readonly SemaphoreSlim archiveLock = new(1, 1);
	PatchSettings Settings => app.Patches;

	public MainViewModel(AppSettings app, IDialogs dialogs) {
		this.app = app;
		this.dialogs = dialogs;
		cameraZoom = Settings.CameraZoom;
		restoreBeforeApply = Settings.RestoreBeforeApply;
		ignoreText = File.Exists(AppPaths.IgnoreListFile) ? File.ReadAllText(AppPaths.IgnoreListFile) : IgnoreList.DefaultText;
		statusText = Loc.T("status.noGame");
		selectedLanguage = Loc.Instance.Language;
		pickColor = Color.FromRgb(255, 60, 60);

		OptionGroups = new(PatchRegistry.All
			.Where(p => p.Id != "skills.disable")
			.GroupBy(p => p.Category)
			.Select(g => new OptionGroup(Loc.Category(g.Key), g.Select(p => new OptionItem(p, Settings)).ToList())));
		foreach (var c in Settings.ColorMods)
			ColorMods.Add(new ColorModItem(c));
		foreach (var m in Settings.MinimapMarkers)
			Markers.Add(new MarkerItem(m));
		OodleMissing = !OodleResolver.IsAvailable();
	}

	public string Title => "PoeFree " + typeof(MainViewModel).Assembly.GetName().Version?.ToString(3);
	public Loc L => Loc.Instance;
	public string[] Languages => Loc.Languages;
	public string AboutText => Loc.T("about.text");

	// ---------------------------------------------------------------- state

	public ObservableCollection<GameInstall> Installs { get; } = [];
	public ObservableCollection<OptionGroup> OptionGroups { get; }

	[ObservableProperty] private GameInstall? selectedInstall;
	[ObservableProperty] private string? archivePath;
	[ObservableProperty] private string statusText;
	[ObservableProperty] private bool isModified;
	[ObservableProperty] private bool isBusy;
	[ObservableProperty] private double progress;
	[ObservableProperty] private bool oodleMissing;
	[ObservableProperty] private string logText = "";
	[ObservableProperty] private double cameraZoom;
	[ObservableProperty] private bool restoreBeforeApply;
	[ObservableProperty] private string selectedLanguage;
	[ObservableProperty] private int selectedTab;

	partial void OnCameraZoomChanged(double value) => Settings.CameraZoom = Math.Round(value, 2);
	partial void OnRestoreBeforeApplyChanged(bool value) => Settings.RestoreBeforeApply = value;

	partial void OnSelectedInstallChanged(GameInstall? value) {
		if (value is not null && value.ArchivePath != ArchivePath)
			_ = OpenArchiveAsync(value.ArchivePath);
	}

	partial void OnSelectedLanguageChanged(string value) {
		if (value == Loc.Instance.Language)
			return;
		app.Language = value;
		Loc.Instance.SetLanguage(value);
		SaveAll();
		dialogs.RestartUi();
	}

	void Log(string line) {
		var text = $"[{DateTime.Now:HH:mm:ss}] {line}";
		if (Dispatcher.UIThread.CheckAccess())
			LogText += text + Environment.NewLine;
		else
			Dispatcher.UIThread.Post(() => LogText += text + Environment.NewLine);
		try {
			Directory.CreateDirectory(AppPaths.LogsDirectory);
			File.AppendAllText(Path.Combine(AppPaths.LogsDirectory, $"poefree-{DateTime.Now:yyyyMMdd}.log"), text + Environment.NewLine);
		} catch {
			// logging must never fail the app
		}
	}

	// ---------------------------------------------------------------- startup / archive

	public async Task InitializeAsync() {
		Log(Title + " - " + Loc.T("app.subtitle"));
		if (AppSettings.LoadError is not null)
			Log("settings.json could not be read, defaults are used: " + AppSettings.LoadError);
		if (OodleMissing)
			Log(Loc.T("oodle.missing"));
		else if (OodleResolver.IsAvailable())
			Log("Oodle: " + OodleResolver.LoadedPath);
		_ = CheckUpdatesAtStartupAsync();
		var installs = await Task.Run(GameDetector.FindInstalls);
		foreach (var i in installs) {
			Installs.Add(i);
			Log("Found: " + i.DisplayName);
		}
		var last = app.LastArchive is not null && File.Exists(app.LastArchive) ? app.LastArchive : installs.FirstOrDefault()?.ArchivePath;
		if (last is not null) {
			var match = Installs.FirstOrDefault(i => i.ArchivePath.Equals(last, StringComparison.OrdinalIgnoreCase));
			if (match is null) {
				match = new GameInstall(GameDetector.GuessVersion(last), GameDetector.GuessPlatform(last), last);
				Installs.Add(match);
			}
			SelectedInstall = match;
		}
	}

	[RelayCommand]
	async Task Browse() {
		var path = await dialogs.OpenFile(Loc.T("status.noGame"), "Content.ggpk / _.index.bin", "*.ggpk", "*.bin");
		if (path is null)
			return;
		var install = new GameInstall(GameDetector.GuessVersion(path), GameDetector.GuessPlatform(path), path);
		Installs.Add(install);
		SelectedInstall = install;
	}

	[RelayCommand]
	async Task Detect() {
		var installs = await Task.Run(GameDetector.FindInstalls);
		foreach (var i in installs)
			if (!Installs.Any(x => x.ArchivePath.Equals(i.ArchivePath, StringComparison.OrdinalIgnoreCase)))
				Installs.Add(i);
	}

	async Task OpenArchiveAsync(string path) {
		if (OodleMissing) {
			StatusText = Loc.T("oodle.missing");
			ArchivePath = path;
			return;
		}
		IsBusy = true;
		StatusText = Loc.T("status.loading");
		await CloseArchiveAsync();
		ArchivePath = path;
		app.LastArchive = path;
		await archiveLock.WaitAsync();
		try {
			archive = await Task.Run(() => GameArchive.Open(path, readOnly: true));
			UpdateStatus();
			Log($"Opened {path} ({archive.Version}, {archive.Index.Files.Count:N0} files)");
			LoadSkills();
			stats = null;
			StatResults.Clear();
			_ = LoadIconsAsync();
			_ = AfterArchiveOpenedAsync(path);
		} catch (Exception e) {
			StatusText = e.Message;
			Log("Open failed: " + e);
		} finally {
			archiveLock.Release();
			IsBusy = false;
		}
	}

	async Task CloseArchiveAsync() {
		await archiveLock.WaitAsync();
		try {
			archive?.Dispose();
			archive = null;
		} finally {
			archiveLock.Release();
		}
	}

	void UpdateStatus() {
		if (archive is null)
			return;
		var custom = archive.CustomBundleNames().ToList();
		IsModified = custom.Count > 0;
		var state = IsModified ? Loc.F("status.modified", string.Join(", ", custom.Take(3))) : Loc.T("status.original");
		StatusText = Loc.F("status.loaded", archive.Version == GameVersion.Poe1 ? "Path of Exile 1" : "Path of Exile 2", archive.Index.Files.Count, state);
		foreach (var g in OptionGroups)
			foreach (var o in g.Items)
				o.IsSupported = o.Patch.Supports(archive.Version);
	}

	// ---------------------------------------------------------------- oodle

	[RelayCommand]
	async Task DownloadOodle() {
		IsBusy = true;
		try {
			var path = await OodleResolver.DownloadAsync(new Progress<string>(Log));
			app.OodlePath = path;
			await OodleReady();
		} catch (Exception e) {
			await dialogs.Error(e.Message);
		} finally {
			IsBusy = false;
		}
	}

	[RelayCommand]
	async Task LocateOodle() {
		var file = await dialogs.OpenFile(Loc.T("oodle.locate"), "oo2core", "oo2core*.dll", "liboo2core*");
		if (file is null)
			return;
		try {
			app.OodlePath = OodleResolver.ImportLibrary(file);
			await OodleReady();
		} catch (Exception e) {
			await dialogs.Error(e.Message);
		}
	}

	async Task OodleReady() {
		SaveAll();
		if (!OodleResolver.IsAvailable()) {
			// The resolver caches the first failure inside .NET: a restart is needed in that case.
			await dialogs.Info("oo2core saved. Please restart PoeFree.");
			return;
		}
		OodleMissing = false;
		Log("Oodle: " + OodleResolver.LoadedPath);
		if (ArchivePath is not null)
			await OpenArchiveAsync(ArchivePath);
	}

	// ---------------------------------------------------------------- options / presets / profiles

	[RelayCommand]
	void Preset(string name) {
		Settings.Enabled.Clear();
		if (PatchRegistry.Presets.TryGetValue(name, out var ids))
			Settings.Enabled.UnionWith(ids);
		RefreshOptions();
	}

	void RefreshOptions() {
		foreach (var g in OptionGroups)
			foreach (var o in g.Items)
				o.Refresh();
		CameraZoom = Settings.CameraZoom;
		RestoreBeforeApply = Settings.RestoreBeforeApply;
	}

	[RelayCommand]
	async Task SaveProfile() {
		SyncSettings();
		var file = await dialogs.SaveFile(Loc.T("profile.save"), "poefree-profile.json", "PoeFree profile", "*.json");
		if (file is null)
			return;
		Settings.Save(file);
		Log("Profile saved: " + file);
	}

	[RelayCommand]
	async Task LoadProfile() {
		var file = await dialogs.OpenFile(Loc.T("profile.load"), "PoeFree profile", "*.json");
		if (file is null)
			return;
		try {
			var loaded = PatchSettings.Load(file);
			app.Patches = loaded;
			SaveAll();
			dialogs.RestartUi();
		} catch (Exception e) {
			await dialogs.Error(e.Message);
		}
	}

	void SyncSettings() {
		Settings.ColorMods = ColorMods.Select(c => c.Entry).ToList();
		Settings.MinimapMarkers = Markers.Select(m => m.Marker).ToList();
	}

	public void SaveAll() {
		try {
			SyncSettings();
			app.Language = Loc.Instance.Language;
			app.Save();
			Directory.CreateDirectory(AppPaths.DataDirectory);
			File.WriteAllText(AppPaths.IgnoreListFile, IgnoreText);
		} catch (Exception e) {
			Log("Could not save settings: " + e.Message);
		}
	}

	// ---------------------------------------------------------------- apply / restore

	[RelayCommand]
	async Task Apply() {
		if (ArchivePath is null) {
			await dialogs.Error(Loc.T("needsGame"));
			return;
		}
		if (!await dialogs.Confirm(Loc.T("confirm.apply") + Environment.NewLine + Environment.NewLine + ArchivePath))
			return;
		SaveAll();
		var path = ArchivePath;
		IsBusy = true;
		Progress = 0;
		SelectedTab = TabLog;
		await CloseArchiveAsync();
		try {
			var log = new Progress<string>(Log);
			var progress = new Progress<double>(p => Progress = p * 100);
			var settings = Settings.Clone();
			var ignore = IgnoreList.Parse(IgnoreText);
			var summary = await Task.Run(() => PatchEngine.ApplyAsync(path, settings, ignore, log, progress));
			var warning = DescribeApplyProblems(summary);
			if (warning.Length > 0)
				await dialogs.Error(Loc.F("done.apply", summary.FilesChanged, summary.FilesAdded, summary.Elapsed.TotalSeconds) + warning);
			else
				await dialogs.Info(Loc.F("done.apply", summary.FilesChanged, summary.FilesAdded, summary.Elapsed.TotalSeconds));
		} catch (Exception e) {
			Log("Apply failed: " + e);
			await dialogs.Error(Friendly(e));
		} finally {
			IsBusy = false;
			await OpenArchiveAsync(path);
		}
	}

	[RelayCommand]
	async Task Restore() {
		if (ArchivePath is null) {
			await dialogs.Error(Loc.T("needsGame"));
			return;
		}
		if (!await dialogs.Confirm(Loc.T("confirm.restore") + Environment.NewLine + Environment.NewLine + ArchivePath))
			return;
		var path = ArchivePath;
		var version = archive?.Version ?? GameDetector.GuessVersion(path);
		IsBusy = true;
		SelectedTab = TabLog;
		await CloseArchiveAsync();
		try {
			var outcome = await Task.Run(() => RestoreService.RestoreAsync(path, version, new Progress<string>(Log)));
			await dialogs.Info(Loc.F("done.restore", outcome));
		} catch (Exception e) {
			Log("Restore failed: " + e);
			await dialogs.Error(Friendly(e));
		} finally {
			IsBusy = false;
			await OpenArchiveAsync(path);
		}
	}

	static string Friendly(Exception e) => e switch {
		UnauthorizedAccessException => e.Message + "\n\nRun PoeFree as administrator (the game folder is protected).",
		IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "The game files are in use. Close Path of Exile and its launcher, then try again.",
		_ => e.Message,
	};

	[RelayCommand]
	async Task Verify() {
		if (SelectedInstall is null)
			return;
		var dir = SelectedInstall.GameDirectory;
		try {
			if (SelectedInstall.Platform == GamePlatform.Steam) {
				var appId = SelectedInstall.Version == GameVersion.Poe2 ? "2694490" : "238960";
				Process.Start(new ProcessStartInfo("steam://validate/" + appId) { UseShellExecute = true });
				return;
			}
			var packCheck = Path.Combine(dir, "PackCheck.exe");
			if (File.Exists(packCheck)) {
				Process.Start(new ProcessStartInfo(packCheck) { WorkingDirectory = dir, UseShellExecute = true });
				return;
			}
			await dialogs.Info("Use the game launcher to verify/repair the game files.");
		} catch (Exception e) {
			await dialogs.Error(e.Message);
		}
	}

	[RelayCommand]
	void OpenFolder() {
		var dir = SelectedInstall?.GameDirectory ?? (ArchivePath is null ? null : Path.GetDirectoryName(ArchivePath));
		if (dir is not null && Directory.Exists(dir))
			Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
	}

	[RelayCommand]
	void OpenDataFolder() {
		Directory.CreateDirectory(AppPaths.DataDirectory);
		Process.Start(new ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true });
	}

	[RelayCommand]
	void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

	const int TabLog = 7;

	// ---------------------------------------------------------------- skills

	List<string> allSkillFolders = [];
	public ObservableCollection<SkillItem> Skills { get; } = [];
	[ObservableProperty] private string skillFilter = "";
	[ObservableProperty] private string customSkillPath = "";
	[ObservableProperty] private bool onlySelectedSkills;

	partial void OnSkillFilterChanged(string value) => FilterSkills();
	partial void OnOnlySelectedSkillsChanged(bool value) => FilterSkills();

	void LoadSkills() {
		if (archive is null)
			return;
		var set = new SortedSet<string>(StringComparer.Ordinal);
		const string root = "metadata/effects/spells/";
		foreach (var p in archive.PathsUnder(root)) {
			var rest = p[root.Length..];
			var slash = rest.IndexOf('/');
			if (slash > 0)
				set.Add(root + rest[..slash]);
		}
		foreach (var s in Settings.DisabledSkillEffects)
			set.Add(s.TrimEnd('/'));
		allSkillFolders = [.. set];
		FilterSkills();
	}

	void FilterSkills() {
		Skills.Clear();
		IEnumerable<string> list = allSkillFolders;
		if (!string.IsNullOrWhiteSpace(SkillFilter))
			list = list.Where(s => s.Contains(SkillFilter.Trim().Replace(' ', '_'), StringComparison.OrdinalIgnoreCase));
		if (OnlySelectedSkills)
			list = list.Where(s => IsSkillRemoved(s) || IsPathProtected(s));
		foreach (var s in list.Take(1500))
			Skills.Add(new SkillItem(s, this));
	}

	public bool IsSkillRemoved(string folder) => Settings.DisabledSkillEffects.Contains(folder.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);

	public void SetSkillRemoved(string folder, bool value) {
		folder = folder.TrimEnd('/');
		Settings.DisabledSkillEffects.RemoveAll(s => s.TrimEnd('/').Equals(folder, StringComparison.OrdinalIgnoreCase));
		if (value)
			Settings.DisabledSkillEffects.Add(folder);
	}

	public bool IsPathProtected(string path) => IgnoreList.Parse(IgnoreText).Prefixes.Contains(GameArchive.Normalize(path).TrimEnd('/'));

	public void SetPathProtected(string path, bool value) {
		var norm = GameArchive.Normalize(path).TrimEnd('/');
		var lines = IgnoreText.Replace("\r\n", "\n").Split('\n').ToList();
		lines.RemoveAll(l => GameArchive.Normalize(l.Trim()).TrimEnd('/') == norm);
		if (value)
			lines.Add(norm);
		IgnoreText = string.Join(Environment.NewLine, lines);
	}

	[RelayCommand]
	void AddCustomSkill() {
		var p = GameArchive.Normalize(CustomSkillPath).TrimEnd('/');
		if (p.Length == 0)
			return;
		SetSkillRemoved(p, true);
		if (!allSkillFolders.Contains(p)) {
			allSkillFolders.Add(p);
			allSkillFolders.Sort(StringComparer.Ordinal);
		}
		CustomSkillPath = "";
		FilterSkills();
	}

	// ---------------------------------------------------------------- colour mods

	List<StatDescription>? stats;
	public ObservableCollection<StatResult> StatResults { get; } = [];
	public ObservableCollection<ColorModItem> ColorMods { get; } = [];
	[ObservableProperty] private string statSearch = "";
	[ObservableProperty] private StatResult? selectedStat;
	[ObservableProperty] private Color pickColor;
	[ObservableProperty] private ColorModItem? selectedColorMod;
	CancellationTokenSource? statSearchCts;

	partial void OnStatSearchChanged(string value) => _ = SearchStatsAsync(value);

	async Task SearchStatsAsync(string query) {
		statSearchCts?.Cancel();
		var cts = statSearchCts = new CancellationTokenSource();
		await Task.Delay(250);
		if (cts.IsCancellationRequested || archive is null || query.Trim().Length < 2)
			return;
		await EnsureStatsAsync();
		if (stats is null)
			return;
		var q = query.Trim();
		var found = await Task.Run(() => stats
			.Where(s => s.Lines.Any(l => l.Contains(q, StringComparison.OrdinalIgnoreCase)) || s.StatIds.Any(i => i.Contains(q, StringComparison.OrdinalIgnoreCase)))
			.Take(300).Select(s => new StatResult(s)).ToList());
		if (cts.IsCancellationRequested)
			return;
		StatResults.Clear();
		foreach (var r in found)
			StatResults.Add(r);
	}

	async Task EnsureStatsAsync() {
		if (stats is not null || archive is null)
			return;
		Log(Loc.T("colors.loading"));
		await archiveLock.WaitAsync();
		try {
			if (archive is null)
				return;
			var a = archive;
			stats = await Task.Run(() => StatDescriptions.LoadAll(a));
			Log($"{stats.Count:N0} stat descriptions loaded.");
		} finally {
			archiveLock.Release();
		}
	}

	[RelayCommand]
	void AddColorMod() {
		if (SelectedStat is null)
			return;
		var d = SelectedStat.Description;
		var entry = new ColorModEntry {
			Line = d.Lines.FirstOrDefault() ?? "",
			StatId = d.Lines.Count == 0 ? d.StatIds.FirstOrDefault() ?? "" : "",
			FileName = d.File,
			Red = PickColor.R,
			Green = PickColor.G,
			Blue = PickColor.B,
		};
		var existing = ColorMods.FirstOrDefault(c => c.Entry.Line == entry.Line && c.Entry.StatId == entry.StatId && c.Entry.FileName == entry.FileName);
		if (existing is not null) {
			existing.Color = PickColor;
			return;
		}
		ColorMods.Add(new ColorModItem(entry));
	}

	[RelayCommand]
	void RemoveColorMod(ColorModItem? item) {
		if (item is not null)
			ColorMods.Remove(item);
	}

	[RelayCommand]
	void ApplyColorToSelected() {
		if (SelectedColorMod is not null)
			SelectedColorMod.Color = PickColor;
	}

	[RelayCommand]
	void ClearColorMods() => ColorMods.Clear();

	[RelayCommand]
	async Task SuggestedColorModsLoad() {
		await EnsureStatsAsync();
		foreach (var e in SuggestedColorMods.For(archive?.Version ?? GameVersion.Poe1)) {
			if (ColorMods.Any(c => c.Entry.StatId == e.StatId))
				continue;
			// Show the English text instead of the stat id when the game data is loaded.
			var d = stats?.FirstOrDefault(s => s.StatIds.Contains(e.StatId) && s.Lines.Count > 0);
			if (d is not null) {
				e.Line = d.Lines[0];
				e.FileName = d.File;
			}
			ColorMods.Add(new ColorModItem(e));
		}
	}

	[RelayCommand]
	async Task ImportColorMods() {
		var file = await dialogs.OpenFile(Loc.T("colors.import"), "JSON", "*.json");
		if (file is null)
			return;
		try {
			var json = await File.ReadAllTextAsync(file);
			List<ColorModEntry>? list;
			if (json.TrimStart().StartsWith('['))
				list = JsonSerializer.Deserialize(json, PoeFreeJsonContext.Default.ListColorModEntry);
			else
				list = PatchSettings.FromJson(json).ColorMods;
			foreach (var e in list ?? [])
				ColorMods.Add(new ColorModItem(e));
			Log($"Imported {list?.Count ?? 0} colour mod(s).");
		} catch (Exception e) {
			await dialogs.Error(e.Message);
		}
	}

	[RelayCommand]
	async Task ExportColorMods() {
		var file = await dialogs.SaveFile(Loc.T("colors.export"), "color_mods.json", "JSON", "*.json");
		if (file is null)
			return;
		await File.WriteAllTextAsync(file, JsonSerializer.Serialize(ColorMods.Select(c => c.Entry).ToList(), PoeFreeJsonContext.Default.ListColorModEntry));
	}

	// ---------------------------------------------------------------- minimap

	public ObservableCollection<MarkerItem> Markers { get; } = [];
	public ObservableCollection<IconChoice> Icons { get; } = [];
	public RarityIconSettings Rarity => Settings.RarityIcons;
	[ObservableProperty] private string newMarkerPath = "";
	[ObservableProperty] private int newMarkerIcon = 79;
	[ObservableProperty] private string newMarkerHideOn = "";
	[ObservableProperty] private string objectSearchHint = "";

	async Task LoadIconsAsync() {
		var a = archive;
		if (a is null)
			return;
		List<MinimapIconCatalog.Icon> icons;
		await archiveLock.WaitAsync();
		try {
			icons = await Task.Run(() => MinimapIconCatalog.Load(a));
		} catch {
			icons = [];
		} finally {
			archiveLock.Release();
		}
		Icons.Clear();
		foreach (var i in icons)
			Icons.Add(new IconChoice(i.Index, i.Name));
	}

	partial void OnNewMarkerPathChanged(string value) {
		if (archive is null || value.Trim().Length < 4) {
			ObjectSearchHint = "";
			return;
		}
		var p = GameArchive.Normalize(value.Trim());
		var path = p.EndsWith(".ot", StringComparison.Ordinal) ? p : p + ".ot";
		ObjectSearchHint = archive.Exists(path) ? "✓ " + path : "✗ " + path;
	}

	[RelayCommand]
	void AddMarker() {
		if (string.IsNullOrWhiteSpace(NewMarkerPath))
			return;
		Markers.Add(new MarkerItem(new MinimapMarker { ObjectPath = NewMarkerPath.Trim(), Icon = NewMarkerIcon, HideOnState = NewMarkerHideOn.Trim() }));
		NewMarkerPath = "";
		Settings.Enabled.Add("minimap.markers");
		RefreshOptions();
	}

	[RelayCommand]
	void RemoveMarker(MarkerItem? item) {
		if (item is not null)
			Markers.Remove(item);
	}

	public string IconName(int index) => Icons.FirstOrDefault(i => i.Index == index)?.Name ?? "";

	// ---------------------------------------------------------------- ignore list

	[ObservableProperty] private string ignoreText;

	partial void OnIgnoreTextChanged(string value) {
		foreach (var s in Skills)
			s.Refresh();
	}

	[RelayCommand]
	void SaveIgnoreList() {
		SaveAll();
		Log($"Ignore list saved ({IgnoreList.Parse(IgnoreText).Count} active entr(ies)).");
	}

	[RelayCommand]
	void ResetIgnoreList() => IgnoreText = IgnoreList.DefaultText;

	// ---------------------------------------------------------------- explorer

	public ObservableCollection<string> ExplorerResults { get; } = [];
	[ObservableProperty] private string explorerSearch = "";
	[ObservableProperty] private string? selectedPath;
	[ObservableProperty] private string previewText = "";
	CancellationTokenSource? explorerCts;

	partial void OnExplorerSearchChanged(string value) => _ = SearchPathsAsync(value);

	async Task SearchPathsAsync(string query) {
		explorerCts?.Cancel();
		var cts = explorerCts = new CancellationTokenSource();
		await Task.Delay(300);
		var a = archive;
		if (cts.IsCancellationRequested || a is null)
			return;
		var q = query.Trim().Replace('\\', '/').ToLowerInvariant();
		if (q.Length < 3)
			return;
		var found = await Task.Run(() => {
			var r = new List<string>();
			foreach (var p in a.Paths) {
				if (cts.IsCancellationRequested)
					break;
				if (p.Contains(q, StringComparison.Ordinal)) {
					r.Add(p);
					if (r.Count >= 3000)
						break;
				}
			}
			return r;
		});
		if (cts.IsCancellationRequested)
			return;
		ExplorerResults.Clear();
		foreach (var p in found)
			ExplorerResults.Add(p);
	}

	partial void OnSelectedPathChanged(string? value) => _ = PreviewAsync(value);

	async Task PreviewAsync(string? path) {
		var a = archive;
		if (path is null || a is null) {
			PreviewText = "";
			return;
		}
		await archiveLock.WaitAsync();
		try {
			if (archive is null)
				return;
			var bytes = await Task.Run(() => a.ReadBytes(path));
			if (bytes is null) {
				PreviewText = "";
				return;
			}
			PreviewText = PoeText.IsTextPath(path) && bytes.Length < 4_000_000
				? PoeText.Decode(bytes)
				: $"{path}\n{bytes.Length:N0} bytes (binary)\n\n" + HexPreview(bytes);
		} catch (Exception e) {
			PreviewText = e.Message;
		} finally {
			archiveLock.Release();
		}
	}

	static string HexPreview(byte[] data) {
		var sb = new StringBuilder();
		for (var i = 0; i < Math.Min(data.Length, 512); i += 16) {
			sb.Append($"{i:X8}  ");
			for (var j = 0; j < 16 && i + j < data.Length; j++)
				sb.Append($"{data[i + j]:X2} ");
			sb.AppendLine();
		}
		return sb.ToString();
	}

	[RelayCommand]
	async Task ExtractSelected() {
		var a = archive;
		if (a is null || SelectedPath is null)
			return;
		var file = await dialogs.SaveFile(Loc.T("explorer.extract"), Path.GetFileName(SelectedPath), "*", "*");
		if (file is null)
			return;
		var bytes = a.ReadBytes(SelectedPath);
		if (bytes is not null)
			await File.WriteAllBytesAsync(file, bytes);
		Log($"Extracted {SelectedPath} -> {file}");
	}

	[RelayCommand]
	async Task ExtractResults() {
		var a = archive;
		if (a is null || ExplorerResults.Count == 0)
			return;
		var dir = await dialogs.PickFolder(Loc.T("explorer.extract"));
		if (dir is null)
			return;
		IsBusy = true;
		try {
			var list = ExplorerResults.ToList();
			var n = await Task.Run(() => a.ReadMany(list, (p, bytes) => {
				var dest = Path.Combine(dir, p.Replace('/', Path.DirectorySeparatorChar));
				Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
				File.WriteAllBytes(dest, bytes.ToArray());
			}));
			Log($"Extracted {n} file(s) to {dir}");
		} finally {
			IsBusy = false;
		}
	}

	[RelayCommand]
	async Task CopyPath() {
		if (SelectedPath is not null)
			await dialogs.SetClipboard(SelectedPath);
	}

	[RelayCommand]
	async Task ReplaceSelected() {
		if (ArchivePath is null || SelectedPath is null)
			return;
		var source = await dialogs.OpenFile(Loc.T("explorer.replace"), "*", "*");
		if (source is null || !await dialogs.Confirm(Loc.F("confirm.replace", SelectedPath, source)))
			return;
		var path = ArchivePath;
		var target = SelectedPath;
		IsBusy = true;
		await CloseArchiveAsync();
		try {
			await Task.Run(() => {
				if (RestoreService.IsOriginal(path))
					RestoreService.EnsureBackup(path);
				else if (RestoreService.LoadState(path)?.PatchedSha256 != RestoreService.Sha256(GameArchive.ReadRawIndex(path)))
					throw new InvalidOperationException("The game files were modified by another tool. Restore them first.");
				using (var w = GameArchive.Open(path, readOnly: false)) {
					w.Write(target, File.ReadAllBytes(source));
					w.Save();
				}
				RestoreService.RecordPatched(path);
			});
			Log($"Replaced {target} with {source}");
		} catch (Exception e) {
			await dialogs.Error(Friendly(e));
		} finally {
			IsBusy = false;
			await OpenArchiveAsync(path);
			SelectedPath = target;
		}
	}
}
