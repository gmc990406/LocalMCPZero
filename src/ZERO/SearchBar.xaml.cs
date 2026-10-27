using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ZERO;

/// <summary>共享搜索条：占满剩余宽度，右侧「搜索」按钮，回车搜索，有内容时显示清除。</summary>
public partial class SearchBar : UserControl
{
    public event EventHandler? SearchRequested;
    public event EventHandler? Cleared;
    public event TextChangedEventHandler? QueryChanged;

    public SearchBar()
    {
        InitializeComponent();
        HintBlock.Text = Hint;
    }

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(SearchBar),
            new PropertyMetadata("搜索…", (d, e) =>
            {
                if (d is SearchBar s) s.HintBlock.Text = (string)e.NewValue;
            }));

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string Text
    {
        get => Box.Text;
        set
        {
            if (Box.Text == value) return;
            Box.Text = value ?? "";
        }
    }

    public void Clear(bool raise = true)
    {
        if (Box.Text.Length == 0)
        {
            UpdateChrome();
            return;
        }
        Box.Text = "";
        UpdateChrome();
        if (raise) Cleared?.Invoke(this, EventArgs.Empty);
    }

    void UpdateChrome()
    {
        bool empty = Box.Text.Length == 0;
        HintBlock.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        BtnClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateChrome();
        QueryChanged?.Invoke(sender, e);
    }

    void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SearchRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    void Search_Click(object sender, RoutedEventArgs e) => SearchRequested?.Invoke(this, EventArgs.Empty);

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        Clear(raise: true);
        SearchRequested?.Invoke(this, EventArgs.Empty);
    }
}
