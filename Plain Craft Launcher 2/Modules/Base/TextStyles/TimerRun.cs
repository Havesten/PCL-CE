using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using PCL.Core.App.Localization;

namespace PCL;

internal static partial class TextStyles
{
    public class TimerRun : Run, IDisposable
    {
        public delegate void TimerTickDelegate(TimerRun sender);

        public static readonly DependencyProperty UpdateIntervalProperty =
            DependencyProperty.Register(
                nameof(UpdateInterval),
                typeof(TimeSpan),
                typeof(TimerRun),
                new PropertyMetadata(TimeSpan.FromSeconds(1d)));

        private object _isDisposed = false;

        private DispatcherTimer? _timer;

        public TimerRun(TimeSpan interval = default, bool autoStart = false)
        {
            UpdateInterval = interval == TimeSpan.Zero
                ? TimeSpan.FromSeconds(1d)
                : interval;
            AutoStart = autoStart;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>
        ///     计时器按需创建。列表里绝大多数文本 Run 根本不含 §k（随机闪烁），
        ///     每个都造一个 DispatcherTimer 只会白白拖慢文字渲染。
        /// </summary>
        private DispatcherTimer EnsureTimer()
        {
            if (_timer is not null)
                return _timer;
            _timer = new DispatcherTimer { Interval = UpdateInterval };
            _timer.Tick += _TimerTick;
            return _timer;
        }

        private object IsTimerRunning => _timer is not null && _timer.IsEnabled;

        public TimeSpan UpdateInterval
        {
            get => (TimeSpan)GetValue(UpdateIntervalProperty);
            set
            {
                if (value > TimeSpan.Zero) SetValue(UpdateIntervalProperty, value);
            }
        }

        public bool AutoStart { get; set; }

        public void Dispose()
        {
            if ((bool)_isDisposed)
                return;
            _isDisposed = true;
            _timer?.Tick -= _TimerTick;
            _timer?.Stop();
            _timer = null;
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (ReferenceEquals(e.Property, UpdateIntervalProperty) && _timer is not null)
                _timer.Interval = UpdateInterval;
        }

        public event TimerTickDelegate? TimerTick;

        private void _TimerTick(object? sender, EventArgs e)
        {
            TimerTick?.Invoke(this);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (AutoStart)
                StartTimer();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopTimer();
        }

        public void StartTimer()
        {
            if (Dispatcher is null)
            {
                Common.Log(
                    "[TimerRun] Dispatcher is null, unable to run",
                    Common.LogLevel.Critical,
                    userSummary: Lang.Text("Minecraft.Launch.Error.DispatcherUnavailable"));
                return;
            }

            if (!(bool)IsTimerRunning)
                EnsureTimer().Start();
        }

        public void StopTimer()
        {
            if ((bool)IsTimerRunning)
                _timer?.Stop();
        }
    }
}
