using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;

namespace ModbusTester.Core.Services;

/// <summary>
/// What is known about one connected device's unit selector registers (e.g. System unit 401), as needed by profile
/// writes whose unit / range depend on them (Reg 223). Values come only from the device: a successful profile read
/// that included the selector, or a verified profile write of it. Nothing is assumed from document defaults.
/// Tied to one Unit ID; the owner clears it whenever the device may have changed (disconnect, other connection type,
/// other profile).
/// </summary>
public sealed class ProfileUnitContext
{
    private readonly Dictionary<int, ushort> _states = [];
    private byte? _unitId;

    /// <summary>Forgets everything; true if something was known.</summary>
    public bool Clear()
    {
        bool had = _states.Count > 0;
        _states.Clear();
        _unitId = null;
        return had;
    }

    /// <summary>
    /// Takes the selector registers of <paramref name="profile"/> found in successful results (a profile read, or the
    /// read back of a verified profile write). Results for another Unit ID replace what was known.
    /// </summary>
    /// <returns>True if a known state changed.</returns>
    public bool Record(DeviceProfile profile, IEnumerable<ModbusResult> results)
    {
        HashSet<int> selectors = [.. profile.Registers.Select(d => d.UnitSelectorRegister).OfType<int>()];
        bool changed = false;
        foreach (ModbusResult result in results.Where(r => r.IsSuccess && r.Request.Function == ModbusFunction.ReadHoldingRegisters))
        {
            if (_unitId != result.Request.UnitId)
            {
                changed |= _states.Count > 0;
                _states.Clear();
                _unitId = result.Request.UnitId;
            }
            int firstRegister = result.Request.PduAddress + 1;
            for (int i = 0; i < result.Registers.Count; i++)
            {
                int register = firstRegister + i;
                if (!selectors.Contains(register))
                    continue;
                changed |= !_states.TryGetValue(register, out ushort old) || old != result.Registers[i];
                _states[register] = result.Registers[i];
            }
        }
        return changed;
    }

    /// <summary>Known selector states (register number → raw) for this Unit ID; empty for any other device.</summary>
    public IReadOnlyDictionary<int, ushort> For(byte unitId) =>
        _unitId == unitId ? new Dictionary<int, ushort>(_states) : new Dictionary<int, ushort>();
}
