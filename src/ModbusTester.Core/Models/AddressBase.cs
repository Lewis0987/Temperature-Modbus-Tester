namespace ModbusTester.Core.Models;

/// <summary>
/// How the user-entered address maps to the 0-based address sent in the PDU.
/// OneBased: "Register 1" (device documents such as Siemens QFA/QPA) is sent as PDU address 0.
/// ZeroBased: the entered value is sent as-is.
/// </summary>
public enum AddressBase
{
    OneBased,
    ZeroBased,
}
