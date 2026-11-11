using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoeFree.Core.Diagnostics;
using PoeFree.Core.Game;
using PoeFree.Core.Patching;

namespace PoeFree.App.ViewModels;

/// <summary>
/// Watch mode: while PoeFree is open it notices that the game updated (by looking at the game files), writes a report of what
/// changed for an analysis request, and puts the options back once the game is closed. The game is never started or closed.
/// </summary>
public sealed partial class MainViewModel {
	static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(5);
	/// <summary>Ticks in a row without the game running before the files are written (the game needs a moment to let go of them).</summary>
	const int ClosedTicksBeforeApply = 2;
	/// <summary>The archive must have been left alone for this long (a launcher may still be updating it).</summary>
	static readonly TimeSpan QuietBeforeApply = TimeSpan.FromSeconds(20);
	const int ClipboardPathsPerList = 40;

	/// <summary>Replaced by a fake in the off-screen UI checks.</summary>
	public static IGameProbe Probe { get; set; } = new GameProbe();

	[ObservableProperty] private bool gameUpdated;
	[ObservableProperty] private string gameUpdateMessage = "";
	[ObservableProperty] private string gameUpdateHint = "";
	/// <summary>One line about the state of the options after an update (will be applied again, were applied again, failed).</summary>
	[ObservableProperty] private string watchNote = "";
	/// <summary>The game files are being written: the window must not close now.</summary>
	[ObservableProperty] private bool isWriting;

	/// <summary>Something happened that the user should see (the window shows itself if it is hidden in the notification area).</summary>
	public event Action? AttentionRequested;

	DispatcherTimer? watchTimer;
	GameUpdateTracker? tracker;
	UpdateBaseline? pendingReport;
	bool watchTickRunning, wasRunning, forcePoll, reapplyPending, attentionPending;
	int closedTicks;
	DateTime reapplyNotBefore;
	string? resultNote;
	Task afterOpen = Task.CompletedTask;

	public bool WatchGameUpdates {
		get => app.WatchGameUpdates;
		set {
			app.WatchGameUpdates = value;
			OnPropertyChanged();
			if (!value)
				WatchNote = "";
		}
	}

	public bool AutoReapply {
		get => app.AutoReapply;
		set {
			app.AutoReapply = value;
			OnPropertyChanged();
		}
	}

	public bool KeepInTray {
		get => app.KeepInTray;
		set {
			app.KeepInTray = value;
			OnPropertyChanged();
		}
	}

	public bool StartWithWindows {
		get => app.StartWithWindows;
		set {
			try {
				Startup.Set(value);
				app.StartWithWindows = value;
			} catch (Exception e) {
				Log("Could not change the start with Windows setting: " + e.Message);
			}
			OnPropertyChanged();
		}
	}

	public bool CanStartWithWindows => Startup.IsSupported;

	public void StartWatching() {
		if (watchTimer is not null)
			return;
		watchTimer = new DispatcherTimer { Interval = WatchInterval };
		watchTimer.Tick += async (_, _) => await WatchTickAsync();
		watchTimer.Start();
	}

	public void StopWatching() {
		watchTimer?.Stop();
		watchTimer = null;
	}

	/// <summary>One look at the game. Public so the off-screen UI checks can drive it without waiting for the timer.</summary>
	public async Task WatchTickAsync() {
		if (watchTickRunning || IsBusy || IsChecking || OodleMissing || !app.WatchGameUpdates || ArchivePath is null)
			return;
		watchTickRunning = true;
		try {
			var path = ArchivePath;
			if (tracker is null || !tracker.Install.ArchivePath.Equals(path, StringComparison.OrdinalIgnoreCase)) {
				var install = SelectedInstall is { } s && s.ArchivePath.Equals(path, StringComparison.OrdinalIgnoreCase)
					? s : new GameInstall(GameDetector.GuessVersion(path), GameDetector.GuessPlatform(path), path);
				tracker = new GameUpdateTracker(install, Probe);
				closedTicks = 0;
				resultNote = null;
				reapplyNotBefore = default;
				LoadWatchState(path);
			}
			var t = tracker;
			var force = forcePoll;
			forcePoll = false;
			var (update, running) = await Task.Run(() => {
				var isRunning = Probe.IsRunning(t.Install);
				// The file system may only show the game's last writes once it has closed the files: look again right then.
				return (t.Poll(force || (wasRunning && !isRunning)), isRunning);
			});
			if (ArchivePath != path)
				return;
			wasRunning = running;
			closedTicks = running ? 0 : closedTicks + 1;

			if (update is not null) {
				await OnGameUpdatedAsync(t, update);
				return;
			}
			var removed = t.Files == WatchedFiles.Original && t.OptionsWereRemoved;
			if (reapplyPending && t.Files != WatchedFiles.Unknown && !removed) {
				// Applied again or restored by hand in the meantime.
				reapplyPending = false;
				GameUpdateTracker.SetReapplyPending(path, false);
			}
			var auto = removed && reapplyPending && app.AutoReapply && AnyOptionEnabled() && DateTime.UtcNow >= reapplyNotBefore;
			if (auto && !running && closedTicks >= ClosedTicksBeforeApply && DateTime.UtcNow - t.LastWriteUtc >= QuietBeforeApply) {
				await AutoReapplyAsync(path);
				return;
			}
			WatchNote = removed
				? Loc.T(auto ? (running ? "watch.willReapply.running" : "watch.willReapply") : "watch.removed")
				: resultNote ?? "";
			if (attentionPending && !running && !auto) {
				attentionPending = false;
				AttentionRequested?.Invoke();
			}
		} catch (Exception e) {
			Log("Update watch: " + e.Message);
		} finally {
			watchTickRunning = false;
		}
	}

	bool AnyOptionEnabled() => PatchRegistry.All.Any(p => p.IsEnabled(Settings));

	void LoadWatchState(string path) {
		var baseline = GameUpdateTracker.LoadBaseline(path);
		reapplyPending = baseline?.ReapplyPending == true;
		ShowPendingReport(baseline);
	}

	void ShowPendingReport(UpdateBaseline? baseline) {
		pendingReport = baseline is { PendingReportFile: { } file } && File.Exists(file) ? baseline : null;
		GameUpdateHint = "";
		if (pendingReport is null) {
			GameUpdated = false;
			return;
		}
		GameUpdateMessage = Loc.F("watch.updated", VersionChange(pendingReport), pendingReport.PendingDetectedUtc?.ToLocalTime() ?? DateTime.Now);
		GameUpdated = true;
	}

	static string VersionChange(UpdateBaseline b) =>
		$"{GameProbe.VersionLabel(b.PendingOldVersion) ?? "?"} → {GameProbe.VersionLabel(b.PendingNewVersion) ?? Loc.T("watch.newVersion")}";

	/// <summary>Writes the report of a game update: what changed in the files and how every option copes with the new version.</summary>
	async Task OnGameUpdatedAsync(GameUpdateTracker t, GameUpdate update) {
		var path = update.Install.ArchivePath;
		Log($"Game update detected: {GameProbe.VersionLabel(update.OldVersion) ?? "?"} -> {GameProbe.VersionLabel(update.NewVersion) ?? "?"} " +
			$"(index {update.OldSha256[..12]} -> {update.NewSha256[..12]})");
		var optionsRemoved = t.Files == WatchedFiles.Original && t.OptionsWereRemoved;
		string? file = null;
		try {
			// The stored check belongs to the previous game version: keep it before the new check replaces it.
			var before = CompatibilityCheck.Load(path);
			var diffTask = Task.Run(() => IndexDiff.Compare(update.OldIndex, update.NewIndex));
			CompatibilityReport? after = null;
			if (t.Files == WatchedFiles.Original) {
				// Open the new files and test every option on them (read-only: works while the game runs).
				await OpenArchiveAsync(path);
				await afterOpen;
				after = CompatibilityCheck.Load(path);
			}
			var diff = await diffTask;
			var settings = Settings.Clone();
			var text = await Task.Run(() => GameUpdateReport.Build(update, diff, before, after, settings));
			file = GameUpdateReport.Save(update, text);
			Log($"Update report written: {file} (+{diff.Added.Count} / -{diff.Removed.Count} / ~{diff.Resized.Count} files)");
		} catch (Exception e) {
			Log("Could not write the update report: " + e);
		}
		try {
			GameUpdateTracker.Accept(update, file, optionsRemoved);
		} catch (Exception e) {
			Log("Could not store the new game version: " + e.Message);
		}
		reapplyPending = optionsRemoved;
		reapplyNotBefore = default;
		forcePoll = true;
		attentionPending = file is not null;
		ShowPendingReport(GameUpdateTracker.LoadBaseline(path));
	}

	/// <summary>Same as the Apply button, without questions: runs after a game update, with the game closed.</summary>
	async Task AutoReapplyAsync(string path) {
		Log("The game update removed the options: applying them again.");
		SaveAll();
		IsBusy = true;
		IsWriting = true;
		Progress = 0;
		WatchNote = Loc.T("watch.reapplying");
		await CloseArchiveAsync();
		try {
			var log = new Progress<string>(Log);
			var progress = new Progress<double>(p => Progress = p * 100);
			var settings = Settings.Clone();
			var ignore = IgnoreList.Parse(IgnoreText);
			var summary = await Task.Run(() => PatchEngine.ApplyAsync(path, settings, ignore, log, progress));
			var problems = DescribeApplyProblems(summary);
			resultNote = Loc.F("watch.reapplied", DateTime.Now, summary.FilesChanged + summary.FilesAdded)
				+ (problems.Length > 0 ? " " + Loc.T("watch.reapplied.problems") : "");
			reapplyPending = false;
			GameUpdateTracker.SetReapplyPending(path, false);
		} catch (Exception e) {
			Log("Automatic apply failed: " + e);
			resultNote = Loc.F("watch.reapplyFailed", Friendly(e).Split('\n')[0]);
			// Do not try again in a loop (e.g. no permission): wait for the user or for a while.
			reapplyNotBefore = DateTime.UtcNow + TimeSpan.FromMinutes(10);
		} finally {
			IsWriting = false;
			IsBusy = false;
			await OpenArchiveAsync(path);
			forcePoll = true;
			WatchNote = resultNote ?? "";
		}
	}

	/// <summary>Puts on the clipboard a ready request for analysis: a short introduction and the report of the update.</summary>
	[RelayCommand]
	async Task CopyAnalysisRequest() {
		if (pendingReport?.PendingReportFile is not { } file)
			return;
		try {
			var report = await Task.Run(() => GameUpdateReport.Shorten(File.ReadAllText(file), ClipboardPathsPerList));
			var request = Loc.F("watch.request", VersionChange(pendingReport).Replace("→", "->"), file)
				+ Environment.NewLine + Environment.NewLine + report;
			await dialogs.SetClipboard(request);
			GameUpdateHint = Loc.T("watch.copied");
			Log($"Analysis request copied ({request.Length:N0} characters).");
		} catch (Exception e) {
			await dialogs.Error(e.Message);
		}
	}

	[RelayCommand]
	async Task OpenUpdateReport() {
		if (pendingReport?.PendingReportFile is not { } file)
			return;
		try {
			Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose();
		} catch (Exception e) {
			await dialogs.Error(e.Message);
		}
	}

	[RelayCommand]
	void DismissUpdateReport() {
		if (ArchivePath is not null)
			GameUpdateTracker.DismissReport(ArchivePath);
		ShowPendingReport(null);
	}

	[RelayCommand]
	void OpenUpdateReports() {
		var dir = ArchivePath is null ? Path.Combine(PoeFree.Core.AppPaths.DataDirectory, "updates") : GameUpdateTracker.FolderFor(ArchivePath);
		Directory.CreateDirectory(dir);
		Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
	}
}
