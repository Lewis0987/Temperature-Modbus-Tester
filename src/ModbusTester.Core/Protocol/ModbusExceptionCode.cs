namespace ModbusTester.Core.Protocol;

public enum ModbusExceptionCode : byte
{
    IllegalFunction = 0x01,
    IllegalDataAddress = 0x02,
    IllegalDataValue = 0x03,
    SlaveDeviceFailure = 0x04,
    Acknowledge = 0x05,
    SlaveDeviceBusy = 0x06,
    MemoryParityError = 0x08,
    GatewayPathUnavailable = 0x0A,
    GatewayTargetFailedToRespond = 0x0B,
}

public static class ModbusExceptionCodeExtensions
{
    public static string Describe(this ModbusExceptionCode code) => code switch
    {
        ModbusExceptionCode.IllegalFunction => "Illegal Function",
        ModbusExceptionCode.IllegalDataAddress => "Illegal Data Address",
        ModbusExceptionCode.IllegalDataValue => "Illegal Data Value",
        ModbusExceptionCode.SlaveDeviceFailure => "Slave Device Failure",
        ModbusExceptionCode.Acknowledge => "Acknowledge",
        ModbusExceptionCode.SlaveDeviceBusy => "Slave Device Busy",
        ModbusExceptionCode.MemoryParityError => "Memory Parity Error",
        ModbusExceptionCode.GatewayPathUnavailable => "Gateway Path Unavailable",
        ModbusExceptionCode.GatewayTargetFailedToRespond => "Gateway Target Failed To Respond",
        _ => $"Unknown Exception 0x{(byte)code:X2}",
    };
}
