using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using MirrorDeck.App.Interop;

namespace MirrorDeck.App;

public partial class App : Application
{
	public static bool UseLightTheme { get; private set; }

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		ApplySystemTheme();
		SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
	}

	protected override void OnExit(ExitEventArgs e)
	{
		SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
		base.OnExit(e);
	}

	public static void ApplySystemTheme()
	{
		UseLightTheme = ReadAppsUseLightTheme();
		ResourceDictionary resources = Application.Current.Resources;
		if (UseLightTheme)
		{
			SetBrush(resources, "AppBackgroundBrush", Color.FromRgb(246, 248, 251));
			SetBrush(resources, "PanelBrush", Color.FromArgb(110, 250, 250, 250));
			SetBrush(resources, "PanelBrushSolid", Color.FromRgb(248, 250, 252));
			SetBrush(resources, "CardBrush", Color.FromRgb(byte.MaxValue, byte.MaxValue, byte.MaxValue));
			SetBrush(resources, "ControlBrush", Color.FromRgb(byte.MaxValue, byte.MaxValue, byte.MaxValue));
			SetBrush(resources, "ControlHoverBrush", Color.FromRgb(239, 244, 249));
			SetBrush(resources, "ControlPressedBrush", Color.FromRgb(226, 234, 242));
			SetBrush(resources, "PopupBrush", Color.FromRgb(byte.MaxValue, byte.MaxValue, byte.MaxValue));
			SetBrush(resources, "AccentBrush", Color.FromRgb(0, 103, 192));
			SetBrush(resources, "AccentTextBrush", Color.FromRgb(byte.MaxValue, byte.MaxValue, byte.MaxValue));
			SetBrush(resources, "TextBrush", Color.FromRgb(30, 34, 40));
			SetBrush(resources, "MutedTextBrush", Color.FromRgb(84, 96, 110));
			SetBrush(resources, "DisabledTextBrush", Color.FromRgb(130, 141, 154));
			SetBrush(resources, "BorderBrushSoft", Color.FromRgb(210, 218, 228));
		}
		else
		{
			SetBrush(resources, "AppBackgroundBrush", Color.FromRgb(28, 28, 28));
			SetBrush(resources, "PanelBrush", Color.FromArgb(100, 28, 28, 28));
			SetBrush(resources, "PanelBrushSolid", Color.FromRgb(32, 32, 32));
			SetBrush(resources, "CardBrush", Color.FromArgb(160, 40, 40, 40));
			SetBrush(resources, "ControlBrush", Color.FromArgb(220, 48, 48, 48));
			SetBrush(resources, "ControlHoverBrush", Color.FromRgb(59, 59, 59));
			SetBrush(resources, "ControlPressedBrush", Color.FromRgb(44, 44, 44));
			SetBrush(resources, "PopupBrush", Color.FromRgb(36, 36, 36));
			SetBrush(resources, "AccentBrush", Color.FromRgb(76, 194, byte.MaxValue));
			SetBrush(resources, "AccentTextBrush", Color.FromRgb(6, 20, 28));
			SetBrush(resources, "TextBrush", Color.FromRgb(243, 247, 250));
			SetBrush(resources, "MutedTextBrush", Color.FromRgb(170, 180, 192));
			SetBrush(resources, "DisabledTextBrush", Color.FromRgb(111, 120, 133));
			SetBrush(resources, "BorderBrushSoft", Color.FromArgb(180, 78, 78, 78));
		}
		foreach (Window window in Application.Current.Windows)
		{
			Win11WindowStyler.Apply(window, !UseLightTheme);
		}
	}

	private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
	{
		UserPreferenceCategory category = e.Category;
		if ((category == UserPreferenceCategory.Color || category == UserPreferenceCategory.General) ? true : false)
		{
			Application.Current.Dispatcher.Invoke(ApplySystemTheme);
		}
	}

	private static bool ReadAppsUseLightTheme()
	{
		using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
		return registryKey?.GetValue("AppsUseLightTheme") is int num && num != 0;
	}

	private static void SetBrush(ResourceDictionary resources, string key, Color color)
	{
		if (resources[key] is SolidColorBrush { IsFrozen: false } solidColorBrush)
		{
			solidColorBrush.Color = color;
		}
		else
		{
			resources[key] = new SolidColorBrush(color);
		}
	}
}
