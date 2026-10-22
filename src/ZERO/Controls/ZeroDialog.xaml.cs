using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ZERO;

public enum DialogKind { Info, Question, Warning, Danger, Error }

/// <summary>对话框内容。Cancel 为 null 时只显示一个按钮；Note 为醒目的提示框（可选）。</summary>
public sealed record DialogSpec(string Title, string Message, DialogKind Kind = DialogKind.Info,
                                string Ok = "确定", string? Cancel = "取消", string? Note = null);

/// <summary>与 ZERO 界面风格一致的对话框，用来替代系统 MessageBox。</summary>
public partial class ZeroDialog : Window
{
    static readonly Geometry GeoQuestion = Geometry.Parse(
        "M2,12 A10,10 0 1 0 22,12 A10,10 0 1 0 2,12 Z M9.3,9.4 A2.8,2.8 0 1 1 13.4,11.9 C12.5,12.4 12,13 12,14 M12,17.4 L12,17.5");
    static readonly Geometry GeoError = Geometry.Parse(
        "M2,12 A10,10 0 1 0 22,12 A10,10 0 1 0 2,12 Z M9,9 L15,15 M15,9 L9,15");

    readonly DialogSpec _spec;
    bool _closing;

    ZeroDialog(DialogSpec spec)
    {
        InitializeComponent();
        _spec = spec;
        TitleText.Text = spec.Title;
        MessageText.Text = spec.Message;
        MessageText.Visibility = string.IsNullOrEmpty(spec.Message) ? Visibility.Collapsed : Visibility.Visible;
        // 全局 TextBlock 样式会覆盖按钮前景色，所以显式指定文字颜色
        BtnOk.Content = new System.Windows.Controls.TextBlock { Text = spec.Ok, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
        if (spec.Cancel == null) BtnCancel.Visibility = Visibility.Collapsed;
        else BtnCancel.Content = spec.Cancel;

        var (color, glyph) = spec.Kind switch
        {
            DialogKind.Question => (Rgb(0x007AFF), GeoQuestion),
            DialogKind.Warning => (Rgb(0xFF9F0A), (Geometry)FindResource("IcoWarn")),
            DialogKind.Danger => (Rgb(0xFF3B30), (Geometry)FindResource("IcoWarn")),
            DialogKind.Error => (Rgb(0xFF3B30), GeoError),
            _ => (Rgb(0x007AFF), (Geometry)FindResource("IcoInfo")),
        };
        IconBadge.Background = new SolidColorBrush(Color.FromArgb(0x1F, color.R, color.G, color.B));
        IconGlyph.Stroke = new SolidColorBrush(color);
        IconGlyph.Data = glyph;
        if (spec.Kind is DialogKind.Danger or DialogKind.Error)
            BtnOk.Background = (Brush)FindResource("Red");

        if (!string.IsNullOrEmpty(spec.Note))
        {
            bool red = spec.Kind is DialogKind.Danger or DialogKind.Error;
            NoteBox.Visibility = Visibility.Visible;
            NoteBox.Background = new SolidColorBrush(red ? Rgb(0xFFF1F0) : Rgb(0xFFF7EB));
            NoteBox.BorderBrush = new SolidColorBrush(red ? Rgb(0xFFD6D3) : Rgb(0xFFE3B8));
            var fg = new SolidColorBrush(red ? Rgb(0xC4271D) : Rgb(0x9A5800));
            NoteText.Foreground = fg; NoteGlyph.Stroke = fg;
            NoteText.Text = spec.Note;
        }

        PreviewKeyDown += OnKey;
        Loaded += (_, _) =>
        {
            // 危险操作默认聚焦「取消」，避免误按回车
            (DefaultIsCancel ? BtnCancel : BtnOk).Focus();
            Animate(true, null);
        };
    }

    bool DefaultIsCancel => _spec.Cancel != null && _spec.Kind == DialogKind.Danger;

    static Color Rgb(int v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    /// <summary>显示对话框。返回 true 表示点击了主按钮。</summary>
    public static bool Show(Window? owner, DialogSpec spec)
    {
        var d = new ZeroDialog(spec);
        if (owner != null && owner.IsVisible && owner.WindowState != WindowState.Minimized
            && PresentationSource.FromVisual(owner) is { CompositionTarget: not null } src)
        {
            // 覆盖在主窗口上，遮罩与主窗口圆角外框对齐
            var p = src.CompositionTarget.TransformFromDevice.Transform(owner.PointToScreen(new Point(0, 0)));
            d.Owner = owner;
            d.WindowStartupLocation = WindowStartupLocation.Manual;
            d.Left = p.X; d.Top = p.Y;
            d.Width = owner.ActualWidth; d.Height = owner.ActualHeight;
            d.Dim.Margin = owner is MainWindow mw ? mw.Shell.Margin : new Thickness(0);
            d.Dim.CornerRadius = owner is MainWindow ? new CornerRadius(12) : new CornerRadius(0);
        }
        else
        {
            d.Dim.Visibility = Visibility.Collapsed;
            d.SizeToContent = SizeToContent.WidthAndHeight;
            d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            d.Topmost = true;
            d.ShowInTaskbar = true;
            d.Card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) d.DragMove(); };
        }
        return d.ShowDialog() == true;
    }

    void OnKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Finish(_spec.Cancel == null); e.Handled = true; }
        else if (e.Key == Key.Enter)
        {
            // 回车触发当前聚焦的按钮；焦点不在按钮上时触发默认按钮
            bool ok = Keyboard.FocusedElement == BtnOk || (Keyboard.FocusedElement != BtnCancel && !DefaultIsCancel);
            Finish(ok); e.Handled = true;
        }
    }

    void Ok_Click(object s, RoutedEventArgs e) => Finish(true);
    void Cancel_Click(object s, RoutedEventArgs e) => Finish(false);

    void Finish(bool result)
    {
        if (_closing) return;
        _closing = true;
        Animate(false, () => DialogResult = result);
    }

    void Animate(bool open, Action? done)
    {
        var dur = TimeSpan.FromMilliseconds(open ? 200 : 120);
        IEasingFunction ease = open ? new QuarticEase { EasingMode = EasingMode.EaseOut } : new QuadraticEase { EasingMode = EasingMode.EaseIn };
        DoubleAnimation A(double to) => new(to, dur) { EasingFunction = ease };
        var fade = A(open ? 1 : 0);
        if (done != null) fade.Completed += (_, _) => done();
        Dim.BeginAnimation(OpacityProperty, A(open ? 1 : 0));
        Card.BeginAnimation(OpacityProperty, fade);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, A(open ? 1 : 0.97));
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, A(open ? 1 : 0.97));
    }

    /// <summary>调试用：把对话框（含遮罩，叠在窗口底色上）渲染为 2 倍 PNG。</summary>
    public static void RenderPreview(DialogSpec spec, string path)
    {
        var d = new ZeroDialog(spec);
        var root = d.Root;
        d.Content = null;
        d.Dim.Opacity = 1; d.Card.Opacity = 1; d.CardScale.ScaleX = d.CardScale.ScaleY = 1;
        var host = new System.Windows.Controls.Grid { Width = 640, Height = 420, Background = (Brush)d.FindResource("WindowBg") };
        host.Children.Add(root);
        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)host.Width * 2, (int)host.Height * 2, 192, 192, PixelFormats.Pbgra32);
        bmp.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        enc.Save(fs);
        d.Close();
    }
}
