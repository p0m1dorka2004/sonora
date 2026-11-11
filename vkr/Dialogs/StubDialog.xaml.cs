using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace vkr.Dialogs
{
    public partial class StubDialog : Window
    {
        public StubDialog(string title, string message, string? note = null)
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;
            NoteText.Text = note ?? "Прототип интерфейса · действие не выполняется.";
        }

        public static void Present(Window? owner, string title, string message, string? note = null)
        {
            var dialog = new StubDialog(title, message, note);
            if (owner is { IsVisible: true })
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dialog.ShowDialog();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            OkButton.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => Close();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        }
    }
}
