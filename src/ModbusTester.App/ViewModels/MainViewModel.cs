using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModbusTester.Core.Models;
using ModbusTester.Core.Services;
using ModbusTester.Core.Transport;

namespace ModbusTester.App.ViewModels;

public sealed record Option<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxLogEntries = 5000;

    private readonly CommunicationLogger _logger = new();
    private readonly ModbusService _service;
    private readonly Dictionary<int, double> _scales = [];

    public MainViewModel()
    {
        _service = new ModbusService(_logger);
        _service.StateChanged += (_, state) => OnUiThread(() => ConnectionState = state);
        _logger.EntryAdded += (_, entry) => OnUiThread(() => AppendLog(entry));
        SelectedFunction = Functions[0];
        SelectedAddressBase = AddressBases[0];
    }

    // ---- Connection ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private bool _isTcp = true;

    [ObservableProperty] private string _host = "192.168.137.189";
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private int _unitId = 1;
    [ObservableProperty] private int _timeoutMs = 1000;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(ReadCommand))]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(CanEditConnection))]
    private ConnectionState _connectionState = ConnectionState.Disconnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(ReadCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditConnection))]
    private bool _isBusy;

    public bool IsConnected => ConnectionState == ConnectionState.Connected;
    public bool CanEditConnection => !IsConnected && !IsBusy && ConnectionState != ConnectionState.Connecting;

    // ---- Request ----

    public IReadOnlyList<Option<ModbusFunction>> Functions { get; } =
    [
        new(ModbusFunction.ReadHoldingRegisters, "FC03 - Read Holding Registers"),
        new(ModbusFunction.ReadInputRegisters, "FC04 - Read Input Registers"),
    ];

    public IReadOnlyList<Option<AddressBase>> AddressBases { get; } =
    [
        new(AddressBase.OneBased, "1-based (Register No. 1 → PDU 0)"),
        new(AddressBase.ZeroBased, "0-based (PDU address)"),
    ];

    [ObservableProperty] private Option<ModbusFunction> _selectedFunction;
    [ObservableProperty] private Option<AddressBase> _selectedAddressBase;
    [ObservableProperty] private int _startAddress = 1;
    [ObservableProperty] private int _quantity = 6;
    [ObservableProperty] private double _defaultScale = 1;
    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private bool _lastResultPassed;

    public ObservableCollection<RegisterRowViewModel> Rows { get; } = [];
    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    // ---- Commands ----

    private bool CanConnect() => IsTcp && !IsBusy && !IsConnected && ConnectionState != ConnectionState.Connecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (!Validate(Port is >= 1 and <= 65535, "Port must be 1..65535")
            || !Validate(TimeoutMs is >= 50 and <= 60000, "Timeout must be 50..60000 ms")
            || !Validate(!string.IsNullOrWhiteSpace(Host), "IP Address is required"))
            return;

        IsBusy = true;
        try
        {
            var transport = new ModbusTcpTransport(new TcpSettings(Host.Trim(), Port, TimeoutMs));
            await _service.ConnectAsync(transport);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDisconnect() => !IsBusy && ConnectionState != ConnectionState.Disconnected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            await _service.DisconnectAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRead() => IsConnected && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task ReadAsync()
    {
        if (!Validate(UnitId is >= 0 and <= 255, "Unit ID must be 0..255")
            || !Validate(Quantity is >= 1 and <= 125, "Quantity must be 1..125"))
            return;

        var request = new ModbusRequest(SelectedFunction.Value, (byte)UnitId, StartAddress, (ushort)Quantity, SelectedAddressBase.Value);

        IsBusy = true;
        try
        {
            ModbusResult result = await _service.ReadRegistersAsync(request);
            LastResultPassed = result.IsSuccess;
            LastResult = result.IsSuccess
                ? $"PASS   {result.ElapsedMs:F0} ms"
                : $"FAIL   {result.Status}: {result.ErrorMessage}";

            if (result.IsSuccess)
            {
                Rows.Clear();
                foreach (RegisterRow row in RegisterService.BuildRows(result, ScaleFor))
                    Rows.Add(new RegisterRowViewModel(row, (address, scale) => _scales[address] = scale));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        _logger.Clear();
        LogEntries.Clear();
    }

    [RelayCommand]
    private void CopyLog() => Clipboard.SetText(string.Join(Environment.NewLine, LogEntries));

    /// <summary>Applies the default scale to every row (and forgets per-address scales).</summary>
    [RelayCommand]
    private void ApplyDefaultScale()
    {
        _scales.Clear();
        foreach (RegisterRowViewModel row in Rows)
            row.Scale = DefaultScale;
    }

    // ---- Helpers ----

    private double ScaleFor(int address) => _scales.TryGetValue(address, out double scale) ? scale : DefaultScale;

    private bool Validate(bool condition, string message)
    {
        if (!condition)
            _logger.Error($"Input error: {message}");
        return condition;
    }

    private void AppendLog(LogEntry entry)
    {
        LogEntries.Add(entry);
        while (LogEntries.Count > MaxLogEntries)
            LogEntries.RemoveAt(0);
    }

    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    public ValueTask DisposeAsync() => _service.DisposeAsync();
}
