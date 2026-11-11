using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PoeFree.App.ViewModels;

namespace PoeFree.App.Views;

public partial class MainWindow : Window, IDialogs {
	readonly MainViewModel vm;
	readonly AppSettings settings;
	bool started;

	public MainWindow() : this(new AppSettings()) { } // designer

	public MainWindow(AppSettings settings) {
		InitializeComponent();
		this.settings = settings;
		vm = new MainViewModel(settings, this);
		DataContext = vm;
		Opened += (_, _) => Start();
		Closing += (_, e) => {
			vm.SaveAll();
			if (vm.IsWriting) {
				// Closing in the middle of a write could leave the game files half written.
				e.Cancel = true;
				vm.StatusText = Loc.T("watch.busy");
				return;
			}
			var byUser = !e.IsProgrammatic && e.CloseReason == WindowCloseReason.WindowClosing;
			if (byUser && settings.KeepInTray && Application.Current is App { HasTrayIcon: true }) {
				e.Cancel = true;
				Hide();
			}
		};
		Closed += (_, _) => vm.StopWatching();
		vm.AttentionRequested += () => (Application.Current as App)?.ShowMainWindow();
		vm.PropertyChanged += (_, e) => {
			if (e.PropertyName == nameof(MainViewModel.LogText) && this.FindControl<TextBox>("LogBox") is { } box)
				box.CaretIndex = box.Text?.Length ?? 0;
		};
	}

	/// <summary>Loads the game and starts watching it. Runs when the window opens, or right away when PoeFree starts hidden.</summary>
	public async void Start() {
		if (started)
			return;
		started = true;
		await vm.InitializeAsync();
	}

	public void RestartUi() => (Application.Current as App)?.ReopenMainWindow();

	public void Exit() => (Application.Current as App)?.Exit();

	public Task<bool> Confirm(string message) => ShowMessage(message, Loc.T("PoeFree"), true);
	public async Task Info(string message) => await ShowMessage(message, "PoeFree", false);
	public async Task Error(string message) => await ShowMessage(message, Loc.T("error"), false, true);

	async Task<bool> ShowMessage(string message, string title, bool question, bool error = false) {
		var result = false;
		var dialog = new Window {
			Title = title,
			Width = 480,
			SizeToContent = SizeToContent.Height,
			CanResize = false,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = Brush("#0E100B"),
		};
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
		var ok = new Button { Content = question ? Loc.T("yes") : Loc.T("ok"), Classes = { "primary" }, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
		ok.Click += (_, _) => {
			result = true;
			dialog.Close();
		};
		buttons.Children.Add(ok);
		if (question) {
			var no = new Button { Content = Loc.T("no"), MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
			no.Click += (_, _) => dialog.Close();
			buttons.Children.Add(no);
		}
		// PoE-style framed panel (see Themes/PoeTheme.axaml)
		dialog.Content = new Border {
			Classes = { "panel" },
			Margin = new Thickness(8),
			Padding = new Thickness(18),
			Child = new StackPanel {
				Spacing = 16,
				Children = {
					new SelectableTextBlock {
						Text = message,
						TextWrapping = TextWrapping.Wrap,
						FontSize = 15,
						Foreground = error ? Brush("#D2554A") : Brush("#C9C2AE"),
					},
					buttons,
				},
			},
		};
		if (!IsVisible)
			Show(); // a dialog needs a visible owner (PoeFree may be hidden in the notification area)
		await dialog.ShowDialog(this);
		return result;
	}

	static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

	public async Task<string?> OpenFile(string title, string filterName, params string[] patterns) {
		var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
			Title = title,
			AllowMultiple = false,
			FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All],
		});
		return files.Count > 0 ? files[0].TryGetLocalPath() : null;
	}

	public async Task<string?> SaveFile(string title, string suggestedName, string filterName, params string[] patterns) {
		var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
			Title = title,
			SuggestedFileName = suggestedName,
			FileTypeChoices = [new FilePickerFileType(filterName) { Patterns = patterns }],
		});
		return file?.TryGetLocalPath();
	}

	public async Task<string?> PickFolder(string title) {
		var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
		return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
	}

	public async Task SetClipboard(string text) {
		if (Clipboard is not null)
			await Clipboard.SetTextAsync(text);
	}
}
