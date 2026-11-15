using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using vkr.Services;

namespace vkr.Dialogs
{
    public partial class ModelDialog : Window
    {
        public ModelDialog(RecognitionEngine currentEngine)
        {
            InitializeComponent();

            SelectedEngine = currentEngine;
            WhisperEngineRadio.IsChecked = currentEngine == RecognitionEngine.WhisperCpp;
            GigaAmEngineRadio.IsChecked = currentEngine == RecognitionEngine.GigaAmV3;
            GigaAmRnntEngineRadio.IsChecked = currentEngine == RecognitionEngine.GigaAmV3Rnnt;

            UpdateEngineState();
        }

        public RecognitionEngine SelectedEngine { get; private set; }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });

            if (GigaAmRnntEngineRadio.IsChecked == true) GigaAmRnntEngineRadio.Focus();
            else if (GigaAmEngineRadio.IsChecked == true) GigaAmEngineRadio.Focus();
            else WhisperEngineRadio.Focus();
        }

        private void Engine_Checked(object sender, RoutedEventArgs e) => UpdateEngineState();

        private void UpdateEngineState()
        {
            if (ModelNoteText is null) return;

            ModelNoteText.Text = WhisperEngineRadio.IsChecked == true
                ? "Whisper Small · 466 МБ · whisper.cpp · офлайн"
                : GigaAmRnntEngineRadio.IsChecked == true
                    ? "GigaAM v3 RNN-T · 226 МБ · ONNX Runtime · офлайн"
                    : "GigaAM v3 · 225 МБ · ONNX Runtime · офлайн";
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            SelectedEngine = GigaAmRnntEngineRadio.IsChecked == true
                ? RecognitionEngine.GigaAmV3Rnnt
                : GigaAmEngineRadio.IsChecked == true
                    ? RecognitionEngine.GigaAmV3
                    : RecognitionEngine.WhisperCpp;

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

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
