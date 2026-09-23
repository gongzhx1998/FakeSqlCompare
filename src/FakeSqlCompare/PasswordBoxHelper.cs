using System.Windows;
using System.Windows.Controls;

namespace FakeSqlCompare;

public static class PasswordBoxHelper
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword", typeof(string), typeof(PasswordBoxHelper),
            new FrameworkPropertyMetadata(string.Empty, OnBoundPasswordChanged));

    public static readonly DependencyProperty BindPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BindPassword", typeof(bool), typeof(PasswordBoxHelper),
            new PropertyMetadata(false, OnBindPasswordChanged));

    private static readonly DependencyProperty UpdatingProperty =
        DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(PasswordBoxHelper));

    public static string GetBoundPassword(DependencyObject obj) => (string)obj.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject obj, string value) => obj.SetValue(BoundPasswordProperty, value);
    public static bool GetBindPassword(DependencyObject obj) => (bool)obj.GetValue(BindPasswordProperty);
    public static void SetBindPassword(DependencyObject obj, bool value) => obj.SetValue(BindPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        if ((bool)box.GetValue(UpdatingProperty)) return;
        box.PasswordChanged -= HandlePasswordChanged;
        box.Password = e.NewValue as string ?? "";
        box.PasswordChanged += HandlePasswordChanged;
    }

    private static void OnBindPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        box.PasswordChanged -= HandlePasswordChanged;
        if ((bool)e.NewValue) box.PasswordChanged += HandlePasswordChanged;
    }

    private static void HandlePasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetValue(UpdatingProperty, true);
        SetBoundPassword(box, box.Password);
        box.SetValue(UpdatingProperty, false);
    }
}

public static class ComboBoxAssist
{
    public static readonly DependencyProperty DropDownCommandProperty =
        DependencyProperty.RegisterAttached(
            "DropDownCommand", typeof(System.Windows.Input.ICommand), typeof(ComboBoxAssist),
            new PropertyMetadata(null, OnDropDownCommandChanged));

    public static void SetDropDownCommand(DependencyObject obj, System.Windows.Input.ICommand? value)
        => obj.SetValue(DropDownCommandProperty, value);

    public static System.Windows.Input.ICommand? GetDropDownCommand(DependencyObject obj)
        => (System.Windows.Input.ICommand?)obj.GetValue(DropDownCommandProperty);

    private static void OnDropDownCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox box) return;
        box.DropDownOpened -= OnDropDownOpened;
        if (e.NewValue is System.Windows.Input.ICommand)
            box.DropDownOpened += OnDropDownOpened;
    }

    private static async void OnDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox box) return;
        var cmd = GetDropDownCommand(box);
        if (cmd is AsyncRelayCommand asyncCmd)
            await asyncCmd.ExecuteAsync();
        else if (cmd?.CanExecute(null) == true)
            cmd.Execute(null);
        if (box.IsKeyboardFocusWithin && box.Items.Count > 0)
            box.IsDropDownOpen = true;
    }
}
