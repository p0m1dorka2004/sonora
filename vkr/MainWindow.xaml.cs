using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using vkr.Dialogs;
using vkr.Services;
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

        private readonly NativeWhisperService _whisper = new();
        private readonly GigaAmService _gigaAm = new();
        private readonly GigaAmRnntService _gigaAmRnnt = new();
        private RecognitionEngine _engine = RecognitionEngine.WhisperCpp;
        private CancellationTokenSource? _transcriptionCts;
        private bool _isTranscribing;
        private DateTime _processingStarted;
        private string _lastTranscriptText = string.Empty;

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
            UpdateModelUi();

            _recordTimer.Tick += (_, _) => UpdateTimerText();
            _waveTimer.Tick += (_, _) => UpdateWaveform();

            ThemeToggle.IsChecked = ThemeManager.IsDark;
            ThemeToggle.Checked += ThemeToggle_Changed;
            ThemeToggle.Unchecked += ThemeToggle_Changed;
            ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
            Closed += (_, _) =>
            {
                ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
                _transcriptionCts?.Cancel();
                _whisper.Dispose();
                _gigaAm.Dispose();
                _gigaAmRnnt.Dispose();
            };

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
            if (_state == RecordingState.Recording)
            {
                var elapsed = DateTime.Now - _recordingStarted;
                RecordTimerText.Text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
                return;
            }

            if (_state == RecordingState.Processing && _processingStarted != default)
            {
                var elapsed = DateTime.Now - _processingStarted;
                RecordTimerText.Text = $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
            }
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
                "Микрофон пока не подключён",
                "Запись с микрофона появится позже. Сейчас распознавание можно проверить на готовом аудиофайле.",
                "Нажмите «Распознать аудиофайл» — локальная модель Whisper Small уже работает.");
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
            else if (e.Key == Key.Escape && _isTranscribing)
            {
                _transcriptionCts?.Cancel();
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

        private void ModelSelect_Click(object sender, RoutedEventArgs e)
        {
            if (_isTranscribing)
            {
                return;
            }

            var dialog = new ModelDialog(_engine) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                _engine = dialog.SelectedEngine;
                UpdateModelUi();
            }
        }

        private string EngineLabel => _engine switch
        {
            RecognitionEngine.GigaAmV3 => "GigaAM v3",
            RecognitionEngine.GigaAmV3Rnnt => "GigaAM v3 RNN-T",
            _ => "Whisper Small"
        };

        private void UpdateModelUi()
        {
            switch (_engine)
            {
                case RecognitionEngine.GigaAmV3:
                    ModelNameText.Text = "GigaAM v3";
                    ModelInfoText.Text = "GigaAM v3 · ONNX · локально";
                    ModelSelectButton.ToolTip = "Движок: GigaAM v3. Нажмите, чтобы изменить.";
                    AutomationProperties.SetName(ModelSelectButton, "Модель распознавания: GigaAM v3");
                    break;
                case RecognitionEngine.GigaAmV3Rnnt:
                    ModelNameText.Text = "GigaAM v3 RNN-T";
                    ModelInfoText.Text = "GigaAM v3 RNN-T · ONNX · локально";
                    ModelSelectButton.ToolTip = "Движок: GigaAM v3 RNN-T. Нажмите, чтобы изменить.";
                    AutomationProperties.SetName(ModelSelectButton, "Модель распознавания: GigaAM v3 RNN-T");
                    break;
                default:
                    ModelNameText.Text = "Whisper Small";
                    ModelInfoText.Text = "Whisper Small · локально";
                    ModelSelectButton.ToolTip = "Движок: Whisper Small. Нажмите, чтобы изменить.";
                    AutomationProperties.SetName(ModelSelectButton, "Модель распознавания: Whisper Small");
                    break;
            }
        }

        // =====================  Локальное распознавание  =====================

        private async void TranscribeFile_Click(object sender, RoutedEventArgs e)
        {
            if (_isTranscribing || _state == RecordingState.Recording)
            {
                return;
            }

            if (_engine == RecognitionEngine.WhisperCpp && !_whisper.IsModelAvailable)
            {
                ShowStub("Модель не найдена",
                    $"Не найден файл модели:\n{_whisper.ModelPath}",
                    "Поместите ggml-small.bin в папку models рядом с приложением.");
                return;
            }

            if (_engine == RecognitionEngine.GigaAmV3 && !_gigaAm.IsAvailable)
            {
                ShowStub("GigaAM не настроена",
                    "Не найдены модель, препроцессор или словарь GigaAM.",
                    "Ожидается папка models\\gigaam рядом с приложением.");
                return;
            }

            if (_engine == RecognitionEngine.GigaAmV3Rnnt && !_gigaAmRnnt.IsAvailable)
            {
                ShowStub("GigaAM RNN-T не настроена",
                    "Не найдены модели RNN-T, препроцессор или словарь.",
                    "Ожидается папка models\\gigaam-rnnt рядом с приложением.");
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "Выберите аудиофайл",
                Filter = "Аудиофайлы|*.wav;*.mp3;*.flac|Все файлы|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog(this) == true)
            {
                await TranscribeFileAsync(dialog.FileName);
            }
        }

        private async Task TranscribeFileAsync(string audioPath)
        {
            _isTranscribing = true;
            _state = RecordingState.Processing;
            _processingStarted = DateTime.Now;
            _transcriptionCts = new CancellationTokenSource();
            _recordTimer.Start();
            _waveTimer.Start();

            DictationTitle.Text = "Распознаю аудиофайл";
            DictationHint.Text = $"{System.IO.Path.GetFileName(audioPath)} · загрузка локальной модели…";
            RecordTimerText.Text = "00:00";
            RecognitionStatusText.Text = "Загрузка";
            ApplyRecognitionPill(RecordingState.Processing);

            RecordButton.IsEnabled = false;
            TranscribeFileButton.IsEnabled = false;
            ModelSelectButton.IsEnabled = false;

            StopRecordAnimations();
            LiveDot.Fill = (Brush)FindResource("AmberBrush");
            LiveDot.Opacity = 1;

            TranscriptDynamic.Children.Clear();
            TranscriptDynamic.Visibility = Visibility.Visible;
            TranscriptStatic.Visibility = Visibility.Collapsed;
            TranscriptCountText.Text = "0";
            _lastTranscriptText = string.Empty;

            RecognitionStatusText.Text = "Обработка";
            DictationHint.Text = $"{System.IO.Path.GetFileName(audioPath)} · {EngineLabel}" +
                                 (_engine is RecognitionEngine.GigaAmV3 or RecognitionEngine.GigaAmV3Rnnt ? " · ONNX" : string.Empty);

            try
            {
                var result = await Task.Run(
                    () => _engine switch
                    {
                        RecognitionEngine.GigaAmV3 => _gigaAm.Transcribe(audioPath),
                        RecognitionEngine.GigaAmV3Rnnt => _gigaAmRnnt.Transcribe(audioPath),
                        _ => _whisper.Transcribe(audioPath)
                    },
                    _transcriptionCts.Token);

                foreach (var segment in result.Segments)
                {
                    AppendTranscriptSegment(segment);
                }

                _lastTranscriptText = result.Text;

                if (result.Segments.Count == 0 || string.IsNullOrWhiteSpace(result.Text))
                {
                    RecognitionStatusText.Text = "Нет речи";
                    DictationTitle.Text = "Речь не найдена";
                    DictationHint.Text = "В файле не удалось выделить речь. Попробуйте другой файл.";
                    ApplyRecognitionPill(RecordingState.Processing);
                    return;
                }

                var filled = ApplyRecognizedValues(result.Text);
                var duration = result.AudioDuration;
                RecordTimerText.Text = $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}";
                DictationTitle.Text = "Файл распознан";
                DictationHint.Text =
                    $"{System.IO.Path.GetFileName(audioPath)} · {duration:mm\\:ss} · {result.Segments.Count} фрагментов · заполнено полей: {filled}";
                RecognitionStatusText.Text = "Готово";
                ApplyRecognitionPill(RecordingState.Idle);
            }
            catch (OperationCanceledException)
            {
                RecognitionStatusText.Text = "Отменено";
                DictationTitle.Text = "Распознавание отменено";
                DictationHint.Text = "Можно выбрать другой аудиофайл.";
                ApplyRecognitionPill(RecordingState.Idle);
            }
            catch (Exception ex)
            {
                RecognitionStatusText.Text = "Ошибка";
                DictationTitle.Text = "Не удалось распознать";
                DictationHint.Text = "Проверьте аудиофайл и локальную модель.";
                ApplyRecognitionPill(RecordingState.Processing);
                ShowStub("Ошибка распознавания", ex.Message,
                    "Локальные движки работают офлайн. Проверьте модель whisper.cpp и окружение GigaAM.");
            }
            finally
            {
                _transcriptionCts?.Dispose();
                _transcriptionCts = null;
                _isTranscribing = false;
                _state = RecordingState.Idle;
                _recordTimer.Stop();
                _waveTimer.Stop();

                RecordButton.IsEnabled = true;
                TranscribeFileButton.IsEnabled = true;
                ModelSelectButton.IsEnabled = true;

                LiveDot.Fill = (Brush)FindResource("HeroFaintBrush");
                LiveDot.Opacity = 1;
                SetIdleWave();
            }
        }

        private void AppendTranscriptSegment(TranscriptionSegment segment)
        {
            var block = new StackPanel { Margin = new Thickness(0, 9, 0, 0) };

            var header = new Grid();
            var time = new TextBlock
            {
                Text = segment.Start.ToString(@"mm\:ss"),
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 8.5
            };
            time.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush");

            var confidence = new TextBlock
            {
                Text = segment.Probability > 0 ? $"{Math.Round(segment.Probability * 100)}%" : "—",
                FontSize = 8.5,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            confidence.SetResourceReference(
                TextBlock.ForegroundProperty,
                segment.Probability > 0 && segment.Probability < 0.8f ? "AmberBrush" : "AccentBrush");

            header.Children.Add(time);
            header.Children.Add(confidence);
            block.Children.Add(header);

            var text = new TextBlock
            {
                Text = $"«{segment.Text}»",
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16.5,
                Margin = new Thickness(0, 3, 0, 0)
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "InkSoftBrush");
            block.Children.Add(text);

            var divider = new Border { Height = 1, Margin = new Thickness(0, 9, 0, 0) };
            divider.SetResourceReference(Border.BackgroundProperty, "LineBrush");
            block.Children.Add(divider);

            TranscriptDynamic.Children.Add(block);
            TranscriptCountText.Text = TranscriptDynamic.Children.Count.ToString();
        }

        private int ApplyRecognizedValues(string text)
        {
            var normalized = text.ToLowerInvariant().Replace('ё', 'е');
            var applied = 0;
            var fieldStyle = (Style)FindResource("FieldCell");
            var labelStyle = (Style)FindResource("FieldLabel");

            string? NumberAfter(params string[] keys)
            {
                foreach (var key in keys)
                {
                    var index = normalized.IndexOf(key, StringComparison.Ordinal);
                    if (index < 0) continue;

                    var start = index + key.Length;
                    var tail = normalized.Substring(start, Math.Min(50, normalized.Length - start));
                    var match = Regex.Match(tail, @"(\d+(?:[.,]\d+)?)");
                    if (match.Success) return match.Groups[1].Value.Replace('.', ',');
                }

                return null;
            }

            TextBox? FindField(string labelPrefix)
            {
                foreach (var border in FindVisualChildren<Border>(MainScroll))
                {
                    if (!ReferenceEquals(border.Style, fieldStyle)) continue;

                    var label = FindVisualChildren<TextBlock>(border)
                        .FirstOrDefault(t => ReferenceEquals(t.Style, labelStyle));
                    if (label is null) continue;
                    if (!label.Text.StartsWith(labelPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                    return FindVisualChildren<TextBox>(border).FirstOrDefault();
                }

                return null;
            }

            void SetField(string? value, string labelPrefix)
            {
                if (value is null) return;
                var box = FindField(labelPrefix);
                if (box is null) return;
                box.Text = value;
                applied++;
            }

            void SetNamed(string? value, string name)
            {
                if (value is null || FindName(name) is not TextBox box) return;
                box.Text = value;
                applied++;
            }

            SetField(NumberAfter("фракция выброса", "фв"), "ФВ, В режим");
            SetField(NumberAfter("ударный объем", "уо"), "УО, мл");
            SetField(NumberAfter("конечно-диастолический объем", "диастолический объем", "диасталлический объем", "диастоллический объем", "кдо"), "КДО, мл");
            SetField(NumberAfter("конечно-систолический объем", "систолический объем", "ксо"), "КСО, мл");
            SetField(NumberAfter("кдр"), "КДР / КДД");
            SetField(NumberAfter("кср"), "КСР / КСД");
            SetField(NumberAfter("мжп"), "МЖП, мм");
            SetField(NumberAfter("зслж"), "ЗСЛЖ, мм");
            SetField(NumberAfter("аорта ", "аорта,", "аорта:"), "Аорта (кольцо)");
            SetField(NumberAfter("сдла"), "СДЛА");
            SetNamed(NumberAfter("пиковый градиент"), "AorticPeakGradientInput");
            SetNamed(NumberAfter("средний градиент"), "AorticMeanGradientInput");
            var area = NumberAfter("площадь отверстия");
            if (area == "08") area = "0,8";
            SetNamed(area, "AorticAreaInput");

            if (Regex.IsMatch(normalized, @"(?:^|\s)(?:e|е)\s*7(?:\s|$|[.,])"))
            {
                SetField("0,7", "E/A");
            }

            var eOverE = Regex.Match(normalized,
                @"(?:(?:e|е)\s*(?:[/\\]|на)\s*(?:e|е)|ене|ена\s*е)[^\d]{0,12}(\d+(?:[.,]\d+)?)");
            if (eOverE.Success) SetField(eOverE.Groups[1].Value.Replace('.', ','), "E/e'");

            var eOverA = Regex.Match(normalized,
                @"(?:(?:e|е)\s*(?:[/\\]|на)\s*(?:a|а)|ена(?!\s*е)|(?:e|е)\s*на\s*(?!(?:e|е)))[^\d]{0,12}(\d+(?:[.,]\d+)?)");
            if (eOverA.Success) SetField(eOverA.Groups[1].Value.Replace('.', ','), "E/A");

            return applied;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed) yield return typed;

                foreach (var descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }

        private void OrganSoon_Click(object sender, RoutedEventArgs e)
        {
            var organ = (sender as FrameworkElement)?.Tag as string ?? "Этот орган";
            ShowStub("Орган пока недоступен",
                $"Протокол для области «{organ}» появится в следующих версиях. Сейчас в программе реализован только анализ сердца.",
                "Один протокол — один орган. Модуль находится в разработке.");
        }

        private void ShowAllTranscript_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_lastTranscriptText))
            {
                var text = _lastTranscriptText.Length > 1200
                    ? _lastTranscriptText[..1200] + "…"
                    : _lastTranscriptText;

                StubDialog.Present(this, "Полный транскрипт", text,
                    "Локальная модель Whisper Small · офлайн");
                return;
            }

            ShowStub("Полный транскрипт",
                "Будет открыт журнал всех распознанных фрагментов с временными метками, уверенностью и привязкой к полям протокола.");
        }

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
