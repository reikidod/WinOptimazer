using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using WinOptimizer.Infrastructure;
using WinOptimizer.ViewModels;

namespace WinOptimizer.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _copyFeedbackActive;
    private HwndSource? _windowSource;
    private NetworkMonitorWindow? _networkWindow;

    private const int WmXButtonDown = 0x020B;
    private const int XButton1 = 0x0001; // Mouse 4 — назад
    private const int XButton2 = 0x0002; // Mouse 5 — вперёд

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        FitToScreen();
        ApplyScale();
        ApplyBackground();
        AppSettings.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            AppSettings.Changed -= OnSettingsChanged;
            _windowSource?.RemoveHook(WindowProc);
            _windowSource = null;
        };
        DataContext = viewModel;
        Loaded += OnLoaded;

        // Запомненное состояние боковой панели применяем сразу, без анимации.
        SidebarWidth = _viewModel.IsSidebarExpanded ? SidebarExpandedWidth : SidebarCompactWidth;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private const double SidebarCompactWidth = 84;
    private const double SidebarExpandedWidth = 240;

    public static readonly DependencyProperty SidebarWidthProperty = DependencyProperty.Register(
        nameof(SidebarWidth), typeof(double), typeof(MainWindow),
        new PropertyMetadata(SidebarFullWidth, (d, e) => ((MainWindow)d).OnSidebarWidthChanged((double)e.NewValue)));

    /// <summary>Ширина боковой панели: 84 — только иконки, 240 — иконки с подписями.</summary>
    public double SidebarWidth
    {
        get => (double)GetValue(SidebarWidthProperty);
        set => SetValue(SidebarWidthProperty, value);
    }

    private void OnSidebarWidthChanged(double width)
    {
        SidebarColumn.Width = new GridLength(Math.Max(0, width));
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSidebarExpanded))
        {
            AnimateSidebar(_viewModel.IsSidebarExpanded);
        }
    }

    private void AnimateSidebar(bool expanded)
    {
        var target = expanded ? SidebarExpandedWidth : SidebarCompactWidth;
        BeginAnimation(SidebarWidthProperty, null);

        if (!Motion.IsEnabled)
        {
            SidebarWidth = target;
            return;
        }

        var animation = new DoubleAnimation(SidebarWidth, target, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            // Фиксируем итоговое значение и снимаем анимацию, чтобы она не перекрывала дальнейшие изменения.
            BeginAnimation(SidebarWidthProperty, null);
            SidebarWidth = target;
        };
        BeginAnimation(SidebarWidthProperty, animation);
    }

    /// <summary>Размер окна по умолчанию, но не больше ~92% рабочей области: на ноутбуках с масштабом 125–150% всё помещается и читается.</summary>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Floor(area.Width * 0.92));
        Height = Math.Min(Height, Math.Floor(area.Height * 0.92));
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
    }

    private double _lastScale = double.NaN;

    private void OnSettingsChanged()
    {
        ApplyScale();
        ApplyBackground();
        // UseDarkTitleBar только при создании окна — на каждый тик ползунка фона не нужен.
    }

    private string? _loadedBackground;
    private int _lastBlurPct = -1;
    private int _lastDimPct = -1;

    /// <summary>
    /// Фон-картинка с размытием и затемнением.
    /// Картинка декодируется один раз (адаптивное разрешение), blur/dim обновляются точечно без лишних layout-проходов.
    /// Масштаб и положение картинки от размытия не зависят: при движении ползунка кадр не зумится.
    /// </summary>
    private void ApplyBackground()
    {
        var s = AppSettings.Current;
        var path = s.BackgroundImage;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            if (_loadedBackground is not null || BackgroundImage.Source is not null)
            {
                BackgroundImage.Source = null;
                BackgroundImage.Effect = null;
                BackgroundImage.Visibility = Visibility.Collapsed;
                BackgroundDim.Visibility = Visibility.Collapsed;
                _loadedBackground = null;
                _lastBlurPct = -1;
                _lastDimPct = -1;
            }
            return;
        }

        if (_loadedBackground != path)
        {
            try
            {
                // Под размер экрана: не больше 1600 px по ширине — blurEffect заметно легче на меньших битмапах.
                var decodeW = (int)Math.Clamp(SystemParameters.PrimaryScreenWidth, 1280, 1600);

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = decodeW;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();

                BackgroundImage.Source = bmp;
                BackgroundImage.CacheMode = new BitmapCache { EnableClearType = false, RenderAtScale = 1 };
                _loadedBackground = path;
                _lastBlurPct = -1; // force blur reapply for new image
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
            {
                Logger.Error("Не удалось загрузить фон", ex);
                BackgroundImage.Source = null;
                BackgroundImage.Effect = null;
                BackgroundImage.Visibility = Visibility.Collapsed;
                BackgroundDim.Visibility = Visibility.Collapsed;
                _loadedBackground = null;
                return;
            }
        }

        if (_lastBlurPct != s.BackgroundBlurPercent)
        {
            var radius = s.BackgroundBlurPercent * 0.5;
            if (radius < 0.5)
            {
                // Без размытия эффект снимаем — GPU не гоняет gaussian впустую.
                BackgroundImage.Effect = null;
            }
            else
            {
                if (BackgroundImage.Effect is not BlurEffect)
                    BackgroundImage.Effect = BackgroundBlur;
                BackgroundBlur.Radius = radius;
            }
            _lastBlurPct = s.BackgroundBlurPercent;
        }

        if (_lastDimPct != s.BackgroundDimPercent)
        {
            BackgroundDim.Opacity = s.BackgroundDimPercent / 100.0;
            _lastDimPct = s.BackgroundDimPercent;
        }

        if (BackgroundImage.Visibility != Visibility.Visible)
            BackgroundImage.Visibility = Visibility.Visible;
        if (BackgroundDim.Visibility != Visibility.Visible)
            BackgroundDim.Visibility = Visibility.Visible;
    }

    private void ApplyScale()
    {
        var scale = AppSettings.Current.UiScale;
        if (Math.Abs(scale - _lastScale) < 0.001) return;
        _lastScale = scale;
        RootGrid.LayoutTransform = Math.Abs(scale - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowProc);
        NativeMethods.UseDarkTitleBar(this);
    }

    /// <summary>Обрабатывает аппаратные боковые кнопки мыши на уровне окна Win32.</summary>
    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmXButtonDown) return IntPtr.Zero;

        var button = (int)((wParam.ToInt64() >> 16) & 0xffff);
        var navigated = button switch
        {
            XButton1 => _viewModel.NavigateBack(),
            XButton2 => _viewModel.NavigateForward(),
            _ => false,
        };

        if (!navigated) return IntPtr.Zero;
        handled = true;
        return new IntPtr(1);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            // Граница async void: исключение отсюда уронило бы процесс. Окно остаётся рабочим, ошибка — в журнале.
            Logger.Error("Ошибка начальной загрузки", ex);
            ShowInfo("Не удалось загрузить данные", $"{ex.Message}{Environment.NewLine}{Environment.NewLine}Подробности записаны в {AppPaths.LogFile}");
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        // Закрытие посреди применения твиков может оставить систему в промежуточном состоянии — спрашиваем.
        if (!e.Cancel && _viewModel.IsBusy)
        {
            var dialog = new DialogWindow(
                "Идёт применение изменений",
                "Операция ещё не завершена. Если закрыть программу сейчас, часть изменений может остаться незавершённой.",
                primary: "Подождать",
                secondary: null,
                cancel: "Всё равно закрыть")
            {
                Owner = this,
            };

            dialog.ShowDialog();
            e.Cancel = dialog.Choice == DialogChoice.Primary;
        }
    }

    private void OnOpenNetworkMonitor(object sender, RoutedEventArgs e)
    {
        if(_networkWindow is {IsVisible:true}){_networkWindow.Activate();return;}
        _networkWindow=new NetworkMonitorWindow{Owner=this};
        _networkWindow.Closed+=(_,_)=>_networkWindow=null;
        _networkWindow.Show();
    }

    private async void OnCopyReport(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.BuildInfoReport());
        }
        catch (ExternalException ex)
        {
            // Буфер обмена может быть занят другой программой.
            Logger.Error("Не удалось скопировать в буфер обмена", ex);
            ShowInfo("Не удалось скопировать", "Буфер обмена занят другой программой. Повторите попытку.");
            return;
        }

        if (sender is Button button && !_copyFeedbackActive)
        {
            _copyFeedbackActive = true;
            var original = button.Content;
            button.Content = "Скопировано";
            await Task.Delay(1500);
            button.Content = original;
            _copyFeedbackActive = false;
        }
    }

    private void OnSaveReport(object sender, RoutedEventArgs e)
    {
        if (_viewModel.InfoSections.Count == 0)
        {
            ShowInfo("Нет данных", "Сведения о системе ещё не загружены. Нажмите «Обновить» и повторите.");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить характеристики ПК",
            Filter = "Текстовый файл (*.txt)|*.txt",
            FileName = $"pc-info-{DateTime.Now:yyyy-MM-dd}.txt",
            DefaultExt = ".txt",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            // BOM нужен, чтобы Блокнот и другие программы правильно открыли кириллицу.
            File.WriteAllText(dialog.FileName, _viewModel.BuildInfoReport(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Logger.Info($"Характеристики сохранены: {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Не удалось сохранить отчёт", ex);
            ShowInfo("Не удалось сохранить", ex.Message);
        }
    }

    private void OnSaveJsonReport(object sender, RoutedEventArgs e)
    {
        if (_viewModel.InfoSections.Count == 0)
        {
            ShowInfo("Нет данных", "Сведения о системе ещё не загружены. Нажмите «Обновить» и повторите.");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспортировать характеристики ПК в JSON",
            Filter = "JSON-файл (*.json)|*.json",
            FileName = $"pc-info-{DateTime.Now:yyyy-MM-dd}.json",
            DefaultExt = ".json",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var json = JsonSerializer.Serialize(_viewModel.InfoSections,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Logger.Info($"Характеристики экспортированы в JSON: {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error("Не удалось экспортировать характеристики в JSON", ex);
            ShowInfo("Не удалось сохранить JSON", ex.Message);
        }
    }

    private void ShowInfo(string title, string message)
    {
        var dialog = new DialogWindow(title, message, primary: "OK", secondary: null, cancel: string.Empty)
        {
            Owner = this,
        };
        dialog.ShowDialog();
    }
}
