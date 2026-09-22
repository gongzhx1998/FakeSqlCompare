using System.Windows;

namespace FakeSqlCompare;

public partial class AliasPromptWindow : Window
{
    public AliasPromptWindow(string initial)
    {
        InitializeComponent();
        AliasBox.Text = initial;
        Loaded += (_, _) =>
        {
            AliasBox.Focus();
            AliasBox.SelectAll();
        };
    }

    public string Alias => AliasBox.Text.Trim();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AliasBox.Text))
        {
            AliasBox.Focus();
            return;
        }
        DialogResult = true;
    }

    public static bool TryAsk(Window owner, string initial, out string alias)
    {
        var dlg = new AliasPromptWindow(initial) { Owner = owner };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Alias))
        {
            alias = dlg.Alias;
            return true;
        }
        alias = "";
        return false;
    }
}
