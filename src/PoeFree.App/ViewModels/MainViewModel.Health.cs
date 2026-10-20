using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PoeFree.Core.Diagnostics;
using PoeFree.Core.Patching;

namespace PoeFree.App.ViewModels;

/// <summary>Compatibility check (options broken by a game update) and PoeFree update notifications.</summary>
public sealed partial class MainViewModel {
	const int TabDiagnostics = 6;

	public ObservableCollection<HealthItem> HealthResults { get; } = [];
	[ObservableProperty] private string compatSummary = Loc.T("diag.none");
	[ObservableProperty] private bool compatProblems;
	[ObservableProperty] private string compatBanner = "";
	[ObservableProperty] private bool isChecking;

	[ObservableProperty] private bool updateAvailable;
	[ObservableProperty] private string updateMessage = "";
	string? updateUrl;
	string? updateTag;

	public bool CheckUpdatesAtStartup {
		get => app.CheckUpdates;
		set {
			app.CheckUpdates = value;
			OnPropertyChanged();
		}
	}

	/// <summary>Shows the last stored report and runs a new check if the game files changed since then.</summary>
	async Task AfterArchiveOpenedAsync(string path) {
		ShowReport(CompatibilityCheck.Load(path));
		if (archive is null || archive.IsModified)
			return;
		bool needed;
		try {
			needed = await Task.Run(() => CompatibilityCheck.NeedsCheck(path, out _));
		} catch {
			return;
		}
		if (needed) {
			Log(Loc.T("diag.running"));
			await RunCompatibilityCheck();
		}
	}

	[RelayCommand]
	async Task RunCompatibilityCheck() {
		if (archive is null || IsChecking) {
			if (archive is null)
				await dialogs.Error(Loc.T("needsGame"));
			return;
		}
		IsChecking = true;
		CompatSummary = Loc.T("diag.running");
		await archiveLock.WaitAsync();
		try {
			if (archive is null)
				return;
			var a = archive;
			var report = await Task.Run(() => CompatibilityCheck.Run(a));
			if (!report.ArchiveModified)
				CompatibilityCheck.Save(report);
			ShowReport(report);
			Log(CompatSummary);
			foreach (var p in report.Problems)
				Log($"  {p.Status}: {p.Id} - {Loc.HealthDetail(p)}");
		} catch (Exception e) {
			CompatSummary = e.Message;
			Log("Compatibility check failed: " + e);
		} finally {
			archiveLock.Release();
			IsChecking = false;
		}
	}

	void ShowReport(CompatibilityReport? report) {
		HealthResults.Clear();
		foreach (var g in OptionGroups)
			foreach (var o in g.Items)
				o.Warning = null;
		if (report is null) {
			CompatSummary = Loc.T("diag.none");
			CompatProblems = false;
			return;
		}
		foreach (var r in report.Results.OrderBy(r => r.Status switch { HealthStatus.Broken => 0, HealthStatus.Warning => 1, HealthStatus.Ok => 2, _ => 3 }))
			HealthResults.Add(new HealthItem(r));
		var ok = report.Results.Count(r => r.Status == HealthStatus.Ok);
		var broken = report.Results.Count(r => r.Status == HealthStatus.Broken);
		var warn = report.Results.Count(r => r.Status == HealthStatus.Warning);
		CompatSummary = Loc.F("diag.summary", ok, broken, warn, report.CheckedUtc.ToLocalTime())
			+ (report.ArchiveModified ? "  -  " + Loc.T("diag.modified") : "");
		MarkProblems(report.Problems);
	}

	void MarkProblems(IEnumerable<PatchHealth> problems) {
		var list = problems.Where(p => p.Status == HealthStatus.Broken).ToList();
		foreach (var g in OptionGroups)
			foreach (var o in g.Items)
				if (list.FirstOrDefault(p => p.Id == o.Patch.Id) is { } p)
					o.Warning = "⚠ " + Loc.T("diag.option") + ": " + Loc.HealthDetail(p);
		CompatProblems = list.Count > 0;
		CompatBanner = CompatProblems
			? Loc.F("diag.banner", list.Count, string.Join(", ", list.Select(p => PatchRegistry.Get(p.Id) is { } patch ? Loc.PatchName(patch) : p.Id)))
			: "";
	}

	[RelayCommand]
	void ShowDiagnostics() => SelectedTab = TabDiagnostics;

	/// <summary>Problems of the options used in the last apply, appended to the result message.</summary>
	string DescribeApplyProblems(ApplySummary summary) {
		if (summary.Problems.Count == 0)
			return "";
		MarkProblems(summary.Problems);
		return Environment.NewLine + Environment.NewLine + Loc.T("apply.problems") + Environment.NewLine
			+ string.Join(Environment.NewLine, summary.Problems.Select(p =>
				"• " + (PatchRegistry.Get(p.Id) is { } patch ? Loc.PatchName(patch) : p.Id) + " - " + Loc.HealthDetail(p)));
	}

	// ---------------------------------------------------------------- PoeFree updates

	async Task CheckUpdatesAtStartupAsync() {
		if (!app.CheckUpdates)
			return;
		if (app.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(6) && app.IgnoredUpdate is null)
			return;
		await CheckUpdatesAsync(silent: true);
	}

	[RelayCommand]
	Task CheckUpdatesNow() => CheckUpdatesAsync(silent: false);

	async Task CheckUpdatesAsync(bool silent) {
		try {
			var info = await UpdateChecker.CheckAsync(app.UpdateRepository);
			app.LastUpdateCheckUtc = DateTime.UtcNow;
			if (info is null || (silent && info.Tag == app.IgnoredUpdate)) {
				UpdateAvailable = false;
				if (!silent)
					await dialogs.Info(Loc.F("update.none", UpdateChecker.CurrentVersion.ToString(3)));
				return;
			}
			updateUrl = info.Url;
			updateTag = info.Tag;
			UpdateMessage = Loc.F("update.available", info.Tag, UpdateChecker.CurrentVersion.ToString(3));
			UpdateAvailable = true;
			Log(UpdateMessage + " " + info.Url);
		} catch (Exception e) {
			if (!silent)
				await dialogs.Error(Loc.F("update.failed", e.Message));
			else
				Log(Loc.F("update.failed", e.Message));
		}
	}

	[RelayCommand]
	void OpenUpdate() {
		if (updateUrl is not null)
			OpenUrl(updateUrl);
	}

	[RelayCommand]
	void IgnoreUpdate() {
		app.IgnoredUpdate = updateTag;
		UpdateAvailable = false;
		SaveAll();
	}
}
