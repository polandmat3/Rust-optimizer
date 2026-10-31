using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ProjectOpti;

public partial class LoginWindow : Window
{
    // Placeholder credentials for now. Replace with real authentication later.
    private const string ValidUser = "admin";
    private const string ValidPass = "admin";

    private FallingIcons? _icons;

    public LoginWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UserBox.Focus();

        // card fades and slides in
        var tt = new TranslateTransform();
        LoginCard.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        LoginCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(500)) { EasingFunction = ease });

        _icons = new FallingIcons(IconCanvas);
        _icons.Start();

        if (_icons.ImageCount == 0)
        {
            NoIconsText.Text = $"No icons found. Put .png / .jpg files in: {_icons.SearchedFolder}";
            NoIconsText.Visibility = Visibility.Visible;
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _icons?.Stop();
    }

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (UserBox.Text.Trim() == ValidUser && PassBox.Password == ValidPass)
        {
            DialogResult = true; // closes the window, App.OnStartup opens the main window
            return;
        }

        ErrorText.Text = "Wrong username or password.";
        ErrorText.Visibility = Visibility.Visible;
        PassBox.Clear();
        PassBox.Focus();

        // little shake so the failed login feels responsive
        var tt = LoginCard.RenderTransform as TranslateTransform ?? new TranslateTransform();
        LoginCard.RenderTransform = tt;
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(360) };
        double[] xs = { 0, -10, 10, -8, 8, -4, 0 };
        for (int i = 0; i < xs.Length; i++)
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(xs[i], KeyTime.FromPercent(i / (double)(xs.Length - 1))));
        tt.BeginAnimation(TranslateTransform.XProperty, shake);
    }
}
