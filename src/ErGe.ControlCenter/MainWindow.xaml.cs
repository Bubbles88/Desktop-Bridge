using System.Windows;
using System.Windows.Threading;
using ErGe.Core.Ipc;
using ErGe.OwnerControl.Client;

namespace ErGe.ControlCenter;

public partial class MainWindow : Window
{
    private readonly OwnerControlClient _client = new();
    private readonly DispatcherTimer _refreshTimer;
    private bool _loaded;
    private bool _suppressToggle;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshStateAsync(showErrors: false);

        Loaded += async (_, _) =>
        {
            _loaded = true;
            await RefreshStateAsync(showErrors: true);
            _refreshTimer.Start();
        };

        Closed += (_, _) => _refreshTimer.Stop();
    }

    private async void AlwaysOnToggle_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_loaded || _suppressToggle)
        {
            return;
        }

        await ExecuteAsync(
            () => _client.SetPersistentModeAsync("AlwaysOn"),
            "Persistent mode set to Always On.");
    }

    private async void AlwaysOnToggle_Unchecked(
        object sender,
        RoutedEventArgs e)
    {
        if (!_loaded || _suppressToggle)
        {
            return;
        }

        await ExecuteAsync(
            () => _client.SetPersistentModeAsync("AlwaysOff"),
            "Persistent mode set to Always Off.");
    }

    private async void ConnectNowButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await ExecuteAsync(
            () => _client.SetSessionOverrideAsync("ConnectNow"),
            "Temporary Connect Now override enabled.");

    private async void LocalOnlyButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await ExecuteAsync(
            () => _client.SetSessionOverrideAsync("LocalOnly"),
            "Temporary Local Only override enabled.");

    private async void EmergencyBlockButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await ExecuteAsync(
            () => _client.SetSessionOverrideAsync("EmergencyBlock"),
            "Emergency Block enabled.");

    private async void ClearOverrideButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await ExecuteAsync(
            () => _client.ClearSessionOverrideAsync(),
            "Temporary owner override cleared.");

    private async void RefreshButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await RefreshStateAsync(showErrors: true);

    private async Task ExecuteAsync(
        Func<Task<OwnerControlResponse>> command,
        string successMessage)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);

        try
        {
            var response = await command();

            if (!response.Success || response.State is null)
            {
                MessageText.Text =
                    $"{response.ErrorCode ?? "owner_control_failed"}: {response.Error}";
                return;
            }

            ApplyState(response.State);
            MessageText.Text = successMessage;
        }
        catch (Exception ex)
        {
            SetDisconnected(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshStateAsync(bool showErrors)
    {
        if (_busy)
        {
            return;
        }

        try
        {
            var response = await _client.GetStatusAsync();

            if (!response.Success || response.State is null)
            {
                if (showErrors)
                {
                    MessageText.Text =
                        $"{response.ErrorCode ?? "owner_control_failed"}: {response.Error}";
                }

                return;
            }

            ApplyState(response.State);

            if (showErrors)
            {
                MessageText.Text = "Owner Control connected.";
            }
        }
        catch (Exception ex)
        {
            SetDisconnected(showErrors ? ex.Message : "Core unavailable.");
        }
    }

    private void ApplyState(OwnerControlState state)
    {
        CoreStatusText.Text = state.ConfigurationHealthy
            ? "Connected"
            : "Policy Fault";

        MachineText.Text = state.MachineName;

        SessionStatusText.Text = state.SessionAgentConnected
            ? "Interactive Ready"
            : "No Session Agent";

        AccessStatusText.Text = state.RemoteAiAllowed
            ? "Remote AI allowed"
            : state.LocalAiAllowed
                ? "Local AI only"
                : "AI access blocked";

        PolicyDetailText.Text =
            $"Persistent: {state.PersistentMode}   |   " +
            $"Override: {state.SessionOverride}   |   " +
            $"Effective: {state.EffectiveAccess}";

        _suppressToggle = true;
        try
        {
            var alwaysOn = string.Equals(
                state.PersistentMode,
                "AlwaysOn",
                StringComparison.Ordinal);

            AlwaysOnToggle.IsChecked = alwaysOn;
            AlwaysOnToggle.Content = alwaysOn ? "ON" : "OFF";

            AlwaysOnDescription.Text = alwaysOn
                ? "ErGe will use the persistent Always On policy when availability services are added."
                : "ErGe remains persistently unavailable to AI unless a temporary owner override allows access.";
        }
        finally
        {
            _suppressToggle = false;
        }
    }

    private void SetDisconnected(string message)
    {
        CoreStatusText.Text = "Disconnected";
        SessionStatusText.Text = "Unknown";
        AccessStatusText.Text = "Core unavailable";
        MessageText.Text = message;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AlwaysOnToggle.IsEnabled = !busy;
        ConnectNowButton.IsEnabled = !busy;
        LocalOnlyButton.IsEnabled = !busy;
        EmergencyBlockButton.IsEnabled = !busy;
        ClearOverrideButton.IsEnabled = !busy;
    }
}
