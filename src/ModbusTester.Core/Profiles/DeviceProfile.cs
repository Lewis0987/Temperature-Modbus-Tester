namespace ModbusTester.Core.Profiles;

/// <summary>
/// A device's register map. The <see cref="Generic"/> profile has no definitions, so every register keeps the
/// generic behaviour (raw value × manual scale). Profiles only interpret data; they never affect the transport.
/// </summary>
public sealed class DeviceProfile
{
    private readonly Dictionary<ushort, RegisterDefinition> _byPduAddress;

    public DeviceProfile(string name, string description, IEnumerable<RegisterDefinition> registers)
    {
        Name = name;
        Description = description;
        Registers = registers.OrderBy(r => r.RegisterNumber).ToArray();
        _byPduAddress = Registers.ToDictionary(r => r.PduAddress);
    }

    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<RegisterDefinition> Registers { get; }
    public bool IsGeneric => Registers.Count == 0;

    /// <summary>The definition for a 0-based PDU address (Register N = PDU N - 1), or null if the profile does not define it.</summary>
    public RegisterDefinition? Find(ushort pduAddress) => _byPduAddress.GetValueOrDefault(pduAddress);

    public override string ToString() => Name;

    public static DeviceProfile Generic { get; } = new("Generic Modbus", "No device definition: raw value × manual scale", []);

    /// <summary>All selectable profiles, Generic first (the default).</summary>
    public static IReadOnlyList<DeviceProfile> All { get; } = [Generic, A6V12258321Profile.Profile];
}
