using Microsoft.Win32;

namespace PoeFree.App;

/// <summary>"Start with Windows": an entry for the current user in the Run key, starting PoeFree hidden in the notification area.</summary>
public static class Startup {
	const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	const string ValueName = "PoeFree";

	public static bool IsSupported => OperatingSystem.IsWindows() && Environment.ProcessPath is not null;

	public static void Set(bool enabled) {
		if (!OperatingSystem.IsWindows())
			throw new PlatformNotSupportedException("Only available on Windows.");
		using var key = Registry.CurrentUser.CreateSubKey(RunKey);
		if (enabled)
			key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {App.TraySwitch}");
		else
			key.DeleteValue(ValueName, false);
	}
}
