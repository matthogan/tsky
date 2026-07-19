using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using System.Threading.Tasks;

namespace tsky;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public static event Action<bool>? ThemeChanged;

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		DispatcherUnhandledException += App_DispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
		TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
		DebugTraceLog.Write("App", "Global exception handlers attached.");

		ApplySystemThemeResources();
		SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
	}

	protected override void OnExit(ExitEventArgs e)
	{
		DispatcherUnhandledException -= App_DispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
		TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
		SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
		JsonFileStorage.ReleaseAllLocks();
		base.OnExit(e);
	}

	private static void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
	{
		DebugTraceLog.Write(e.Exception, "Unhandled dispatcher exception");
	}

	private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is Exception ex)
		{
			DebugTraceLog.Write(ex, $"Unhandled AppDomain exception (IsTerminating={e.IsTerminating})");
			return;
		}

		DebugTraceLog.Write("App", $"Unhandled AppDomain exception object (IsTerminating={e.IsTerminating}): {e.ExceptionObject}");
	}

	private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
	{
		DebugTraceLog.Write(e.Exception, "Unobserved task exception");
	}

	private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
	{
		if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
		{
			Dispatcher.Invoke(ApplySystemThemeResources);
		}
	}

	private void ApplySystemThemeResources()
	{
		var useDarkTheme = IsDarkThemeEnabled();

		SetBrush("AppBackgroundBrush", useDarkTheme ? "#FF1E1E1E" : "#FFF7F7F8");
		SetBrush("PrimaryTextBrush", useDarkTheme ? "#FFF1F1F1" : "#FF1F2328");
		SetBrush("SecondaryTextBrush", useDarkTheme ? "#FFC9D1D9" : "#FF57606A");
		SetBrush("PanelBackgroundBrush", useDarkTheme ? "#FF252526" : "#FFFFFFFF");
		SetBrush("PanelBorderBrush", useDarkTheme ? "#55FFFFFF" : "#22000000");
		SetBrush("InputBorderBrush", useDarkTheme ? "#33FFFFFF" : "#16000000");
		SetBrush("TabHeaderBackgroundBrush", useDarkTheme ? "#FF2D2D30" : "#FFF3F4F6");
		SetBrush("TabHeaderHoverBackgroundBrush", useDarkTheme ? "#FF37373B" : "#FFE9EBEF");
		SetBrush("TabHeaderSelectedBackgroundBrush", useDarkTheme ? "#FF3F3F46" : "#FFDDE2E8");
		SetBrush("SplitterBrush", useDarkTheme ? "#55FFFFFF" : "#22000000");
		SetBrush("MenuButtonBackgroundBrush", useDarkTheme ? "#88303030" : "#CCFFFFFF");
		SetBrush("MenuButtonHoverBrush", useDarkTheme ? "#AA3A3A3A" : "#F2FFFFFF");
		SetBrush("MenuButtonPressedBrush", useDarkTheme ? "#CC444444" : "#E0FFFFFF");
		SetBrush("ConnectorResolvedBrush", useDarkTheme ? "#FF57D3C8" : "#FF0C8A83");
		SetBrush("ConnectorUnresolvedBrush", useDarkTheme ? "#FFFF7878" : "#FFC62828");
		SetBrush("ConnectorFocusedBrush", useDarkTheme ? "#FFFFD166" : "#FFF59E0B");
		SetBrush("InputPortDotBrush", useDarkTheme ? "#FF9FB8C6" : "#FF455A64");
		SetBrush("OutputPortDotBrush", useDarkTheme ? "#FF67E8D9" : "#FF009688");
		SetDouble("PortDotSize", useDarkTheme ? 10.0 : 8.0);
		SetDouble("PortDotStrokeThickness", useDarkTheme ? 1.4 : 1.0);
		SetThickness("PortDotMargin", useDarkTheme ? new Thickness(0, 3, 0, 3) : new Thickness(0, 2, 0, 2));

		ThemeChanged?.Invoke(useDarkTheme);
	}

	public static bool IsDarkThemeEnabled()
	{
		const string personalizePath = "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";
		const string valueName = "AppsUseLightTheme";

		try
		{
			using var key = Registry.CurrentUser.OpenSubKey(personalizePath);
			var value = key?.GetValue(valueName);

			if (value is int intValue)
			{
				return intValue == 0;
			}

			if (value is byte[] bytes && bytes.Length >= 4)
			{
				return BitConverter.ToInt32(bytes, 0) == 0;
			}
		}
		catch
		{
			return false;
		}

		return false;
	}

	private void SetBrush(string key, string hexColor)
	{
		Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
	}

	private void SetDouble(string key, double value)
	{
		Resources[key] = value;
	}

	private void SetThickness(string key, Thickness value)
	{
		Resources[key] = value;
	}
}

