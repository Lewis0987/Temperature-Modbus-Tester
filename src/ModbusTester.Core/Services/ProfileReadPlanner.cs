using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Protocol;

namespace ModbusTester.Core.Services;

/// <summary>A run of consecutive document register numbers read with one request.</summary>
public sealed record RegisterGroup(int FirstRegister, int Count)
{
    public int LastRegister => FirstRegister + Count - 1;
}

/// <summary>
/// Profile cumulative read: the Address entered by the user means "show every register the profile defines
/// up to this register". Only defined registers are read; gaps in the register map are never requested,
/// and consecutive registers are combined into one FC03/FC04 request each.
/// </summary>
public static class ProfileReadPlanner
{
    /// <summary>Profile definitions with RegisterNumber ≤ <paramref name="upToRegister"/>, ascending.</summary>
    public static IReadOnlyList<RegisterDefinition> Select(DeviceProfile profile, int upToRegister) =>
        profile.Registers.Where(r => r.RegisterNumber <= upToRegister).OrderBy(r => r.RegisterNumber).ToArray();

    /// <summary>Splits register numbers into runs of consecutive numbers, at most <paramref name="maxPerGroup"/> long.</summary>
    public static IReadOnlyList<RegisterGroup> Group(IEnumerable<int> registerNumbers, int maxPerGroup = ModbusPdu.MaxReadRegisters)
    {
        var groups = new List<RegisterGroup>();
        int first = 0, count = 0;
        foreach (int register in registerNumbers.Distinct().Order())
        {
            if (count > 0 && register == first + count && count < maxPerGroup)
            {
                count++;
                continue;
            }
            if (count > 0)
                groups.Add(new RegisterGroup(first, count));
            first = register;
            count = 1;
        }
        if (count > 0)
            groups.Add(new RegisterGroup(first, count));
        return groups;
    }

    /// <summary>
    /// The requests for "up to <paramref name="address"/>". The user address is turned into a document register
    /// number once (for the selection); each request carries a user-facing start address in the same
    /// <paramref name="addressBase"/>, so <see cref="ModbusRequest.PduAddress"/> is still the only PDU conversion.
    /// A function prescribed by the profile (<see cref="DeviceProfile.RequiredReadFunction"/>) always wins over
    /// <paramref name="function"/>.
    /// </summary>
    public static IReadOnlyList<ModbusRequest> Plan(DeviceProfile profile, int address, AddressBase addressBase, byte unitId,
        ModbusFunction function = ModbusFunction.ReadHoldingRegisters)
    {
        function = profile.RequiredReadFunction ?? function;
        int upToRegister = addressBase == AddressBase.OneBased ? address : address + 1;
        IEnumerable<int> selected = Select(profile, upToRegister).Select(r => r.RegisterNumber);
        return Group(selected)
            .Select(g => new ModbusRequest(function, unitId,
                addressBase == AddressBase.OneBased ? g.FirstRegister : g.FirstRegister - 1, (ushort)g.Count, addressBase))
            .ToArray();
    }
}

/// <summary>Outcome of a profile cumulative read: one result per request actually sent, in send order.</summary>
public sealed record ProfileReadResult(IReadOnlyList<ModbusRequest> Plan, IReadOnlyList<ModbusResult> Results)
{
    /// <summary>Every planned request was sent and succeeded.</summary>
    public bool IsSuccess => Plan.Count > 0 && Results.Count == Plan.Count && Results.All(r => r.IsSuccess);

    /// <summary>The request that failed (the read stops there), or null.</summary>
    public ModbusResult? Failure => Results.FirstOrDefault(r => !r.IsSuccess);

    public double ElapsedMs => Results.Sum(r => r.ElapsedMs);
}

public static class ProfileReader
{
    /// <summary>
    /// Sends the planned requests one after another through the normal <see cref="ModbusService"/> path (so every
    /// request is logged with its real Tx/Rx). Stops at the first failure: the whole read is then FAIL, and
    /// requests after it are not sent.
    /// </summary>
    public static async Task<ProfileReadResult> ReadAsync(ModbusService service, IReadOnlyList<ModbusRequest> plan,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ModbusResult>(plan.Count);
        foreach (ModbusRequest request in plan)
        {
            ModbusResult result = await service.ReadRegistersAsync(request, cancellationToken);
            results.Add(result);
            if (!result.IsSuccess)
                break;
        }
        return new ProfileReadResult(plan, results);
    }
}
