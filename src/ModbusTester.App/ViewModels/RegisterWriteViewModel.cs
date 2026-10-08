using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModbusTester.App.Validation;
using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;
using ModbusTester.Core.Services;

namespace ModbusTester.App.ViewModels;

/// <summary>What the Register Write area needs from the main window: one connection, one busy flag, one log.</summary>
internal interface IRegisterWriteHost
{
    ModbusService Service { get; }
    CommunicationLogger Logger { get; }
    bool IsConnected { get; }
    /// <summary>Shared with Connect / Read / Disconnect, so a write blocks them and vice versa.</summary>
    bool IsBusy { get; set; }
    AddressBase AddressBase { get; }
    DeviceProfile Profile { get; }
    /// <summary>Unit ID (TCP) or Slave ID (RTU) of the active connection type, parsed from the current text.</summary>
    Parsed<int> CurrentUnitId();
    CancellationToken OperationToken { get; }
    /// <summary>A write was verified (read back equal to what was written): refresh the table rows that show those registers.</summary>
    void ApplyVerifiedRead(ModbusResult readBack);
}

public enum WriteStatus
{
    NotReady,
    Ready,
    ValidationError,
    WriteOnHold,
    Cancelled,
    Writing,
    Completed,
    WriteFailed,
    VerificationMismatch,
    VerificationFailed,
}

/// <summary>Text of the "are you sure" dialog, built from an already prepared write (nothing is re-computed).</summary>
public sealed record WriteConfirmation(string Title, string Message);

public sealed record WritableRegisterOption(RegisterDefinition Definition)
{
    public string Text => $"{Definition.RegisterNumber} {Definition.Description}";
    public override string ToString() => Text;
}

/// <summary>
/// Register Write area. Two modes, following the selected Device Profile:
/// <list type="bullet">
/// <item>Profile: pick a writable register, enter an engineering value or pick a state. Preview, validation and the
/// write itself all go through <see cref="ProfileWriteService"/> / <see cref="RegisterWriteEncoder"/> – no profile
/// rule is repeated here. Always FC06.</item>
/// <item>Generic: FC06 / FC16 with raw words at an Address in the current Address Base; Quantity = number of values.</item>
/// </list>
/// Every write asks for confirmation, then reads the registers back with FC03 and compares the raw words.
/// </summary>
public sealed partial class RegisterWriteViewModel : ObservableObject
{
    private readonly IRegisterWriteHost _host;

    /// <summary>Asked before anything is sent; true = send. Replaced in tests.</summary>
    internal Func<WriteConfirmation, bool> Confirm { get; set; } = ShowConfirmation;

    internal RegisterWriteViewModel(IRegisterWriteHost host)
    {
        _host = host;
        _selectedGenericFunction = GenericFunctions[0];
        LoadProfile();
    }

    // ---- Mode ----

    public bool IsProfileMode => !_host.Profile.IsGeneric;
    public bool IsGenericMode => _host.Profile.IsGeneric;

    // Which value input is shown (layout only).
    public bool ShowNumericInput => IsProfileMode && !IsStatusRegister;
    public bool ShowStateInput => IsProfileMode && IsStatusRegister;
    public bool HasRegister => Definition is not null;
    public bool HasPreview => PreviewRaw.Length > 0;

    // ---- Profile mode ----

    /// <summary>Profile writes are FC06 only; shown fixed and disabled (FC16 support of the device is not claimed).</summary>
    public IReadOnlyList<Option<ModbusFunction>> ProfileFunctions { get; } =
        [new(ProfileWriteService.WriteFunction, "FC06 - Write Single Register")];

    public Option<ModbusFunction> ProfileFunction => ProfileFunctions[0];

    /// <summary>The registers of the selected profile that its metadata marks writable (read-only ones are not listed).</summary>
    public ObservableCollection<WritableRegisterOption> WritableRegisters { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusRegister), nameof(IsNumericRegister), nameof(StateOptions), nameof(UnitText),
        nameof(RangeText), nameof(ScaleText), nameof(HoldReason), nameof(IsOnHold), nameof(ValueLabel),
        nameof(ShowNumericInput), nameof(ShowStateInput), nameof(HasRegister))]
    [NotifyCanExecuteChangedFor(nameof(ResetToDefaultCommand))]
    private WritableRegisterOption? _selectedRegister;

    /// <summary>Engineering value text for numeric profile registers.</summary>
    [ObservableProperty] private string _valueText = "";

    /// <summary>Selected state name for Status profile registers (one of <see cref="StateOptions"/>).</summary>
    [ObservableProperty] private string? _selectedState;

    private RegisterDefinition? Definition => SelectedRegister?.Definition;

    public bool IsStatusRegister => Definition?.DataType == DataType.Status;
    public bool IsNumericRegister => Definition is not null && !IsStatusRegister;

    /// <summary>State names straight from <see cref="RegisterDefinition.States"/>, in raw-value order.</summary>
    public IReadOnlyList<string> StateOptions =>
        Definition is { DataType: DataType.Status } d ? [.. d.States.OrderBy(s => s.Key).Select(s => s.Value.Text)] : [];

    // Metadata shown as-is (display only; validation is the Core's).
    public string UnitText => Definition?.Unit ?? "";
    public string RangeText => Definition is { Min: { } min, Max: { } max } ? $"{Format(min)} ~ {Format(max)}" : "";
    public string ScaleText => Definition is null || IsStatusRegister ? "" : Definition.Scale is { } s ? Format(s) : "-";
    public string? HoldReason => Definition?.WriteHoldReason;
    public bool IsOnHold => HoldReason is not null;

    // ---- Generic mode ----

    public IReadOnlyList<Option<ModbusFunction>> GenericFunctions { get; } =
    [
        new(ModbusFunction.WriteSingleRegister, "FC06 - Write Single Register"),
        new(ModbusFunction.WriteMultipleRegisters, "FC16 - Write Multiple Registers"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueLabel))]
    private Option<ModbusFunction> _selectedGenericFunction;

    [ObservableProperty] private string _genericAddressText = "";
    /// <summary>Raw words: "15" / "0xFFE7" for FC06, "15,25,150" or "0x000F,0x0019" for FC16.</summary>
    [ObservableProperty] private string _genericValuesText = "";

    public string ValueLabel => IsGenericMode
        ? SelectedGenericFunction.Value == ModbusFunction.WriteMultipleRegisters ? "Raw Values" : "Raw Value"
        : IsStatusRegister ? "Value" : "Engineering Value";

    // ---- Preview / status ----

    [ObservableProperty] private string _previewEngineering = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private string _previewRaw = "";
    [ObservableProperty] private string _previewHex = "";
    [ObservableProperty] private string _previewPdu = "";
    [ObservableProperty] private string _previewQuantity = "";
    [ObservableProperty] private string _validationMessage = "";
    [ObservableProperty] private WriteStatus _status = WriteStatus.NotReady;
    [ObservableProperty] private string _statusText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(WriteCommand), nameof(ResetToDefaultCommand))]
    private bool _isWriting;

    /// <summary>The prepared write the preview shows; the Write command sends exactly this (after a final re-check).</summary>
    private PreparedWrite? _prepared;

    private sealed record PreparedWrite(ModbusWriteRequest Request, ProfileWriteResult? Profile, WriteConfirmation Confirmation);

    partial void OnSelectedRegisterChanged(WritableRegisterOption? value)
    {
        ValueText = "";
        SelectedState = null;
        Refresh();
    }

    partial void OnValueTextChanged(string value) => Refresh();
    partial void OnSelectedStateChanged(string? value) => Refresh();
    partial void OnSelectedGenericFunctionChanged(Option<ModbusFunction> value) => Refresh();
    partial void OnGenericAddressTextChanged(string value) => Refresh();
    partial void OnGenericValuesTextChanged(string value) => Refresh();

    /// <summary>The profile changed: forget every profile selection, value, preview and message, then reload.</summary>
    internal void LoadProfile()
    {
        WritableRegisters.Clear();
        foreach (RegisterDefinition d in _host.Profile.Registers.Where(r => r.IsWritable).OrderBy(r => r.RegisterNumber))
            WritableRegisters.Add(new WritableRegisterOption(d));
        SelectedRegister = null;   // clears value/state and refreshes
        OnPropertyChanged(nameof(IsProfileMode));
        OnPropertyChanged(nameof(IsGenericMode));
        OnPropertyChanged(nameof(ValueLabel));
        OnPropertyChanged(nameof(ShowNumericInput));
        OnPropertyChanged(nameof(ShowStateInput));
        Refresh();
    }

    /// <summary>Re-runs the preview (inputs, Address Base or Unit ID changed) and the Write button state.</summary>
    internal void Refresh()
    {
        if (IsWriting)
            return;
        _prepared = null;
        PreviewEngineering = PreviewRaw = PreviewHex = PreviewPdu = PreviewQuantity = ValidationMessage = "";
        if (IsProfileMode)
            PreviewProfile();
        else
            PreviewGeneric();
        WriteCommand.NotifyCanExecuteChanged();
        ResetToDefaultCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Connection / busy state changed.</summary>
    internal void RefreshCommands()
    {
        WriteCommand.NotifyCanExecuteChanged();
        ResetToDefaultCommand.NotifyCanExecuteChanged();
    }

    private string? ProfileInput => IsStatusRegister ? SelectedState : ValueText;

    /// <summary>The register's address in the user's Address Base (the Core converts it to the PDU address once).</summary>
    private int ProfileAddress(RegisterDefinition d) => _host.AddressBase == AddressBase.OneBased ? d.RegisterNumber : d.PduAddress;

    private byte PreviewUnitId => _host.CurrentUnitId() is { IsValid: true } id ? (byte)id.Value : (byte)1;

    private void PreviewProfile()
    {
        if (Definition is not { } d)
        {
            SetStatus(WriteStatus.NotReady, "Select a register");
            return;
        }
        if (string.IsNullOrWhiteSpace(ProfileInput))
        {
            SetStatus(d.WriteHoldReason is null ? WriteStatus.NotReady : WriteStatus.WriteOnHold,
                d.WriteHoldReason is null ? (IsStatusRegister ? "Select a value" : "Enter a value") : "Write on hold");
            return;
        }

        ProfileWriteResult result = ProfileWriteService.Prepare(_host.Profile, PreviewUnitId, ProfileAddress(d), _host.AddressBase, ProfileInput);
        if (result.Encoding is { IsValid: true } encoding && result.Request is { } request)
        {
            PreviewEngineering = IsStatusRegister ? ProfileInput!.Trim() : JoinUnit(Format(encoding.EngineeringValue!.Value), d.Unit);
            PreviewRaw = encoding.Raw!.Value.ToString(CultureInfo.InvariantCulture);
            PreviewHex = encoding.Raw.Value.ToString("X4", CultureInfo.InvariantCulture);
            PreviewPdu = $"0x{request.PduAddress:X4}";
            PreviewQuantity = "1";
        }

        switch (result.Error)
        {
            case ProfileWriteError.None:
                _prepared = new PreparedWrite(result.Request!, result, ProfileConfirmation(result));
                SetStatus(WriteStatus.Ready, "Ready");
                break;
            case ProfileWriteError.WriteOnHold:
                SetStatus(WriteStatus.WriteOnHold, "Write on hold");
                break;
            default:
                ValidationMessage = result.Message;
                SetStatus(WriteStatus.ValidationError, "Validation error");
                break;
        }
    }

    private void PreviewGeneric()
    {
        ModbusFunction function = SelectedGenericFunction.Value;
        bool single = function == ModbusFunction.WriteSingleRegister;
        if (string.IsNullOrWhiteSpace(GenericAddressText) && string.IsNullOrWhiteSpace(GenericValuesText))
        {
            SetStatus(WriteStatus.NotReady, "Enter Address and Raw Value");
            return;
        }
        Parsed<int> address = InputValidator.Address(GenericAddressText, _host.AddressBase);
        Parsed<IReadOnlyList<ushort>> values = InputValidator.RawValues(GenericValuesText, single ? 1 : ModbusPdu.MaxWriteRegisters);
        string? error = address.Error ?? values.Error;
        ModbusWriteRequest? request = null;
        if (error is null)
        {
            request = single
                ? ModbusWriteRequest.Single(PreviewUnitId, address.Value, values.Value[0], _host.AddressBase)
                : ModbusWriteRequest.Multiple(PreviewUnitId, address.Value, values.Value, _host.AddressBase);
            try
            {
                PreviewPdu = $"0x{request.PduAddress:X4}";
            }
            catch (ArgumentOutOfRangeException)
            {
                error = "Address + Quantity exceeds 65536.";
            }
        }
        if (values.IsValid)
        {
            PreviewQuantity = values.Value.Count.ToString(CultureInfo.InvariantCulture);
            PreviewRaw = string.Join(", ", values.Value);
            PreviewHex = string.Join(", ", values.Value.Select(v => v.ToString("X4", CultureInfo.InvariantCulture)));
        }
        if (error is not null)
        {
            ValidationMessage = error;
            SetStatus(WriteStatus.ValidationError, "Validation error");
            return;
        }
        _prepared = new PreparedWrite(request!, null, GenericConfirmation(request!));
        SetStatus(WriteStatus.Ready, "Ready");
    }

    private void SetStatus(WriteStatus status, string text)
    {
        Status = status;
        StatusText = text;
    }

    // ---- Commands ----

    private bool CanWrite() => _prepared is not null && !IsWriting
        && _host.IsConnected && !_host.IsBusy && _host.CurrentUnitId().IsValid;

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        if (_prepared is not { } prepared)
            return;
        Parsed<int> unit = _host.CurrentUnitId();
        if (!unit.IsValid)
        {
            _host.Logger.Log(LogKind.Fail, $"Validation FAIL: {unit.Error}");
            return;
        }
        if (!Confirm(prepared.Confirmation))
        {
            SetStatus(WriteStatus.Cancelled, "Write cancelled");
            return;
        }

        IsWriting = true;
        _host.IsBusy = true;
        SetStatus(WriteStatus.Writing, "Writing...");
        try
        {
            ModbusWriteRequest request = prepared.Request with { UnitId = (byte)unit.Value };
            if (prepared.Profile is { Definition: { } definition })
                await WriteProfileAsync(definition, request);
            else
                await WriteGenericAsync(request);
        }
        catch (OperationCanceledException)
        {
            SetStatus(WriteStatus.Cancelled, "Write cancelled");
        }
        finally
        {
            _host.IsBusy = false;
            IsWriting = false;
            RefreshCommands();
        }
    }

    /// <summary>Profile: the write goes through <see cref="ProfileWriteService"/> (which validates again, Core = last line of defence).</summary>
    private async Task WriteProfileAsync(RegisterDefinition definition, ModbusWriteRequest prepared)
    {
        ProfileWriteResult result = await new ProfileWriteService(_host.Service).WriteAsync(
            _host.Profile, prepared.UnitId, prepared.StartAddress, prepared.AddressBase, ProfileInput, _host.OperationToken);
        if (result.Error == ProfileWriteError.WriteOnHold)
        {
            SetStatus(WriteStatus.WriteOnHold, "Write on hold");
            return;
        }
        if (result.Transport is null)
        {
            ValidationMessage = result.Message;
            SetStatus(WriteStatus.ValidationError, "Validation error");
            return;
        }
        if (!result.Transport.IsSuccess)
        {
            SetStatus(WriteStatus.WriteFailed, $"Write failed: {result.Transport.Status}");
            return;
        }
        ModbusFunction readFunction = _host.Profile.RequiredReadFunction ?? ModbusFunction.ReadHoldingRegisters;
        await VerifyAsync(result.Request!, readFunction, readBack =>
        {
            DecodedRegister decoded = RegisterDecoder.DecodeEngineering(result.Request!.StartAddress, definition.PduAddress, readBack[0], definition);
            return JoinUnit(decoded.ValueText, decoded.Definition?.DataType == DataType.Status ? "" : definition.Unit);
        });
    }

    /// <summary>Generic: raw words straight to the Phase 4A service; read back compares raw words only (no decoding).</summary>
    private async Task WriteGenericAsync(ModbusWriteRequest request)
    {
        ModbusWriteResult result = await _host.Service.WriteRegistersAsync(request, _host.OperationToken);
        if (!result.IsSuccess)
        {
            SetStatus(WriteStatus.WriteFailed, $"Write failed: {result.Status}");
            return;
        }
        await VerifyAsync(request, ModbusFunction.ReadHoldingRegisters, readBack => string.Join(", ", readBack));
    }

    /// <summary>
    /// Reads the written registers back and compares raw words. A write that succeeded stays a success: a different
    /// value or a failed read is reported as a verification problem, never as a failed write.
    /// </summary>
    private async Task VerifyAsync(ModbusWriteRequest written, ModbusFunction readFunction, Func<IReadOnlyList<ushort>, string> describe)
    {
        _host.Logger.Info($"Verify: read back {Describe(written)}");
        var readRequest = new ModbusRequest(readFunction, written.UnitId, written.StartAddress, written.Quantity, written.AddressBase);
        ModbusResult readBack = await _host.Service.ReadRegistersAsync(readRequest, _host.OperationToken);
        if (!readBack.IsSuccess)
        {
            SetStatus(WriteStatus.VerificationFailed, $"Write succeeded, verification failed: {readBack.Status}");
            return;
        }
        if (!readBack.Registers.SequenceEqual(written.Values))
        {
            string detail = $"wrote {string.Join(", ", written.Values)}, read back {string.Join(", ", readBack.Registers)}";
            _host.Logger.Log(LogKind.Fail, $"Result: FAIL [Verification mismatch] - {detail}");
            SetStatus(WriteStatus.VerificationMismatch, $"Write succeeded, verification mismatch ({detail})");
            return;
        }
        // Only a verified write reaches the table, using this read back (no extra request).
        _host.ApplyVerifiedRead(readBack);
        SetStatus(WriteStatus.Completed, $"Write completed (read back {describe(readBack.Registers)})");
    }

    private bool CanResetToDefault() => IsProfileMode && Definition?.DefaultValue is not null && !IsWriting;

    /// <summary>Puts the document default back into the input only. Nothing is sent: Write + Confirm are still needed.</summary>
    [RelayCommand(CanExecute = nameof(CanResetToDefault))]
    private void ResetToDefault()
    {
        if (Definition is not { DefaultValue: { } value } d)
            return;
        if (d.DataType != DataType.Status)
        {
            ValueText = Format(value);
            return;
        }
        // The Core maps the default to its raw state; the input shows that state's name.
        if (RegisterWriteEncoder.EncodeDefault(d) is { IsValid: true, Raw: { } raw } && d.States.TryGetValue(raw, out RegisterState? state))
            SelectedState = state.Text;
    }

    // ---- Confirmation text (from the prepared result only) ----

    private WriteConfirmation ProfileConfirmation(ProfileWriteResult result)
    {
        RegisterDefinition d = result.Definition!;
        ushort raw = result.Encoding!.Raw!.Value;
        string valueLine = d.DataType == DataType.Status
            ? $"Value             : {ProfileInput!.Trim()}"
            : $"Engineering Value : {JoinUnit(Format(result.Encoding.EngineeringValue!.Value), d.Unit)}";
        return new WriteConfirmation($"Write Register {d.RegisterNumber}?", string.Join(Environment.NewLine,
            d.Description,
            valueLine,
            $"Raw               : {raw}",
            $"HEX               : {raw:X4}",
            $"Function          : {result.Request!.Function.ToShortName()}"));
    }

    private static WriteConfirmation GenericConfirmation(ModbusWriteRequest request) =>
        new($"Write Address {request.StartAddress}?", string.Join(Environment.NewLine,
            $"Address  : {request.StartAddress} ({BaseText(request.AddressBase)}, PDU 0x{request.PduAddress:X4})",
            $"Function : {request.Function.ToShortName()}",
            $"Quantity : {request.Quantity}",
            $"Raw      : {string.Join(", ", request.Values)}",
            $"HEX      : {string.Join(", ", request.Values.Select(v => v.ToString("X4", CultureInfo.InvariantCulture)))}"));

    private static bool ShowConfirmation(WriteConfirmation confirmation) =>
        (Application.Current?.MainWindow is { } owner
            ? MessageBox.Show(owner, confirmation.Message, confirmation.Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            : MessageBox.Show(confirmation.Message, confirmation.Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel))
        == MessageBoxResult.OK;

    private static string Describe(ModbusWriteRequest request) =>
        $"Address = {request.StartAddress} ({BaseText(request.AddressBase)})  Quantity = {request.Quantity}";

    private static string BaseText(AddressBase addressBase) => addressBase == AddressBase.OneBased ? "1-based" : "0-based";

    private static string JoinUnit(string value, string unit) => unit.Length == 0 ? value : $"{value} {unit}";

    private static string Format(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
}
