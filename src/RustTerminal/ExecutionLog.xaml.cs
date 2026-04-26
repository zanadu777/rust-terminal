using PowershellTerminal;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace RustTerminal
{
    /// <summary>
    /// Interaction logic for ExecutionLog.xaml
    /// </summary>
    public partial class ExecutionLog : UserControl
    {
        public static readonly DependencyProperty SourceProperty =
            DependencyProperty.Register(
                nameof(Source),
                typeof(ObservableCollection<CommandExecutionResult>),
                typeof(ExecutionLog),
                new PropertyMetadata(null));

        public static readonly DependencyProperty SelectedExecutionProperty =
            DependencyProperty.Register(
                nameof(SelectedExecution),
                typeof(CommandExecutionResult),
                typeof(ExecutionLog),
                new PropertyMetadata(null));

        public static readonly DependencyProperty ShowDatesProperty =
            DependencyProperty.Register(
                nameof(ShowDates),
                typeof(bool),
                typeof(ExecutionLog),
                new PropertyMetadata(true, OnDisplaySettingsChanged));

        public static readonly DependencyProperty Use24HourClockProperty =
            DependencyProperty.Register(
                nameof(Use24HourClock),
                typeof(bool),
                typeof(ExecutionLog),
                new PropertyMetadata(true, OnDisplaySettingsChanged));

        public static readonly DependencyProperty IsStartVisibleProperty =
            DependencyProperty.Register(
                nameof(IsStartVisible),
                typeof(bool),
                typeof(ExecutionLog),
                new PropertyMetadata(true, OnDisplaySettingsChanged));

        public static readonly DependencyProperty IsStopVisibleProperty =
            DependencyProperty.Register(
                nameof(IsStopVisible),
                typeof(bool),
                typeof(ExecutionLog),
                new PropertyMetadata(true, OnDisplaySettingsChanged));

        public static readonly DependencyProperty IsDurationVisibleProperty =
            DependencyProperty.Register(
                nameof(IsDurationVisible),
                typeof(bool),
                typeof(ExecutionLog),
                new PropertyMetadata(true, OnDisplaySettingsChanged));

        public ExecutionLog()
        {
            InitializeComponent();
            ApplyDisplaySettings();
        }

        public ObservableCollection<CommandExecutionResult>? Source
        {
            get => (ObservableCollection<CommandExecutionResult>?)GetValue(SourceProperty);
            set => SetValue(SourceProperty, value);
        }

        public CommandExecutionResult? SelectedExecution
        {
            get => (CommandExecutionResult?)GetValue(SelectedExecutionProperty);
            set => SetValue(SelectedExecutionProperty, value);
        }

        public bool ShowDates
        {
            get => (bool)GetValue(ShowDatesProperty);
            set => SetValue(ShowDatesProperty, value);
        }

        public bool Use24HourClock
        {
            get => (bool)GetValue(Use24HourClockProperty);
            set => SetValue(Use24HourClockProperty, value);
        }

        public bool IsStartVisible
        {
            get => (bool)GetValue(IsStartVisibleProperty);
            set => SetValue(IsStartVisibleProperty, value);
        }

        public bool IsStopVisible
        {
            get => (bool)GetValue(IsStopVisibleProperty);
            set => SetValue(IsStopVisibleProperty, value);
        }

        public bool IsDurationVisible
        {
            get => (bool)GetValue(IsDurationVisibleProperty);
            set => SetValue(IsDurationVisibleProperty, value);
        }

        private static void OnDisplaySettingsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ExecutionLog)d).ApplyDisplaySettings();
        }

        private void ApplyDisplaySettings()
        {
            if (StartColumn == null || StopColumn == null || DurationColumn == null)
            {
                return;
            }

            StartColumn.Visibility = IsStartVisible ? Visibility.Visible : Visibility.Collapsed;
            StopColumn.Visibility = IsStopVisible ? Visibility.Visible : Visibility.Collapsed;
            DurationColumn.Visibility = IsDurationVisible ? Visibility.Visible : Visibility.Collapsed;

            string timeFormat = Use24HourClock ? "HH:mm:ss" : "hh:mm:ss tt";
            string dateTimeFormat = ShowDates ? $"yyyy-MM-dd {timeFormat}" : timeFormat;

            StartColumn.Binding = new Binding(nameof(CommandExecutionResult.Start))
            {
                StringFormat = dateTimeFormat
            };

            StopColumn.Binding = new Binding(nameof(CommandExecutionResult.Stop))
            {
                StringFormat = dateTimeFormat
            };
        }
    }
}
