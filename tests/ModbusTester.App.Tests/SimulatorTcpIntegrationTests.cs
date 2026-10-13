using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using ModbusTester.App.ViewModels;
using ModbusTester.Core.Models;
using ModbusTester.Core.Profiles;
using ModbusTester.Core.Services;
using ModbusTester.Core.Transport;

namespace ModbusTester.App.Tests;

/// <summary>
/// Starts the real tools\pi_modbus_server\modbus_tcp_server.py (current source, its own .venv) on a free
/// 127.0.0.1 port for the integration tests and stops exactly that process tree afterwards. A private instance:
/// the tests never depend on (or disturb) the background simulator on 5020, and always test the current source.
/// </summary>
public sealed class SimulatorServerFixture : IDisposable
{
    private readonly Process _process;
    private readonly string _tempDir;

    public int Port { get; }
    public string LogFile { get; }

    public SimulatorServerFixture()
    {
        string serverDir = Path.Combine(RepoRoot(), "tools", "pi_modbus_server");
        string python = Path.Combine(serverDir, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python))
            throw new InvalidOperationException($"Simulator venv not found: {python}");
        _tempDir = Path.Combine(Path.GetTempPath(), "ModbusTester.SimTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        LogFile = Path.Combine(_tempDir, "server.log");
        Port = FreePort();
        _process = Process.Start(new ProcessStartInfo(python)
        {
            // -B: no __pycache__ in the source folder
            ArgumentList = { "-u", "-B", "modbus_tcp_server.py", "--host", "127.0.0.1", "--port", Port.ToString(), "--log-file", LogFile },
            WorkingDirectory = serverDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect(IPAddress.Loopback, Port);
                return;
            }
            catch (SocketException)
            {
                if (_process.HasExited)
                    throw new InvalidOperationException($"Simulator exited with code {_process.ExitCode}");
                Thread.Sleep(100);
            }
        }
        Dispose();
        throw new TimeoutException("Simulator did not start listening");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    public void Dispose()
    {
        // The venv python.exe is a launcher with the interpreter as child: stop this process tree only.
        try
        {
            using Process kill = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {_process.Id}") { CreateNoWindow = true, UseShellExecute = false })!;
            kill.WaitForExit(10000);
        }
        catch (Exception) { /* already gone */ }
        _process.Dispose();
        try { Directory.Delete(_tempDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SimulatorTcpCollection : ICollectionFixture<SimulatorServerFixture>
{
    public const string Name = "Simulator TCP";
}

/// <summary>
/// Phase 5C – SIMULATOR ONLY: Generic 32-bit read and raw write over real TCP with the real MainViewModel against the
/// simulator's Unit 2 test device (fixture, not a real device), plus Unit 1 (A6V12258321 simulator) regression.
/// Not physical sensor evidence.
/// </summary>
[Collection(SimulatorTcpCollection.Name)]
public class SimulatorTcpIntegrationTests(SimulatorServerFixture server)
{
    private const ushort WritableRegister = 101;
    private static readonly ushort[] WritableBaseline = [0x0001, 0x0002];

    private async Task<MainViewModel> ConnectAsync(string unitId, DeviceProfile? profile = null)
    {
        var vm = new MainViewModel(null, TestVm.TempStore())
        {
            IsTcp = true, HostText = "127.0.0.1", PortText = server.Port.ToString(), UnitIdText = unitId,
            TimeoutText = "2000", SelectedDeviceProfile = profile ?? DeviceProfile.Generic,
        };
        vm.Write.Confirm = _ => true;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
        return vm;
    }

    private static void Set(MainViewModel vm, DataType type, Register32Order order = Register32Order.ABCD)
    {
        vm.SelectedGenericDataType = vm.GenericDataTypes.Single(t => t.Value == type);
        vm.SelectedRegister32Order = vm.Register32Orders.Single(o => o.Value == order);
    }

    private static async Task ReadAsync(MainViewModel vm, string address, string quantity, string scale = "1")
    {
        vm.StartAddressText = address;
        vm.QuantityText = quantity;
        vm.DefaultScaleText = scale;
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.True(vm.LastResultPassed, string.Join(" | ", vm.LogEntries.TakeLast(5)));
    }

    private static string[] Requests(MainViewModel vm, string fc) => [.. vm.LogEntries.Where(e => e.Message.StartsWith(fc + "  ")).Select(e => e.Message)];

    /// <summary>Raw words of Unit 2 read and written directly (Core service, independent of the ViewModel under test).</summary>
    private async Task<ushort[]> UnitWordsAsync(int register, ushort count, ushort[]? write = null)
    {
        await using var service = new ModbusService(new CommunicationLogger());
        Assert.True(await service.ConnectAsync(new ModbusTcpTransport(new TcpSettings("127.0.0.1", server.Port, 2000))));
        if (write is not null)
            Assert.True((await service.WriteMultipleRegistersAsync(2, register, write)).IsSuccess);
        ModbusResult read = await service.ReadRegistersAsync(new ModbusRequest(ModbusFunction.ReadHoldingRegisters, 2, register, count));
        Assert.True(read.IsSuccess);
        return [.. read.Registers];
    }

    private Task RestoreWritableAsync() => UnitWordsAsync(WritableRegister, 2, WritableBaseline);

    // UInt32: the same logical value from all four wire orders
    [Theory]
    [InlineData(1, Register32Order.ABCD, "1234 5678")]
    [InlineData(3, Register32Order.BADC, "3412 7856")]
    [InlineData(5, Register32Order.CDAB, "5678 1234")]
    [InlineData(7, Register32Order.DCBA, "7856 3412")]
    public async Task UInt32_FourOrders(int register, Register32Order order, string rawWords)
    {
        await using MainViewModel vm = await ConnectAsync("2");
        Set(vm, DataType.UInt32, order);

        await ReadAsync(vm, register.ToString(), "2");

        RegisterRowViewModel r = Assert.Single(vm.Rows);
        Assert.Equal((register, rawWords, "12345678", "305419896"), (r.Address, r.RawText, r.Hex, r.ValueText));
        Assert.Equal([$"FC03  Unit ID = 2  Address = {register} (PDU 0x{register - 1:X4}, 1-based)  Quantity = 2"], Requests(vm, "FC03"));
    }

    [Theory]
    [InlineData(9, Register32Order.ABCD, "-1")]
    [InlineData(11, Register32Order.CDAB, "-123")]
    [InlineData(13, Register32Order.ABCD, "-2147483648")]
    [InlineData(15, Register32Order.DCBA, "2147483647")]
    public async Task Int32_Values(int register, Register32Order order, string value)
    {
        await using MainViewModel vm = await ConnectAsync("2");
        Set(vm, DataType.Int32, order);

        await ReadAsync(vm, register.ToString(), "2");

        Assert.Equal(value, Assert.Single(vm.Rows).ValueText);
    }

    [Theory]
    [InlineData(17, Register32Order.ABCD, "1")]
    [InlineData(19, Register32Order.BADC, "1")]
    [InlineData(21, Register32Order.CDAB, "1")]
    [InlineData(23, Register32Order.DCBA, "1")]
    [InlineData(25, Register32Order.ABCD, "-1")]
    [InlineData(27, Register32Order.ABCD, "1.5")]
    public async Task Float32_Values(int register, Register32Order order, string value)
    {
        await using MainViewModel vm = await ConnectAsync("2");
        Set(vm, DataType.Float32, order);

        await ReadAsync(vm, register.ToString(), "2");

        RegisterRowViewModel r = Assert.Single(vm.Rows);
        Assert.Equal((value, RegisterQuality.Ok), (r.ValueText, r.Quality));
    }

    // NaN / ±Infinity: read passes, rows InvalidValue, no communication error in the log
    [Fact]
    public async Task Float32_Special_NotACommunicationError()
    {
        await using MainViewModel vm = await ConnectAsync("2");
        Set(vm, DataType.Float32);

        await ReadAsync(vm, "29", "6");

        Assert.Equal(["NaN", "+Infinity", "-Infinity"], vm.Rows.Select(r => r.ValueText));
        Assert.All(vm.Rows, r => Assert.Equal(RegisterQuality.InvalidValue, r.Quality));
        Assert.All(vm.Rows, r => Assert.Null(r.Value));
        Assert.DoesNotContain(vm.LogEntries, e => e.Kind is LogKind.Fail or LogKind.Error);
        Assert.DoesNotContain(vm.LogEntries, e => e.Message.Contains("Timeout") || e.Message.Contains("CRC") || e.Message.Contains("Exception"));
    }

    // Odd quantity: exactly 3 registers requested, the third word Incomplete
    [Fact]
    public async Task OddQuantity_ThreeRegisters_LastIncomplete()
    {
        await using MainViewModel vm = await ConnectAsync("2");
        Set(vm, DataType.Float32);

        await ReadAsync(vm, "27", "3");

        Assert.Equal(["FC03  Unit ID = 2  Address = 27 (PDU 0x001A, 1-based)  Quantity = 3"], Requests(vm, "FC03"));
        Assert.Equal(["1.5", "Incomplete"], vm.Rows.Select(r => r.ValueText));
        Assert.Equal([RegisterQuality.Ok, RegisterQuality.Incomplete], vm.Rows.Select(r => r.Quality));
        Assert.Equal([27, 29], vm.Rows.Select(r => r.Address));
    }

    // Scale changes the value only
    [Fact]
    public async Task Scale_ValueOnly()
    {
        await using MainViewModel vm = await ConnectAsync("2");
        Set(vm, DataType.Float32);
        await ReadAsync(vm, "27", "2", scale: "1");
        (string raw, string hex, string binary) unscaled = (vm.Rows[0].RawText, vm.Rows[0].Hex, vm.Rows[0].Binary);

        await ReadAsync(vm, "27", "2", scale: "0.1");

        Assert.Equal("0.15", vm.Rows[0].ValueText);
        Assert.Equal(unscaled, (vm.Rows[0].RawText, vm.Rows[0].Hex, vm.Rows[0].Binary));
        Assert.Equal(("3FC0 0000", "3FC00000"), (unscaled.raw, unscaled.hex));
    }

    // FC04: the same values, function code unchanged by the data type / order
    [Fact]
    public async Task Fc04_SameDecoding()
    {
        await using MainViewModel vm = await ConnectAsync("2");
        vm.SelectedFunction = vm.Functions.Single(f => f.Value == ModbusFunction.ReadInputRegisters);
        Set(vm, DataType.UInt32, Register32Order.CDAB);

        await ReadAsync(vm, "5", "2");

        Assert.Equal("305419896", Assert.Single(vm.Rows).ValueText);
        Assert.Single(Requests(vm, "FC04"));
        Assert.Empty(Requests(vm, "FC03"));
    }

    // 0-based address base: same wire address, row address in the user's base
    [Fact]
    public async Task ZeroBased_SameWireAddress()
    {
        await using MainViewModel vm = await ConnectAsync("2");
        vm.SelectedAddressBase = vm.AddressBases.Single(b => b.Value == AddressBase.ZeroBased);
        Set(vm, DataType.UInt32);

        await ReadAsync(vm, "0", "2");

        Assert.Equal((0, (ushort)0, "305419896"), (vm.Rows[0].Address, vm.Rows[0].PduAddress, vm.Rows[0].ValueText));
        Assert.Equal(["FC03  Unit ID = 2  Address = 0 (PDU 0x0000, 0-based)  Quantity = 2"], Requests(vm, "FC03"));
    }

    // Raw FC06 row sync: either word of a 32-bit row → the row is decoded again from both current words
    [Fact]
    public async Task Fc06_RowSync_FirstThenSecondWord_ThenRestore()
    {
        try
        {
            await using MainViewModel vm = await ConnectAsync("2");
            Set(vm, DataType.UInt32);
            await ReadAsync(vm, "101", "2");
            Assert.Equal("65538", vm.Rows[0].ValueText);                 // 0x00010002

            vm.Write.GenericAddressText = "101";
            vm.Write.GenericValuesText = "0x1234";
            await vm.Write.WriteCommand.ExecuteAsync(null);
            Assert.Equal(WriteStatus.Completed, vm.Write.Status);
            Assert.Equal(("12340002", "1234 0002"), (vm.Rows[0].Hex, vm.Rows[0].RawText));   // new first + old second word

            vm.Write.GenericAddressText = "102";
            vm.Write.GenericValuesText = "0x5678";
            await vm.Write.WriteCommand.ExecuteAsync(null);
            Assert.Equal(WriteStatus.Completed, vm.Write.Status);
            Assert.Equal(("12345678", "305419896"), (vm.Rows[0].Hex, vm.Rows[0].ValueText));
            Assert.Single(vm.Rows);
        }
        finally
        {
            await RestoreWritableAsync();
        }
        Assert.Equal(WritableBaseline, await UnitWordsAsync(WritableRegister, 2));
    }

    // Raw FC16 row sync: both words in one write (raw words – not a 32-bit engineering write)
    [Fact]
    public async Task Fc16_RowSync_BothWords_ThenRestore()
    {
        try
        {
            await using MainViewModel vm = await ConnectAsync("2");
            Set(vm, DataType.UInt32);
            await ReadAsync(vm, "101", "2");

            vm.Write.SelectedGenericFunction = vm.Write.GenericFunctions.Single(f => f.Value == ModbusFunction.WriteMultipleRegisters);
            vm.Write.GenericAddressText = "101";
            vm.Write.GenericValuesText = "0x1234,0x5678";
            await vm.Write.WriteCommand.ExecuteAsync(null);

            Assert.Equal("Write completed (read back 4660, 22136)", vm.Write.StatusText);
            Assert.Equal(("305419896", "12345678", "1234 5678"), (vm.Rows[0].ValueText, vm.Rows[0].Hex, vm.Rows[0].RawText));
        }
        finally
        {
            await RestoreWritableAsync();
        }
        Assert.Equal(WritableBaseline, await UnitWordsAsync(WritableRegister, 2));
    }

    // Read-only fixture (simulator rule): a raw write is refused, nothing changes
    [Fact]
    public async Task ReadOnlyFixture_WriteRefused()
    {
        await using MainViewModel vm = await ConnectAsync("2");
        vm.Write.GenericAddressText = "1";
        vm.Write.GenericValuesText = "0";
        await vm.Write.WriteCommand.ExecuteAsync(null);

        Assert.Equal("Write failed: ModbusException", vm.Write.StatusText);
        Assert.Contains(vm.LogEntries, e => e.Message.Contains("Illegal Data Address"));
        Assert.Equal([(ushort)0x1234, (ushort)0x5678], await UnitWordsAsync(1, 2));
    }

    // Unit 1 regression over the same server: A6V profile read unchanged, Unit 1 / Unit 2 isolated
    [Fact]
    public async Task Unit1_A6VUnchanged()
    {
        await using MainViewModel vm = await ConnectAsync("1", A6V12258321Profile.Profile);
        vm.StartAddressText = "1287";
        await vm.ReadCommand.ExecuteAsync(null);

        Assert.True(vm.LastResultPassed);
        Assert.Equal(5, Requests(vm, "FC03").Length);
        Assert.Equal(15, vm.Rows.Count);
        Assert.Equal("1.02.123", vm.SoftwareVersionText);
        Assert.Equal(["25.34", "1.5", "-2.5", "150", "Celsius"], new[] { 1, 223, 224, 225, 401 }.Select(a => vm.Rows.Single(r => r.Address == a).ValueText));
    }

    [Fact]
    public async Task UnitsIsolated_GapsStillException02()
    {
        await using MainViewModel unit1 = await ConnectAsync("1");
        await ReadAsync(unit1, "1", "1");
        Assert.Equal("2534", unit1.Rows[0].ValueText);                       // Unit 1 register 1 is not Unit 2's 0x1234
        unit1.StartAddressText = "300";
        await unit1.ReadCommand.ExecuteAsync(null);
        Assert.False(unit1.LastResultPassed);
        Assert.Contains(unit1.LogEntries, e => e.Message.Contains("Illegal Data Address"));

        await using MainViewModel unit2 = await ConnectAsync("2");
        unit2.StartAddressText = "223";
        unit2.QuantityText = "1";
        await unit2.ReadCommand.ExecuteAsync(null);
        Assert.False(unit2.LastResultPassed);                                 // Unit 2 has no register 223
        Assert.Contains(unit2.LogEntries, e => e.Message.Contains("Illegal Data Address"));
    }
}
