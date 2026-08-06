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
		SetBrush("SidebarSectionHeaderBrush", useDarkTheme ? "#FF6E7681" : "#FF57606A");
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
		SetBrush("TaskCardSelectionBrush", useDarkTheme ? "#FF9CDCFE" : "#FF005A9C");
		SetBrush("InputPortDotBrush", useDarkTheme ? "#FF7CFF00" : "#FF455A64");
		SetBrush("OutputPortDotBrush", useDarkTheme ? "#FF67E8D9" : "#FF009688");
		SetBrush("ListenerOnBackgroundBrush", useDarkTheme ? "#FF2A8A54" : "#FFCFEED8");
		SetBrush("ListenerOnBorderBrush", useDarkTheme ? "#FF46BE79" : "#FF75B68A");
		SetBrush("ListenerOffBackgroundBrush", useDarkTheme ? "#FF8A3A3A" : "#FFF7D9D9");
		SetBrush("ListenerOffBorderBrush", useDarkTheme ? "#FFC95A5A" : "#FFD89A9A");
		SetBrush("ListenerPendingBackgroundBrush", useDarkTheme ? "#FF9A6A10" : "#FFF6E8CC");
		SetBrush("ListenerPendingBorderBrush", useDarkTheme ? "#FFD9A040" : "#FFD8B676");
		SetBrush("AgentsDispatchTextBrush", useDarkTheme ? "#FFF2F4F8" : "#FF1F2328");
		SetBrush("AgentsDispatchMutedTextBrush", useDarkTheme ? "#FFB8C1CC" : "#FF57606A");
		SetBrush("AgentsDispatchRowBorderBrush", useDarkTheme ? "#FF363A3E" : "#22000000");
		SetBrush("AgentsDispatchRowSelectedBorderBrush", useDarkTheme ? "#FF27F6C0" : "#FF0C8A83");
		SetBrush("AgentsDispatchRowFocusedBorderBrush", useDarkTheme ? "#FF8CC2FF" : "#FFF59E0B");
		SetBrush("AgentsDispatchRowBackgroundBrush", useDarkTheme ? "#FF1F2429" : "#FFFFFFFF");
		SetBrush("AgentsDispatchRowSelectedBackgroundBrush", useDarkTheme ? "#FF232A2B" : "#FFE6F3F2");
		SetBrush("AgentsDispatchRowFocusedBackgroundBrush", useDarkTheme ? "#FF262D33" : "#FFF3F4F6");
		SetBrush("AgentsDispatchPlayEnabledBackgroundBrush", useDarkTheme ? "#FF19A850" : "#FFB9E8CD");
		SetBrush("AgentsDispatchPlayEnabledBorderBrush", useDarkTheme ? "#FF3AD77A" : "#FF74BD96");
		SetBrush("AgentsDispatchPlayDisabledBackgroundBrush", useDarkTheme ? "#FF33373C" : "#FFE6E8EB");
		SetBrush("AgentsDispatchPlayDisabledBorderBrush", useDarkTheme ? "#FF494F56" : "#FFD0D7DE");
		SetBrush("AgentsDispatchValidationErrorBrush", useDarkTheme ? "#FFE16A6A" : "#FFC62828");
		SetBrush("GraphNodeBackgroundBrush", useDarkTheme ? "#FF1F2A35" : "#FFFFFFFF");
		SetBrush("GraphNodeDisabledBackgroundBrush", useDarkTheme ? "#FF202224" : "#FFF3F4F6");
		SetBrush("GraphNodeBorderBrush", useDarkTheme ? "#FF3D4D5D" : "#FFD0D7DE");
		SetBrush("GraphNodeDisabledBorderBrush", useDarkTheme ? "#FF4A4D50" : "#FFC2C8CF");
		SetBrush("ScrollBarTrackBrush", useDarkTheme ? "#33484F58" : "#16000000");
		SetBrush("ScrollBarThumbBrush", useDarkTheme ? "#CC9AA4AF" : "#8057606A");
		SetBrush("ScrollBarThumbHoverBrush", useDarkTheme ? "#E0B7C0CA" : "#A057606A");
		SetBrush("ScrollBarThumbPressedBrush", useDarkTheme ? "#FFF1F5F9" : "#CC57606A");
		SetBrush("ScrollBarThumbDisabledBrush", useDarkTheme ? "#557B8591" : "#4057606A");
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

