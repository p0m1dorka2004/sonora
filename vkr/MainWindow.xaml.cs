using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using vkr.Dialogs;
using vkr.Themes;

namespace vkr
{
    public partial class MainWindow : Window
    {
        private enum RecordingState
        {
            Idle,
            Recording,
            Processing
        }

        private readonly DispatcherTimer _recordTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
        private readonly DispatcherTimer _waveTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
        private readonly Random _random = new();
        private readonly List<Rectangle> _waveBars = new();
        private readonly double[] _barHeights = new double[44];

        private RecordingState _state = RecordingState.Idle;
        private DateTime _recordingStarted;
        private double _wavePhase;
        private int _waveTick;
        private bool _isMaximized;
        private bool _syncingTheme;
        private Rect _restoreBounds;

        private static readonly Brush RecordIdleCircle = Freeze(Color.FromRgb(0xF5, 0xF2, 0xE9));
        private static readonly Brush RecordActiveCircle = Freeze(Color.FromRgb(0xC4, 0x45, 0x3C));
        private static readonly Brush RecordProcessingCircle = Freeze(Color.FromRgb(0x2A, 0x4A, 0x42));
        private static readonly Brush LiveActiveDot = Freeze(Color.FromRgb(0xE0, 0x6A, 0x5E));

        private static Brush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public MainWindow()
        {
            InitializeComponent();
            BuildWaveform();

            _recordTimer.Tick += (_, _) => UpdateTimerText();
            _waveTimer.Tick += (_, _) => UpdateWaveform();

            ThemeToggle.IsChecked = ThemeManager.IsDark;
            ThemeToggle.Checked += ThemeToggle_Changed;
            ThemeToggle.Unchecked += ThemeToggle_Changed;
            ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
            Closed += (_, _) => ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;

            Loaded += (_, _) =>
            {
                FitToWorkArea();
                ResetRecordingVisuals();
            };
        }

        private void FitToWorkArea()
        {
            var area = SystemParameters.WorkArea;
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);
            Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
            Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
        }

        // =====================  Хром окна  =====================

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || IsInteractiveOrigin(e.OriginalSource))
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }

            if (_isMaximized)
            {
                RestoreWindow();
            }

            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private static bool IsInteractiveOrigin(object? source)
        {
            var current = source as DependencyObject;
            while (current is not null)
            {
                if (current is ButtonBase or TextBoxBase or ScrollBar or Thumb)
                {
                    return true;
                }

                current = current is Visual or Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }

            return false;
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

        private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void ToggleMaximize()
        {
            if (_isMaximized || WindowState == WindowState.Maximized)
            {
                RestoreWindow();
            }
            else
            {
                MaximizeWindow();
            }
        }

        private void MaximizeWindow()
        {
            _restoreBounds = new Rect(Left, Top, Width, Height);
            var area = SystemParameters.WorkArea;

            _isMaximized = true;
            if (MaximizeGlyph is not null)
            {
                MaximizeGlyph.Text = "\uE923";
            }

            WindowState = WindowState.Normal;
            Left = area.Left;
            Top = area.Top;
            Width = area.Width;
            Height = area.Height;
        }

        private void RestoreWindow()
        {
            _isMaximized = false;
            if (MaximizeGlyph is not null)
            {
                MaximizeGlyph.Text = "\uE922";
            }

            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
            }
            else if (_restoreBounds.Width > 0 && _restoreBounds.Height > 0)
            {
                Left = _restoreBounds.Left;
                Top = _restoreBounds.Top;
                Width = _restoreBounds.Width;
                Height = _restoreBounds.Height;
            }

            RootBorder.Padding = new Thickness(0);
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                _isMaximized = true;
                if (MaximizeGlyph is not null)
                {
                    MaximizeGlyph.Text = "\uE923";
                }

                if (RootBorder is not null)
                {
                    RootBorder.Padding = new Thickness(SystemParameters.WindowResizeBorderThickness.Left);
                }
            }
            else if (!_isMaximized && WindowState == WindowState.Normal)
            {
                if (RootBorder is not null)
                {
                    RootBorder.Padding = new Thickness(0);
                }
            }
        }

        // =====================  Волна и таймер  =====================

        private void BuildWaveform()
        {
            WaveformPanel.Children.Clear();
            _waveBars.Clear();

            var brush = Freeze(Color.FromRgb(0x8C, 0xCB, 0xB7));

            for (var i = 0; i < _barHeights.Length; i++)
            {
                var bar = new Rectangle
                {
                    Width = 2.5,
                    Height = 5,
                    RadiusX = 1.25,
                    RadiusY = 1.25,
                    Fill = brush,
                    Margin = new Thickness(1.3, 0, 1.3, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Opacity = 0.55
                };

                _barHeights[i] = 5;
                _waveBars.Add(bar);
                WaveformPanel.Children.Add(bar);
            }

            SetIdleWave();
        }

        private void SetIdleWave()
        {
            for (var i = 0; i < _waveBars.Count; i++)
            {
                var height = 4 + 9 * Math.Abs(Math.Sin(i * 0.42));
                _barHeights[i] = height;
                _waveBars[i].Height = height;
                _waveBars[i].Opacity = 0.62;
            }
        }

        private void UpdateWaveform()
        {
            _waveTick++;
            _wavePhase += 0.17;

            for (var i = 0; i < _waveBars.Count; i++)
            {
                var envelope = 0.35 + 0.65 * Math.Abs(Math.Sin(_wavePhase + i * 0.34));
                var jitter = _waveTick % 5 == 0 ? _random.NextDouble() * 6 : 0;
                var target = 4 + 26 * envelope + jitter;

                _barHeights[i] += (target - _barHeights[i]) * 0.32;
                _barHeights[i] = Math.Clamp(_barHeights[i], 3, 32);

                _waveBars[i].Height = _barHeights[i];
                _waveBars[i].Opacity = 0.55 + _barHeights[i] / 80;
            }

            if (_state == RecordingState.Recording)
            {
                LevelFill.Width = 55 + _random.NextDouble() * 190;
                RailLevelFill.Width = 28 + _random.NextDouble() * 76;
                LevelValueText.Text = $"−{_random.Next(22, 37)} дБ";
            }
        }

        private void UpdateTimerText()
        {
            if (_state != RecordingState.Recording)
            {
                return;
            }

            var elapsed = DateTime.Now - _recordingStarted;
            RecordTimerText.Text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        }

        // =====================  Запись  =====================

        private void RecordButton_Click(object sender, RoutedEventArgs e) => ToggleRecording();

        private void ToggleRecording()
        {
            if (_state == RecordingState.Processing)
            {
                return;
            }

            if (_state == RecordingState.Recording)
            {
                _ = StopAndProcessAsync();
            }
            else
            {
                StartRecording();
            }
        }

        private void StartRecording()
        {
            _state = RecordingState.Recording;
            _recordingStarted = DateTime.Now;
            RecordTimerText.Text = "00:00";

            _recordTimer.Start();
            _waveTimer.Start();

            DictationTitle.Text = "Идёт запись";
            DictationHint.Text = "Говорите естественно — измерения распределятся по карточкам протокола";

            RecordCircle.Background = RecordActiveCircle;
            RecordGlyph.Text = "\uE71A";
            RecordGlyph.Foreground = Brushes.White;
            RecordButton.ToolTip = "Остановить запись (Esc)";

            LiveDot.Fill = LiveActiveDot;
            var blink = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(750))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            LiveDot.BeginAnimation(OpacityProperty, blink);

            var pulseOpacity = new DoubleAnimation(0.7, 0, TimeSpan.FromMilliseconds(1600))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var pulseScale = new DoubleAnimation(0.88, 1.22, TimeSpan.FromMilliseconds(1600))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            RecordRing.BeginAnimation(OpacityProperty, pulseOpacity);
            RecordRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulseScale);
            RecordRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulseScale);

            RecognitionStatusText.Text = "Запись";
            RecognitionDot.Fill = LiveActiveDot;
            ApplyRecognitionPill(RecordingState.Recording);
        }

        private async Task StopAndProcessAsync()
        {
            _state = RecordingState.Processing;
            _recordTimer.Stop();
            _waveTimer.Stop();

            StopRecordAnimations();

            DictationTitle.Text = "Обрабатываю запись";
            DictationHint.Text = "Распознавание речи и распределение значений по полям протокола";

            RecordCircle.Background = RecordProcessingCircle;
            RecordGlyph.Text = "\uE720";
            RecordGlyph.Foreground = Freeze(Color.FromRgb(0x9F, 0xB8, 0xB0));
            RecordButton.IsEnabled = false;
            RecordButton.ToolTip = "Обработка записи";

            LiveDot.Fill = (Brush)FindResource("HeroFaintBrush");
            LiveDot.Opacity = 1;

            RecognitionStatusText.Text = "Обработка";
            RecognitionDot.Fill = (Brush)FindResource("AmberBrush");
            ApplyRecognitionPill(RecordingState.Processing);

            await Task.Delay(950);

            _state = RecordingState.Idle;
            RecordButton.IsEnabled = true;
            ResetRecordingVisuals();

            StubDialog.Present(
                this,
                "Распознавание речи",
                "Запись остановлена. В полной версии приложения аудио будет преобразовано в текст, а значения — распределены по соответствующим полям протокола.",
                "Модуль распознавания не подключён в прототипе интерфейса.");
        }

        private void StopRecordAnimations()
        {
            RecordRing.BeginAnimation(OpacityProperty, null);
            RecordRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            RecordRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            RecordRing.Opacity = 0;
            RecordRingScale.ScaleX = 1;
            RecordRingScale.ScaleY = 1;

            LiveDot.BeginAnimation(OpacityProperty, null);
            LiveDot.Opacity = 1;
        }

        private void ResetRecordingVisuals()
        {
            _recordTimer.Stop();
            _waveTimer.Stop();

            RecordTimerText.Text = "00:00";
            DictationTitle.Text = "Готов к записи";
            DictationHint.Text = "Нажмите F9 и назовите показатели: «аорта 37, ФВ 65, E/A 0,7»";

            RecordCircle.Background = RecordIdleCircle;
            RecordGlyph.Text = "\uE720";
            RecordGlyph.Foreground = Freeze(Color.FromRgb(0x15, 0x23, 0x1F));
            RecordButton.IsEnabled = true;
            RecordButton.ToolTip = "Начать запись (F9)";

            LiveDot.Fill = (Brush)FindResource("HeroFaintBrush");
            RecognitionStatusText.Text = "Готово";
            RecognitionDot.Fill = (Brush)FindResource("GreenDotBrush");
            ApplyRecognitionPill(RecordingState.Idle);

            LevelFill.Width = 96;
            RailLevelFill.Width = 58;
            LevelValueText.Text = "−32 дБ";
            SetIdleWave();
        }

        private void ApplyRecognitionPill(RecordingState state)
        {
            switch (state)
            {
                case RecordingState.Recording:
                    RecognitionPill.Background = (Brush)FindResource("DangerSoftBrush");
                    RecognitionPill.BorderBrush = (Brush)FindResource("PillDangerBorderBrush");
                    RecognitionStatusText.Foreground = (Brush)FindResource("DangerBrush");
                    RecognitionDot.Fill = LiveActiveDot;
                    break;
                case RecordingState.Processing:
                    RecognitionPill.Background = (Brush)FindResource("AmberSoftBrush");
                    RecognitionPill.BorderBrush = (Brush)FindResource("PillAmberBorderBrush");
                    RecognitionStatusText.Foreground = (Brush)FindResource("AmberBrush");
                    RecognitionDot.Fill = (Brush)FindResource("AmberBrush");
                    break;
                default:
                    RecognitionPill.Background = (Brush)FindResource("AccentSoftBrush");
                    RecognitionPill.BorderBrush = (Brush)FindResource("PillAccentBorderBrush");
                    RecognitionStatusText.Foreground = (Brush)FindResource("AccentBrush");
                    RecognitionDot.Fill = (Brush)FindResource("GreenDotBrush");
                    break;
            }
        }

        private void ThemeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingTheme)
            {
                return;
            }

            ThemeManager.SetTheme(ThemeToggle.IsChecked == true);
        }

        private void ThemeManager_ThemeChanged(object? sender, EventArgs e)
        {
            _syncingTheme = true;
            ThemeToggle.IsChecked = ThemeManager.IsDark;
            _syncingTheme = false;

            ApplyRecognitionPill(_state);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                Save_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F9)
            {
                ToggleRecording();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _state == RecordingState.Recording)
            {
                _ = StopAndProcessAsync();
                e.Handled = true;
            }
        }

        // =====================  Заглушки  =====================

        private void Stub_Click(object sender, RoutedEventArgs e)
        {
            var action = (sender as FrameworkElement)?.Tag as string ?? "Действие";
            StubDialog.Present(
                this,
                action,
                $"Элемент «{action}» подготовлен в интерфейсе, но его логика не входит в демонстрационный прототип. Действие будет подключено на этапе программной реализации.");
        }

        private void NewProtocol_Click(object sender, RoutedEventArgs e) =>
            ShowStub("Новый протокол",
                "Будет создана пустая форма исследования с выбором пациента, области сканирования и шаблона протокола.",
                "Несохранённый черновик не пострадает: в прототипе действие не выполняется.");

        private void Protocol_Click(object sender, RoutedEventArgs e) => MainScroll.ScrollToTop();

        private void PatientEdit_Click(object sender, RoutedEventArgs e) =>
            ShowStub("Данные пациента",
                "Редактирование ФИО, даты рождения, номера исследования и области сканирования будет доступно в карточке пациента.");

        private void Export_Click(object sender, RoutedEventArgs e) =>
            ShowStub("Экспорт в PDF",
                "Готовый протокол будет сформирован в виде PDF-документа с подписью врача и сохранён в выбранную папку.",
                "Формирование документа будет выполняться локально, без передачи данных в сеть.");

        private void Print_Click(object sender, RoutedEventArgs e) =>
            ShowStub("Печать протокола",
                "Печать будет выполняться через системный диалог с предварительным просмотром документа.");

        private void Save_Click(object sender, RoutedEventArgs e) =>
            ShowStub("Сохранение черновика",
                "Черновик будет сохранён в локальное хранилище и появится в разделе «Мои исследования».",
                "Локальное сохранение будет реализовано в программной части приложения.");

        private void Finish_Click(object sender, RoutedEventArgs e) =>
            ShowStub("Завершение протокола",
                "Протокол будет проверен на полноту, подписан врачом и перемещён в архив исследований.",
                "В полной версии потребуется подтверждение врача и проверка полей, отмеченных жёлтым.");

        private void Preview_Click(object sender, RoutedEventArgs e) =>
            ShowStub("Предпросмотр протокола",
                "Здесь появится документ в том виде, в котором он будет сохранён или напечатан.");

        private void ModelSelect_Click(object sender, RoutedEventArgs e) =>
            ShowStub("Выбор модели распознавания",
                "Здесь будет выполняться выбор локальной модели распознавания речи. В полной версии можно будет переключаться между быстрой, точной и медицинской моделями — без передачи аудио в сеть.",
                "Доступные модели: Compact · Precision · Medical.");

        private void OrganSoon_Click(object sender, RoutedEventArgs e)
        {
            var organ = (sender as FrameworkElement)?.Tag as string ?? "Этот орган";
            ShowStub("Орган пока недоступен",
                $"Протокол для области «{organ}» появится в следующих версиях. Сейчас в программе реализован только анализ сердца.",
                "Один протокол — один орган. Модуль находится в разработке.");
        }

        private void ShowAllTranscript_Click(object sender, RoutedEventArgs e) =>
            ShowStub("Полный транскрипт",
                "Будет открыт журнал всех распознанных фрагментов с временными метками, уверенностью и привязкой к полям протокола.");

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            var query = SearchBox.Text.Trim();
            e.Handled = true;

            ShowStub("Поиск по архиву",
                string.IsNullOrWhiteSpace(query)
                    ? "Введите ФИО пациента, номер исследования или фрагмент заключения — поиск будет выполняться по локальному архиву."
                    : $"Поиск по запросу «{query}» будет выполняться по локальному архиву протоколов.",
                "Индексация и поиск будут реализованы в программной части.");
        }

        private void FocusField_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string fieldName)
            {
                return;
            }

            var field = FindName(fieldName) as TextBox;
            if (field is null)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                var point = field.TranslatePoint(new Point(0, 0), MainScroll);
                var target = MainScroll.VerticalOffset + point.Y
                             - MainScroll.ViewportHeight / 2 + field.ActualHeight / 2;
                MainScroll.ScrollToVerticalOffset(Math.Max(0, Math.Min(target, MainScroll.ScrollableHeight)));

                field.Focus();
                field.CaretIndex = field.Text.Length;
            }, DispatcherPriority.Loaded);
        }

        private void ShowStub(string title, string message, string? note = null) =>
            StubDialog.Present(this, title, message, note);
    }
}
