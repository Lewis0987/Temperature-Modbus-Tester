using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO.Ports;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModbusTester.App.History;
using ModbusTester.App.Validation;
using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Services;
using ModbusTester.Core.Transport;

namespace ModbusTester.App.ViewModels;

public sealed record Option<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

public enum ConnectionType
{
    Tcp,
    Rtu,
}

/// <summary>
/// Input fields are kept as the exact text the user typed (*Text properties) and validated through
/// <see cref="INotifyDataErrorInfo"/> (red border in WPF). Runtime settings are parsed from that text only
/// at Connect / Read time, so an invalid field can never fall back to a previously valid value.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, INotifyDataErrorInfo, IAsyncDisposable, IRegisterWriteHost
{
    private const int MaxLogEntries = 5000;

    private readonly CommunicationLogger _logger = new();
    private readonly ModbusService _service;
    private readonly Dictionary<int, double> _scales = [];
    private readonly Dictionary<string, string> _errors = [];
    private readonly Func<RtuSettings, ISerialChannel>? _serialChannelFactory;
    private readonly InputHistoryStore _historyStore;
    private readonly List<FieldHistory> _histories = [];
    private CancellationTokenSource _operationCts = new();

    public MainViewModel() : this(null)
    {
    }

    /// <param name="serialChannelFactory">Serial channel for RTU; null uses the real <see cref="SerialPortChannel"/>.</param>
    /// <param name="historyStore">Input history file; null uses <see cref="InputHistoryStore.DefaultPath"/>.</param>
    internal MainViewModel(Func<RtuSettings, ISerialChannel>? serialChannelFactory, InputHistoryStore? historyStore = null)
    {
        _serialChannelFactory = serialChannelFactory;
        _service = new ModbusService(_logger);
        _service.StateChanged += (_, state) => OnUiThread(() => ConnectionState = state);
        _logger.EntryAdded += (_, entry) => OnUiThread(() => AppendLog(entry));

        _historyStore = historyStore ?? new InputHistoryStore(InputHistoryStore.DefaultPath);
        Dictionary<string, List<string>> saved = _historyStore.Load();
        LogHistoryStoreMessages();
        FieldHistory Track(string key, Action<string> apply)
        {
            var history = new FieldHistory(key, saved.GetValueOrDefault(key) ?? [], apply, SaveHistories);
            _histories.Add(history);
            return history;
        }
        TcpHostHistory = Track(HistoryKeys.TcpHost, v => HostText = v);
        TcpUnitIdHistory = Track(HistoryKeys.TcpUnitId, v => UnitIdText = v);
        TcpTimeoutHistory = Track(HistoryKeys.TcpTimeout, v => TimeoutText = v);
        RtuSlaveIdHistory = Track(HistoryKeys.RtuSlaveId, v => SlaveIdText = v);
        RtuTimeoutHistory = Track(HistoryKeys.RtuTimeout, v => RtuTimeoutText = v);
        AddressHistory = Track(HistoryKeys.Address, v => StartAddressText = v);
        QuantityHistory = Track(HistoryKeys.Quantity, v => QuantityText = v);
        DefaultScaleHistory = Track(HistoryKeys.DefaultScale, v => DefaultScaleText = v);

        SelectedFunction = Functions[0];
        SelectedAddressBase = AddressBases[0];
        SelectedParity = Parities.First(p => p.Value == Parity.Even);
        SelectedStopBits = StopBitsOptions[0];
        RefreshComPorts();
        Write = new RegisterWriteViewModel(this);
        ValidateInputs();
    }

    /// <summary>Register Write area (Phase 4C): its own ViewModel, sharing this connection, busy flag and log.</summary>
    public RegisterWriteViewModel Write { get; }

    ModbusService IRegisterWriteHost.Service => _service;
    CommunicationLogger IRegisterWriteHost.Logger => _logger;
    AddressBase IRegisterWriteHost.AddressBase => SelectedAddressBase?.Value ?? AddressBase.OneBased;
    DeviceProfile IRegisterWriteHost.Profile => SelectedDeviceProfile;
    CancellationToken IRegisterWriteHost.OperationToken => _operationCts.Token;

    Parsed<int> IRegisterWriteHost.CurrentUnitId() => ConnectionType switch
    {
        ViewModels.ConnectionType.Tcp => InputValidator.UnitId(UnitIdText),
        ViewModels.ConnectionType.Rtu => InputValidator.SlaveId(SlaveIdText),
        _ => Parsed<int>.Fail("Select Modbus TCP or Modbus RTU."),
    };

    // ---- Connection ----

    /// <summary>Null until the user picks TCP or RTU: both connection areas, Register Read and Connect stay disabled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTcp), nameof(IsRtu), nameof(HasConnectionType))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private ConnectionType? _connectionType;

    public bool HasConnectionType => ConnectionType is not null;

    // Radio button bindings. Only "checked" selects a type; the other radio being unchecked is ignored,
    // so there is no back-and-forth between the two bindings.
    public bool IsTcp
    {
        get => ConnectionType == ViewModels.ConnectionType.Tcp;
        set
        {
            if (value)
                ConnectionType = ViewModels.ConnectionType.Tcp;
        }
    }

    public bool IsRtu
    {
        get => ConnectionType == ViewModels.ConnectionType.Rtu;
        set
        {
            if (value)
                ConnectionType = ViewModels.ConnectionType.Rtu;
        }
    }

    // TCP
    [ObservableProperty] private string _hostText = "192.168.137.189";
    [ObservableProperty] private string _portText = "5020";
    [ObservableProperty] private string _unitIdText = "1";
    [ObservableProperty] private string _timeoutText = "1000";

    // Input histories (each field independent, last 5, persisted). Recorded only after a successful
    // Connect (connection fields) or a PASS Read (Register Read fields). Port has no history.
    public FieldHistory TcpHostHistory { get; }
    public FieldHistory TcpUnitIdHistory { get; }
    public FieldHistory TcpTimeoutHistory { get; }
    public FieldHistory RtuSlaveIdHistory { get; }
    public FieldHistory RtuTimeoutHistory { get; }
    public FieldHistory AddressHistory { get; }
    public FieldHistory QuantityHistory { get; }
    public FieldHistory DefaultScaleHistory { get; }

    // RTU (defaults follow the Siemens QFA/QPA document: 9600 / 8E1, Slave ID 1; all selectable)
    public ObservableCollection<string> ComPorts { get; } = [];
    public IReadOnlyList<int> BaudRates { get; } = [9600, 19200, 38400, 57600, 115200];
    public IReadOnlyList<int> DataBitsOptions { get; } = [8];
    public IReadOnlyList<Option<Parity>> Parities { get; } =
        [new(Parity.None, "None"), new(Parity.Even, "Even"), new(Parity.Odd, "Odd")];
    public IReadOnlyList<Option<StopBits>> StopBitsOptions { get; } =
        [new(StopBits.One, "1"), new(StopBits.Two, "2")];

    [ObservableProperty] private string? _selectedComPort;
    [ObservableProperty] private int _baudRate = 9600;
    [ObservableProperty] private int _dataBits = 8;
    [ObservableProperty] private Option<Parity> _selectedParity;
    [ObservableProperty] private Option<StopBits> _selectedStopBits;
    [ObservableProperty] private string _slaveIdText = "1";
    [ObservableProperty] private string _rtuTimeoutText = "1000";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(ReadCommand), nameof(RefreshComPortsCommand))]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(CanEditConnection))]
    private ConnectionState _connectionState = ConnectionState.Disconnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(ReadCommand), nameof(RefreshComPortsCommand))]
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
    [ObservableProperty] private string _startAddressText = "1";
    [ObservableProperty] private string _quantityText = "6";
    [ObservableProperty] private string _defaultScaleText = "1";

    /// <summary>Generic Modbus (default) or a device document that tells what each register means.</summary>
    public IReadOnlyList<DeviceProfile> DeviceProfiles => DeviceProfile.All;

    [ObservableProperty] private DeviceProfile _selectedDeviceProfile = DeviceProfile.Generic;

    public bool IsGenericProfile => SelectedDeviceProfile.IsGeneric;

    /// <summary>False when the profile prescribes the read function (the Function box is then fixed and disabled).</summary>
    public bool IsFunctionSelectable => SelectedDeviceProfile.RequiredReadFunction is null;

    /// <summary>The user's own Function choice, kept while a profile forces another one and restored afterwards.</summary>
    private Option<ModbusFunction>? _userFunction;

    /// <summary>Last successful read (one result per request) and the Default Scale it used, so a profile change can re-decode without reading again.</summary>
    private (IReadOnlyList<ModbusResult> Results, double DefaultScale)? _lastRead;

    [ObservableProperty] private string _lastResult = "";
    [ObservableProperty] private bool _lastResultPassed;

    public ObservableCollection<RegisterRowViewModel> Rows { get; } = [];
    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    // Any edit re-validates immediately (red border + Connect/Read CanExecute), not only on Connect.
    partial void OnHostTextChanged(string value) => ValidateInputs();
    partial void OnPortTextChanged(string value) => ValidateInputs();
    partial void OnUnitIdTextChanged(string value) { ValidateInputs(); Write?.Refresh(); }
    partial void OnTimeoutTextChanged(string value) => ValidateInputs();
    partial void OnSelectedComPortChanged(string? value) => ValidateInputs();
    partial void OnSlaveIdTextChanged(string value) { ValidateInputs(); Write?.Refresh(); }
    partial void OnRtuTimeoutTextChanged(string value) => ValidateInputs();
    partial void OnSelectedAddressBaseChanged(Option<AddressBase> value) { ValidateInputs(); Write?.Refresh(); }

    // The Write button follows the same connection state and busy flag as Read (no second source of truth).
    partial void OnConnectionStateChanged(ConnectionState value) => Write?.RefreshCommands();
    partial void OnIsBusyChanged(bool value) => Write?.RefreshCommands();
    partial void OnStartAddressTextChanged(string value) => ValidateInputs();
    partial void OnQuantityTextChanged(string value) => ValidateInputs();
    partial void OnDefaultScaleTextChanged(string value) => ValidateInputs();

    /// <summary>
    /// Switching TCP ↔ RTU: abort any pending operation, drop the previous transport and start the new type
    /// as Disconnected. Field contents, their validation state and the log are kept; only the inactive type's
    /// red borders are hidden.
    /// </summary>
    partial void OnConnectionTypeChanged(ConnectionType? value)
    {
        _operationCts.Cancel();
        _operationCts = new CancellationTokenSource();
        _ = _service.ResetAsync();             // raises Disconnected synchronously
        ConnectionState = ConnectionState.Disconnected;
        ClearLastResult();
        ValidateInputs();
        RefreshErrorPresentation();
        Write?.Refresh();
    }

    // ---- Commands ----

    private bool CanConnect() =>
        !IsBusy && !IsConnected && ConnectionState != ConnectionState.Connecting && ConnectionInputsValid;

    private bool ConnectionInputsValid => ConnectionType switch
    {
        ViewModels.ConnectionType.Tcp => !HasError(nameof(HostText), nameof(PortText), nameof(TimeoutText), nameof(UnitIdText)),
        ViewModels.ConnectionType.Rtu => !HasError(nameof(SelectedComPort), nameof(SlaveIdText), nameof(RtuTimeoutText)),
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        // Final gate: parse the current text again; never use an earlier value.
        (IModbusTransport? transport, Action? recordHistory) = ConnectionType switch
        {
            ViewModels.ConnectionType.Tcp => CreateTcpTransport(),
            ViewModels.ConnectionType.Rtu => CreateRtuTransport(),
            _ => (null, null),
        };
        if (transport is null)
            return;

        IsBusy = true;
        try
        {
            // Only values that actually connected go into the history (no refused/timeout/invalid ones).
            if (await _service.ConnectAsync(transport, _operationCts.Token))
                recordHistory?.Invoke();
        }
        catch (OperationCanceledException)
        {
            // Connection type switched while connecting; ResetAsync already reported Disconnected.
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
            ClearLastResult();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearLastResult()
    {
        LastResult = "";
        LastResultPassed = false;
    }

    // Quantity only takes part in Generic reads; a device profile reads its defined registers "up to Address".
    private bool CanRead() => IsConnected && !IsBusy && ConnectionInputsValid
        && !HasError(nameof(StartAddressText), nameof(DefaultScaleText))
        && (!IsGenericProfile || !HasError(nameof(QuantityText)));

    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task ReadAsync()
    {
        // The connection type cannot change while connected, so IsTcp still describes the live transport.
        Parsed<int> id = ConnectionType == ViewModels.ConnectionType.Tcp ? InputValidator.UnitId(UnitIdText) : InputValidator.SlaveId(SlaveIdText);
        Parsed<int> address = InputValidator.Address(StartAddressText, SelectedAddressBase.Value);
        Parsed<int> quantity = IsGenericProfile ? InputValidator.Quantity(QuantityText) : new(0, null);
        Parsed<double> defaultScale = InputValidator.Scale(DefaultScaleText);
        if (!Gate(id, address, quantity, defaultScale))
            return;

        // Generic: one request "Address + Quantity". Profile: one request per run of consecutive defined registers.
        IReadOnlyList<ModbusRequest> plan = IsGenericProfile
            ? [new ModbusRequest(ReadFunction, (byte)id.Value, address.Value, (ushort)quantity.Value, SelectedAddressBase.Value)]
            : ProfileReadPlanner.Plan(SelectedDeviceProfile, address.Value, SelectedAddressBase.Value, (byte)id.Value, ReadFunction);
        if (plan.Count == 0)
        {
            _logger.Log(LogKind.Fail, $"Validation FAIL: {SelectedDeviceProfile.Name} defines no register up to Address {address.Value}.");
            return;
        }

        IsBusy = true;
        try
        {
            ProfileReadResult read = await ProfileReader.ReadAsync(_service, plan, _operationCts.Token);
            // Short verdict only; the full reason (status, message, CRC details...) is in the Communication Log.
            // Any failed request makes the whole read FAIL; rows then keep showing the previous successful read.
            LastResultPassed = read.IsSuccess;
            LastResult = read.IsSuccess ? $"PASS   {read.ElapsedMs:F0} ms" : "FAIL";

            if (read.IsSuccess)
            {
                AddressHistory.Record(address.Value.ToString(CultureInfo.InvariantCulture));
                if (IsGenericProfile)
                    QuantityHistory.Record(quantity.Value.ToString(CultureInfo.InvariantCulture));
                DefaultScaleHistory.Record(defaultScale.Value.ToString(CultureInfo.InvariantCulture));
                _lastRead = (read.Results, defaultScale.Value);
                RebuildRows();
            }
        }
        catch (OperationCanceledException)
        {
            // Connection type switched while reading.
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefreshComPorts() => CanEditConnection;

    /// <summary>Lists the COM ports Windows reports right now; keeps the current selection if it still exists.</summary>
    [RelayCommand(CanExecute = nameof(CanRefreshComPorts))]
    private void RefreshComPorts()
    {
        string? previous = SelectedComPort;
        IReadOnlyList<string> ports = SerialPortChannel.GetPortNames();
        ComPorts.Clear();
        foreach (string port in ports)
            ComPorts.Add(port);
        SelectedComPort = previous is not null && ports.Contains(previous) ? previous : ports.FirstOrDefault();
        _logger.Info(ports.Count == 0 ? "COM ports: none found" : $"COM ports: {string.Join(", ", ports)}");
    }

    private void SaveHistories()
    {
        _historyStore.Save(_histories.ToDictionary(h => h.Key, h => h.Items.ToList()));
        LogHistoryStoreMessages();
    }

    private void LogHistoryStoreMessages()
    {
        foreach (HistoryMessage message in _historyStore.Messages)
            _logger.Log(message.IsError ? LogKind.Error : LogKind.Info, message.Text);
    }

    [RelayCommand]
    private void ClearLog()
    {
        _logger.Clear();
        LogEntries.Clear();
    }

    [RelayCommand]
    private void CopyLog() => Clipboard.SetText(string.Join(Environment.NewLine, LogEntries));

    /// <summary>
    /// Overwrites the Scale of every manually scaled row with Default Scale; rows stay individually editable
    /// afterwards. Rows defined by the selected device profile keep the document scale.
    /// </summary>
    [RelayCommand]
    private void ApplyDefaultScale()
    {
        Parsed<double> scale = InputValidator.Scale(DefaultScaleText);
        if (!Gate(scale))
            return;
        _scales.Clear();
        foreach (RegisterRowViewModel row in Rows.Where(r => r.IsScaleEditable))
            row.Scale = scale.Value;
    }

    /// <summary>Decodes the last successful read with the selected profile (also used when the profile changes).</summary>
    private void RebuildRows()
    {
        Rows.Clear();
        if (_lastRead is not var (results, defaultScale))
            return;
        // Merged and sorted by register, whatever order the requests completed in.
        foreach (DecodedRegister register in RegisterService.Decode(results, SelectedDeviceProfile, a => _scales.GetValueOrDefault(a, defaultScale)))
            Rows.Add(new RegisterRowViewModel(register, (a, scale) => _scales[a] = scale));
    }

    /// <summary>The function actually sent: the profile's prescribed one, otherwise the user's choice (final gate).</summary>
    private ModbusFunction ReadFunction => SelectedDeviceProfile.RequiredReadFunction ?? SelectedFunction.Value;

    partial void OnSelectedDeviceProfileChanged(DeviceProfile? oldValue, DeviceProfile newValue)
    {
        // Driven by profile metadata only: a profile that prescribes a function fixes it; leaving it restores the
        // user's previous choice (e.g. Generic FC04 → A6V12258321 FC03 → Generic FC04 again).
        if (newValue.RequiredReadFunction is { } required)
        {
            if (oldValue?.RequiredReadFunction is null)
                _userFunction = SelectedFunction;
            SelectedFunction = Functions.Single(f => f.Value == required);
        }
        else if (_userFunction is not null)
        {
            SelectedFunction = _userFunction;
            _userFunction = null;
        }

        OnPropertyChanged(nameof(IsGenericProfile));
        OnPropertyChanged(nameof(IsFunctionSelectable));
        ReadCommand.NotifyCanExecuteChanged();
        RebuildRows();
        Write?.LoadProfile();
    }

    // ---- Validation (INotifyDataErrorInfo drives the WPF red border) ----

    // Validation always runs for every field and _errors always holds the truth (used by CanExecute and the
    // Connect gate). Only the *presentation* (what WPF asks for to draw the red border) hides the fields of
    // the inactive connection type. Their values and errors are untouched and reappear when switching back.
    private static readonly string[] TcpFields = [nameof(HostText), nameof(PortText), nameof(UnitIdText), nameof(TimeoutText)];
    private static readonly string[] RtuFields = [nameof(SelectedComPort), nameof(SlaveIdText), nameof(RtuTimeoutText)];

    private bool IsPresented(string propertyName) => ConnectionType switch
    {
        ViewModels.ConnectionType.Tcp => !RtuFields.Contains(propertyName),
        ViewModels.ConnectionType.Rtu => !TcpFields.Contains(propertyName),
        _ => !TcpFields.Contains(propertyName) && !RtuFields.Contains(propertyName),   // nothing selected yet
    };

    public bool HasErrors => _errors.Keys.Any(IsPresented);
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public IEnumerable GetErrors(string? propertyName) =>
        propertyName is not null && IsPresented(propertyName) && _errors.TryGetValue(propertyName, out string? error)
            ? new[] { error }
            : Array.Empty<string>();

    /// <summary>Asks WPF to re-query the TCP and RTU fields, so red borders follow the active connection type.</summary>
    private void RefreshErrorPresentation()
    {
        foreach (string propertyName in TcpFields.Concat(RtuFields))
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
        OnPropertyChanged(nameof(HasErrors));
    }

    /// <summary>Re-validates every field (TCP and RTU fields are validated regardless of the active type).</summary>
    private void ValidateInputs()
    {
        SetError(nameof(HostText), InputValidator.Host(HostText).Error);
        SetError(nameof(PortText), InputValidator.Port(PortText).Error);
        SetError(nameof(UnitIdText), InputValidator.UnitId(UnitIdText).Error);
        SetError(nameof(TimeoutText), InputValidator.TimeoutMs(TimeoutText).Error);
        SetError(nameof(SelectedComPort), SelectedComPort is null ? "Select a COM port (Refresh COM to rescan)." : null);
        SetError(nameof(SlaveIdText), InputValidator.SlaveId(SlaveIdText).Error);
        SetError(nameof(RtuTimeoutText), InputValidator.TimeoutMs(RtuTimeoutText).Error);
        if (SelectedAddressBase is not null)
            SetError(nameof(StartAddressText), InputValidator.Address(StartAddressText, SelectedAddressBase.Value).Error);
        SetError(nameof(QuantityText), InputValidator.Quantity(QuantityText).Error);
        SetError(nameof(DefaultScaleText), InputValidator.Scale(DefaultScaleText).Error);

        ConnectCommand.NotifyCanExecuteChanged();
        ReadCommand.NotifyCanExecuteChanged();
    }

    private void SetError(string propertyName, string? error)
    {
        bool changed = error is null
            ? _errors.Remove(propertyName)
            : !_errors.TryGetValue(propertyName, out string? old) || old != error;
        if (error is not null)
            _errors[propertyName] = error;
        if (!changed)
            return;
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
        OnPropertyChanged(nameof(HasErrors));
    }

    private bool HasError(params string[] propertyNames) => propertyNames.Any(_errors.ContainsKey);

    /// <summary>Logs each invalid field as a validation failure (not a communication error).</summary>
    private bool Gate(params IParsed[] fields)
    {
        bool ok = true;
        foreach (IParsed field in fields)
        {
            if (field.Error is null)
                continue;
            _logger.Log(LogKind.Fail, $"Validation FAIL: {field.Error}");
            ok = false;
        }
        return ok;
    }

    /// <returns>The transport, plus what to record in the field histories if the connection succeeds.</returns>
    private (IModbusTransport?, Action?) CreateTcpTransport()
    {
        Parsed<string> host = InputValidator.Host(HostText);
        Parsed<int> port = InputValidator.Port(PortText);
        Parsed<int> timeout = InputValidator.TimeoutMs(TimeoutText);
        Parsed<int> unitId = InputValidator.UnitId(UnitIdText);
        if (!Gate(host, port, timeout, unitId))
            return (null, null);
        return (new ModbusTcpTransport(new TcpSettings(host.Value, port.Value, timeout.Value)), () =>
        {
            TcpHostHistory.Record(host.Value);   // IP only: Port is not part of any history
            TcpUnitIdHistory.Record(unitId.Value.ToString(CultureInfo.InvariantCulture));
            TcpTimeoutHistory.Record(timeout.Value.ToString(CultureInfo.InvariantCulture));
        });
    }

    private (IModbusTransport?, Action?) CreateRtuTransport()
    {
        Parsed<string> comPort = SelectedComPort is null ? Parsed<string>.Fail("Select a COM port (Refresh COM to rescan).") : new(SelectedComPort, null);
        Parsed<int> slaveId = InputValidator.SlaveId(SlaveIdText);
        Parsed<int> timeout = InputValidator.TimeoutMs(RtuTimeoutText);
        if (!Gate(comPort, slaveId, timeout))
            return (null, null);
        var transport = new ModbusRtuTransport(
            new RtuSettings(comPort.Value, BaudRate, DataBits, SelectedParity.Value, SelectedStopBits.Value, timeout.Value),
            _serialChannelFactory);
        return (transport, () =>
        {
            RtuSlaveIdHistory.Record(slaveId.Value.ToString(CultureInfo.InvariantCulture));
            RtuTimeoutHistory.Record(timeout.Value.ToString(CultureInfo.InvariantCulture));
        });
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

    public ValueTask DisposeAsync()
    {
        _operationCts.Cancel();
        return _service.DisposeAsync();
    }
}
