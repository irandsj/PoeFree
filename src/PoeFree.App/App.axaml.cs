using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using PoeFree.App.Views;
using PoeFree.Core.Archive;
using PoeFree.Core.Diagnostics;

namespace PoeFree.App;

public partial class App : Application {
	/// <summary>Command line switch that starts PoeFree hidden in the notification area (used by "start with Windows").</summary>
	public const string TraySwitch = "--tray";
	/// <summary>Passed when PoeFree restarts itself after an update: wait for the previous instance to finish closing.</summary>
	public const string RestartedSwitch = "--restarted";

	public AppSettings Settings { get; private set; } = null!;

	/// <summary>The notification area icon exists, so a hidden window can be brought back.</summary>
	public bool HasTrayIcon { get; private set; }

	MainWindow? mainWindow;

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted() {
		Settings = AppSettings.Load();
		if (string.IsNullOrEmpty(Settings.Language))
			Settings.Language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt" ? "pt" : "en";
		Loc.Instance.SetLanguage(Settings.Language);
		OodleResolver.PreferredPath = Settings.OodlePath;
		OodleResolver.Install();
		_ = CleanupAfterUpdateAsync();

		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
			CreateTrayIcon();
			mainWindow = new MainWindow(Settings);
			var hidden = HasTrayIcon && (desktop.Args ?? []).Contains(TraySwitch, StringComparer.OrdinalIgnoreCase);
			if (hidden)
				mainWindow.Start(); // works without ever being shown; the icon brings the window up
			else
				desktop.MainWindow = mainWindow;
		}
		base.OnFrameworkInitializationCompleted();
	}

	void CreateTrayIcon() {
		try {
			var open = new NativeMenuItem(Loc.T("tray.open"));
			open.Click += (_, _) => ShowMainWindow();
			var exit = new NativeMenuItem(Loc.T("tray.exit"));
			exit.Click += (_, _) => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.TryShutdown();
			var icon = new TrayIcon {
				Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://PoeFree/Assets/poefree.ico"))),
				ToolTipText = "PoeFree",
				Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), exit } },
			};
			icon.Clicked += (_, _) => ShowMainWindow();
			TrayIcon.SetIcons(this, [icon]);
			HasTrayIcon = true;
		} catch (Exception) {
			// No notification area on this system: PoeFree simply does not hide there.
			HasTrayIcon = false;
		}
	}

	/// <summary>Brings the main window to the front (from the notification area, or when PoeFree is started a second time).</summary>
	public void ShowMainWindow() {
		if (mainWindow is not { } w)
			return;
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: null } desktop)
			desktop.MainWindow = w;
		if (!w.IsVisible)
			w.Show();
		if (w.WindowState == WindowState.Minimized)
			w.WindowState = WindowState.Normal;
		w.Activate();
	}

	/// <summary>Removes the files of the previous version after a self update (it may still be closing: a few tries).</summary>
	static async Task CleanupAfterUpdateAsync() {
		foreach (var delay in new[] { 0, 3000, 10000 }) {
			await Task.Delay(delay);
			await Task.Run(() => SelfUpdater.CleanupOldFiles());
		}
	}

	/// <summary>Rebuilds the main window (used after changing language or loading a profile).</summary>
	public void ReopenMainWindow() {
		if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
			return;
		var old = mainWindow;
		var next = new MainWindow(Settings);
		mainWindow = next;
		desktop.MainWindow = next;
		next.Show();
		old?.Close();
	}

	public void Exit() => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
}
