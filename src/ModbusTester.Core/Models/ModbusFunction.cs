namespace ModbusTester.Core.Models;

public enum ModbusFunction : byte
{
    ReadCoils = 0x01,
    ReadDiscreteInputs = 0x02,
    ReadHoldingRegisters = 0x03,
    ReadInputRegisters = 0x04,
    WriteSingleCoil = 0x05,
    WriteSingleRegister = 0x06,
    WriteMultipleCoils = 0x0F,
    WriteMultipleRegisters = 0x10,
}

public static class ModbusFunctionExtensions
{
    public static string ToShortName(this ModbusFunction function) => $"FC{(byte)function:D2}";
}
