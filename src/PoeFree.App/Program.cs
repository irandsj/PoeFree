using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Threading;
using PoeFree.Core;

namespace PoeFree.App;

static class Program {
	[STAThread]
	public static int Main(string[] args) {
		if (!OperatingSystem.IsWindows())
			return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

		// One PoeFree per data folder: two of them watching and writing the same game files would get in each other's way.
		var id = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(AppPaths.DataDirectory).ToLowerInvariant())))[..12];
		using var mutex = new Mutex(false, @"Local\PoeFree-" + id);
		bool owned;
		try {
			// After a self update the previous instance is still closing: give it time.
			owned = mutex.WaitOne(args.Contains(App.RestartedSwitch, StringComparer.OrdinalIgnoreCase) ? 15000 : 0);
		} catch (AbandonedMutexException) {
			owned = true;
		}
		var signalName = @"Local\PoeFree-show-" + id;
		if (!owned) {
			// Already running (maybe hidden in the notification area): ask it to show its window.
			try {
				using var show = EventWaitHandle.OpenExisting(signalName);
				show.Set();
			} catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException) {
				// It is starting or closing: nothing to show.
			}
			return 0;
		}
		try {
			using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, signalName);
			var listener = new Thread(() => {
				try {
					while (signal.WaitOne())
						Dispatcher.UIThread.Post(() => (Application.Current as App)?.ShowMainWindow());
				} catch (ObjectDisposedException) {
					// closing
				}
			}) { IsBackground = true, Name = "PoeFree show signal" };
			listener.Start();
			return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
		} finally {
			mutex.ReleaseMutex();
		}
	}

	public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
		.UsePlatformDetect()
		.WithInterFont()
		.LogToTrace();
}
