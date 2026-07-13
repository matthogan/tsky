using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace tsky;

public partial class ThemedMessageBoxWindow : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public ThemedMessageBoxWindow(
        string message,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(caption) ? "tsky" : caption;
        MessageTextBlock.Text = message ?? string.Empty;

        ApplyIcon(icon);
        BuildButtons(button, defaultResult);

        SourceInitialized += (_, _) => WindowThemeManager.ApplyTitleBarTheme(this, App.IsDarkThemeEnabled());
        App.ThemeChanged += OnThemeChanged;
        Closed += (_, _) => App.ThemeChanged -= OnThemeChanged;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                var fallback = button switch
                {
                    MessageBoxButton.OK => MessageBoxResult.OK,
                    MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
                    MessageBoxButton.YesNo => MessageBoxResult.No,
                    MessageBoxButton.YesNoCancel => MessageBoxResult.Cancel,
                    _ => MessageBoxResult.None
                };

                CloseWithResult(fallback);
                e.Handled = true;
            }
        };
    }

    private void OnThemeChanged(bool useDarkTheme)
    {
        Dispatcher.Invoke(() => WindowThemeManager.ApplyTitleBarTheme(this, useDarkTheme));
    }

    private void ApplyIcon(MessageBoxImage icon)
    {
        IconTextBlock.Visibility = Visibility.Visible;

        switch (icon)
        {
            case MessageBoxImage.Error:
                IconTextBlock.Text = "✖";
                IconTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
                break;
            case MessageBoxImage.Warning:
                IconTextBlock.Text = "⚠";
                IconTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
                break;
            case MessageBoxImage.Information:
                IconTextBlock.Text = "ℹ";
                IconTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0x02, 0x80, 0xC7));
                break;
            case MessageBoxImage.Question:
                IconTextBlock.Text = "?";
                IconTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5));
                break;
            default:
                IconTextBlock.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void BuildButtons(MessageBoxButton button, MessageBoxResult defaultResult)
    {
        ButtonsPanel.Children.Clear();

        var descriptors = new List<ButtonDescriptor>();

        switch (button)
        {
            case MessageBoxButton.OK:
                descriptors.Add(CreateButtonDescriptor("OK", MessageBoxResult.OK, isDefault: true, isCancel: true));
                break;
            case MessageBoxButton.OKCancel:
                descriptors.Add(CreateButtonDescriptor("Cancel", MessageBoxResult.Cancel, isDefault: defaultResult == MessageBoxResult.Cancel, isCancel: true));
                descriptors.Add(CreateButtonDescriptor("OK", MessageBoxResult.OK, isDefault: defaultResult != MessageBoxResult.Cancel));
                break;
            case MessageBoxButton.YesNo:
                descriptors.Add(CreateButtonDescriptor("Yes", MessageBoxResult.Yes, isDefault: defaultResult != MessageBoxResult.No));
                descriptors.Add(CreateButtonDescriptor("No", MessageBoxResult.No, isDefault: defaultResult == MessageBoxResult.No));
                break;
            case MessageBoxButton.YesNoCancel:
                descriptors.Add(CreateButtonDescriptor("Cancel", MessageBoxResult.Cancel, isDefault: defaultResult == MessageBoxResult.Cancel, isCancel: true));
                descriptors.Add(CreateButtonDescriptor("No", MessageBoxResult.No, isDefault: defaultResult == MessageBoxResult.No));
                descriptors.Add(CreateButtonDescriptor("Yes", MessageBoxResult.Yes, isDefault: defaultResult == MessageBoxResult.Yes));
                break;
            default:
                descriptors.Add(CreateButtonDescriptor("OK", MessageBoxResult.OK, isDefault: true, isCancel: true));
                break;
        }

        foreach (var descriptor in descriptors)
        {
            var buttonControl = new Button
            {
                Content = descriptor.Label,
                MinWidth = 90,
                Height = 34,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = descriptor.IsDefault,
                IsCancel = descriptor.IsCancel,
                Cursor = Cursors.Hand,
                FontWeight = FontWeights.SemiBold
            };

            if (descriptor.Result is MessageBoxResult.OK or MessageBoxResult.Yes)
            {
                buttonControl.SetResourceReference(Control.BackgroundProperty, "TabHeaderSelectedBackgroundBrush");
            }
            else
            {
                buttonControl.SetResourceReference(Control.BackgroundProperty, "PanelBackgroundBrush");
            }

            buttonControl.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
            buttonControl.SetResourceReference(Control.BorderBrushProperty, "PanelBorderBrush");

            buttonControl.Click += (_, _) => CloseWithResult(descriptor.Result);
            ButtonsPanel.Children.Add(buttonControl);
        }

        if (ButtonsPanel.Children.Count > 0)
        {
            if (ButtonsPanel.Children[^1] is FrameworkElement element)
            {
                element.Margin = new Thickness(0);
            }
        }
    }

    private void CloseWithResult(MessageBoxResult result)
    {
        Result = result;
        DialogResult = result is MessageBoxResult.OK or MessageBoxResult.Yes;
        Close();
    }

    private static ButtonDescriptor CreateButtonDescriptor(string label, MessageBoxResult result, bool isDefault, bool isCancel = false)
    {
        return new ButtonDescriptor(label, result, isDefault, isCancel);
    }

    private sealed record ButtonDescriptor(string Label, MessageBoxResult Result, bool IsDefault, bool IsCancel);
}
