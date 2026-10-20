using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PoeFree.Core.ColorMods;
using PoeFree.Core.Minimap;
using PoeFree.Core.Patching;

namespace PoeFree.App.ViewModels;

public sealed partial class OptionItem : ObservableObject {
	readonly PatchSettings settings;
	public Patch Patch { get; }
	public string Name => Loc.PatchName(Patch);
	public string Description => Loc.PatchDescription(Patch);
	public bool Experimental => Patch.Experimental;
	public string ExperimentalText => Loc.T("experimental");
	public bool HasZoom => Patch.Id == "camera.zoom";

	[ObservableProperty]
	private bool isSupported = true;

	public OptionItem(Patch patch, PatchSettings settings) {
		Patch = patch;
		this.settings = settings;
	}

	public bool IsChecked {
		get => settings.IsEnabled(Patch.Id);
		set {
			if (value == IsChecked)
				return;
			settings.Set(Patch.Id, value);
			OnPropertyChanged();
		}
	}

	public void Refresh() => OnPropertyChanged(nameof(IsChecked));

	/// <summary>Set when the last compatibility check found this option broken.</summary>
	[ObservableProperty]
	private string? warning;
}

public sealed class HealthItem(Core.Diagnostics.PatchHealth health) {
	public Core.Diagnostics.PatchHealth Health { get; } = health;
	public string Name { get; } = Core.Patching.PatchRegistry.Get(health.Id) is { } p ? Loc.PatchName(p) : health.Id;
	public string Id => Health.Id;
	public string StatusText => Loc.T("health." + Health.Status);
	public string Detail => Loc.HealthDetail(Health);
	public IBrush Brush => new SolidColorBrush(Health.Status switch {
		Core.Diagnostics.HealthStatus.Ok => Color.Parse("#4EC9B0"),
		Core.Diagnostics.HealthStatus.Warning => Color.Parse("#FFD580"),
		Core.Diagnostics.HealthStatus.Broken => Color.Parse("#FF8080"),
		_ => Color.Parse("#7E8790"),
	});
}

public sealed class OptionGroup(string title, IReadOnlyList<OptionItem> items) {
	public string Title { get; } = title;
	public IReadOnlyList<OptionItem> Items { get; } = items;
}

public sealed partial class SkillItem(string fullPath, MainViewModel owner) : ObservableObject {
	public string FullPath { get; } = fullPath;
	public string Name { get; } = fullPath.TrimEnd('/').Split('/')[^1];

	public bool IsRemoved {
		get => owner.IsSkillRemoved(FullPath);
		set {
			owner.SetSkillRemoved(FullPath, value);
			OnPropertyChanged();
		}
	}

	public bool IsProtected {
		get => owner.IsPathProtected(FullPath);
		set {
			owner.SetPathProtected(FullPath, value);
			OnPropertyChanged();
		}
	}

	public void Refresh() {
		OnPropertyChanged(nameof(IsRemoved));
		OnPropertyChanged(nameof(IsProtected));
	}
}

public sealed partial class ColorModItem(ColorModEntry entry) : ObservableObject {
	public ColorModEntry Entry { get; } = entry;

	public string Text => string.IsNullOrEmpty(Entry.Line) ? Entry.StatId : Entry.Line;
	public string Detail => string.Join("  ", new[] { Entry.StatId, Entry.FileName }.Where(s => !string.IsNullOrEmpty(s) && s != Text));

	public bool Enabled {
		get => !Entry.IsDisabled;
		set {
			Entry.IsDisabled = !value;
			OnPropertyChanged();
		}
	}

	public Color Color {
		get => Color.FromRgb(Entry.Red, Entry.Green, Entry.Blue);
		set {
			Entry.Red = value.R;
			Entry.Green = value.G;
			Entry.Blue = value.B;
			OnPropertyChanged();
			OnPropertyChanged(nameof(Brush));
		}
	}

	public IBrush Brush => new SolidColorBrush(Color);
}

public sealed class StatResult(StatDescription d) {
	public StatDescription Description { get; } = d;
	public string Text { get; } = d.Lines.FirstOrDefault() ?? string.Join(", ", d.StatIds);
	public string Detail { get; } = $"{d.File}  -  {string.Join(", ", d.StatIds)}";
}

public sealed partial class MarkerItem(MinimapMarker marker) : ObservableObject {
	public MinimapMarker Marker { get; } = marker;
	public string ObjectPath => Marker.NormalizedPath;

	public int Icon {
		get => Marker.Icon;
		set {
			Marker.Icon = value;
			OnPropertyChanged();
		}
	}

	public string HideOnState {
		get => Marker.HideOnState;
		set {
			Marker.HideOnState = value;
			OnPropertyChanged();
		}
	}

	public bool Enabled {
		get => Marker.Enabled;
		set {
			Marker.Enabled = value;
			OnPropertyChanged();
		}
	}
}

public sealed record IconChoice(int Index, string Name) {
	public override string ToString() => $"{Index} - {Name}";
}
