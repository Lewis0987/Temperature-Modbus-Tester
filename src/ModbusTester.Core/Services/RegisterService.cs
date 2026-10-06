using ModbusTester.Core.Models;

namespace ModbusTester.Core.Services;

/// <summary>Turns a read result into display rows: user-facing address, decoded value and scale.</summary>
public static class RegisterService
{
    /// <param name="scaleFor">Scale for a user-facing address; null means 1.</param>
    public static IReadOnlyList<RegisterRow> BuildRows(
        ModbusResult result,
        Func<int, double>? scaleFor = null,
        DataType dataType = DataType.UInt16)
    {
        var rows = new List<RegisterRow>(result.Registers.Count);
        ushort[] registers = [.. result.Registers];
        for (int i = 0; i < registers.Length; i++)
        {
            int address = ModbusAddress.Display(result.Request.StartAddress, i);
            double decoded = RegisterDecoder.Decode(registers.AsSpan(i, 1), dataType);
            rows.Add(new RegisterRow(address, registers[i], decoded, scaleFor?.Invoke(address) ?? 1));
        }
        return rows;
    }
}
