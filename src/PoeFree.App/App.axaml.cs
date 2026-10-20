using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PoeFree.App.Views;
using PoeFree.Core.Archive;

namespace PoeFree.App;

public partial class App : Application {
	public AppSettings Settings { get; private set; } = null!;

	public override void Initialize() => AvaloniaXamlLoader.Load(this);

	public override void OnFrameworkInitializationCompleted() {
		Settings = AppSettings.Load();
		if (string.IsNullOrEmpty(Settings.Language))
			Settings.Language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt" ? "pt" : "en";
		Loc.Instance.SetLanguage(Settings.Language);
		OodleResolver.PreferredPath = Settings.OodlePath;
		OodleResolver.Install();

		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
			desktop.MainWindow = new MainWindow(Settings);
		base.OnFrameworkInitializationCompleted();
	}

	/// <summary>Rebuilds the main window (used after changing language or loading a profile).</summary>
	public void ReopenMainWindow() {
		if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
			return;
		var old = desktop.MainWindow;
		var next = new MainWindow(Settings);
		desktop.MainWindow = next;
		next.Show();
		old?.Close();
	}
}
