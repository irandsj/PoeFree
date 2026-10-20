using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PoeFree.App.ViewModels;

namespace PoeFree.App.Views;

public partial class MainWindow : Window, IDialogs {
	readonly MainViewModel vm;

	public MainWindow() : this(new AppSettings()) { } // designer

	public MainWindow(AppSettings settings) {
		InitializeComponent();
		vm = new MainViewModel(settings, this);
		DataContext = vm;
		Opened += async (_, _) => await vm.InitializeAsync();
		Closing += (_, _) => vm.SaveAll();
		vm.PropertyChanged += (_, e) => {
			if (e.PropertyName == nameof(MainViewModel.LogText) && this.FindControl<TextBox>("LogBox") is { } box)
				box.CaretIndex = box.Text?.Length ?? 0;
		};
	}

	public void RestartUi() => (Application.Current as App)?.ReopenMainWindow();

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
			Background = new SolidColorBrush(Color.Parse("#1E2227")),
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
		dialog.Content = new StackPanel {
			Margin = new Thickness(18),
			Spacing = 16,
			Children = {
				new SelectableTextBlock {
					Text = message,
					TextWrapping = TextWrapping.Wrap,
					Foreground = error ? new SolidColorBrush(Color.Parse("#FF8080")) : Brushes.White,
				},
				buttons,
			},
		};
		await dialog.ShowDialog(this);
		return result;
	}

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
