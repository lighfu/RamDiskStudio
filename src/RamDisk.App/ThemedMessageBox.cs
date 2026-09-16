using System.Windows;
using System.Windows.Controls;

namespace RamDisk.App;

public static class ThemedMessageBox
{
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None) =>
        Show(null, text, caption, buttons, image, defaultResult);

    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var dialog = new ThemedDialog(text, caption, buttons, image, defaultResult);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Result;
    }
}

internal sealed class ThemedDialog : Window
{
    internal MessageBoxResult Result { get; private set; }
    internal Button DefaultButton { get; }

    internal ThemedDialog(string text, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        ThemeManager.Attach(this);
        Title = caption; Width = 560; SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(240, Math.Min(760, SystemParameters.WorkArea.Height - 70));
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var options = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            MessageBoxButton.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
            _ => [MessageBoxResult.OK]
        };
        Result = options.Contains(MessageBoxResult.Cancel) ? MessageBoxResult.Cancel : options.Contains(MessageBoxResult.No) ? MessageBoxResult.No : MessageBoxResult.OK;
        if (!options.Contains(defaultResult)) defaultResult = options[0];
        var body = new Grid { Margin = new Thickness(24) };
        body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new TextBlock { Text = caption, FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 16) };
        if (image is MessageBoxImage.Warning or MessageBoxImage.Error) heading.SetResourceReference(TextBlock.ForegroundProperty, image == MessageBoxImage.Error ? "Error" : "Warning");
        body.Children.Add(heading);
        var scroll = new ScrollViewer { MaxHeight = MaxHeight - 175, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) } };
        Grid.SetRow(scroll, 1); body.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        Grid.SetRow(actions, 2); body.Children.Add(actions);
        Button? preferred = null;
        foreach (var result in options)
        {
            var button = new Button { Content = result switch { MessageBoxResult.Yes => "はい", MessageBoxResult.No => "いいえ", MessageBoxResult.Cancel => "キャンセル", _ => "OK" },
                MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsDefault = result == defaultResult, IsCancel = result == Result };
            if (button.IsDefault) button.SetResourceReference(StyleProperty, "PrimaryButton");
            button.Click += (_, _) => { Result = result; DialogResult = result is MessageBoxResult.OK or MessageBoxResult.Yes; };
            if (button.IsDefault) preferred = button;
            actions.Children.Add(button);
        }
        DefaultButton = preferred!;
        Content = body;
        Loaded += (_, _) => DefaultButton.Focus();
    }
}
