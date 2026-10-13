r"""Tests for modbus_tcp_server.py (Phase 4D: read-only / writable registers, FC06 / FC16, write log, restart).

Each test class starts its own server process on a free 127.0.0.1 port with a temporary log file, so the
background server on port 5020 is never touched.

    .venv\Scripts\python.exe -m unittest test_modbus_tcp_server -v

Exception 02 for a read-only write is this simulator's (pymodbus readonly flag) behaviour, not a claim about
the real Siemens sensor.
"""

import math
import os
import socket
import struct
import subprocess
import sys
import tempfile
import time
import unittest

from pymodbus.client import ModbusTcpClient

import modbus_tcp_server as server_module

SERVER = os.path.join(os.path.dirname(os.path.abspath(__file__)), "modbus_tcp_server.py")

DEFAULTS = {
    1: 2534, 2: 0, 3: 4856, 4: 0, 5: 650, 6: 0,
    223: 15, 224: 65511, 225: 150,
    401: 0,
    764: 1, 765: 1, 766: 0,
    1286: 258, 1287: 123,
}
ILLEGAL_DATA_ADDRESS = 2
SLAVE_DEVICE_FAILURE = 4
GENERIC_UNIT = 2


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class Server:
    """modbus_tcp_server.py as a child process (stopped by the test that started it)."""

    def __init__(self, port: int, log_file: str):
        self.port = port
        self.log_file = log_file
        self.process = subprocess.Popen(
            [sys.executable, "-u", SERVER, "--host", "127.0.0.1", "--port", str(port), "--log-file", log_file],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        deadline = time.time() + 15
        while time.time() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=0.2):
                    return
            except OSError:
                if self.process.poll() is not None:
                    raise RuntimeError(f"server exited with {self.process.returncode}")
                time.sleep(0.1)
        self.stop()
        raise RuntimeError("server did not start listening")

    def stop(self) -> None:
        # On Windows the venv python.exe is a launcher with the real interpreter as its child: stop the whole tree
        # this test started (only this PID and its children), then wait until the port is released.
        if os.name == "nt":
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(self.process.pid)],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        else:
            self.process.terminate()
        self.process.wait(timeout=10)
        deadline = time.time() + 10
        while time.time() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=0.2):
                    time.sleep(0.1)
            except OSError:
                return


class SimulatorTestCase(unittest.TestCase):
    server: Server
    client: ModbusTcpClient

    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory(ignore_cleanup_errors=True)
        cls.log_file = os.path.join(cls.tmp.name, "server.log")
        cls.server = Server(free_port(), cls.log_file)
        cls.client = ModbusTcpClient("127.0.0.1", port=cls.server.port, timeout=2)
        assert cls.client.connect()

    @classmethod
    def tearDownClass(cls):
        cls.client.close()
        cls.server.stop()
        cls.tmp.cleanup()

    # Register numbers are 1-based (document); the wire address is register - 1.
    def read(self, register: int, count: int = 1):
        return self.client.read_holding_registers(register - 1, count=count, device_id=1)

    def values(self, register: int, count: int = 1) -> list[int]:
        rr = self.read(register, count)
        self.assertFalse(rr.isError(), rr)
        return rr.registers

    def write(self, register: int, value: int):
        return self.client.write_register(register - 1, value, device_id=1)

    def write_many(self, register: int, values: list[int]):
        return self.client.write_registers(register - 1, values, device_id=1)

    def assert_exception(self, response, code: int = ILLEGAL_DATA_ADDRESS):
        self.assertTrue(response.isError(), response)
        self.assertEqual(code, response.exception_code)

    def assert_defaults(self):
        for register, value in DEFAULTS.items():
            self.assertEqual([value], self.values(register), f"Register {register}")

    def log_lines(self) -> list[str]:
        time.sleep(0.2)
        with open(self.log_file, encoding="utf-8") as f:
            return f.read().splitlines()


class AccessTests(SimulatorTestCase):
    def test_defaults(self):
        self.assert_defaults()

    def test_fc06_223_write_and_read_back(self):
        self.assertFalse(self.write(223, 20).isError())
        self.assertEqual([20], self.values(223))
        self.assertFalse(self.write(223, 15).isError())
        self.assertEqual([15], self.values(223))

    def test_fc06_401_write_and_restore(self):
        self.assertFalse(self.write(401, 1).isError())
        self.assertEqual([1], self.values(401))
        # Plain register: nothing else changes (no degF conversion of registers 1 / 223)
        self.assertEqual([2534], self.values(1))
        self.assertEqual([15], self.values(223))
        self.assertFalse(self.write(401, 0).isError())
        self.assertEqual([0], self.values(401))

    def test_fc06_read_only_registers_rejected(self):
        for register in (1, 2, 3, 4, 5, 6, 1286, 1287):
            with self.subTest(register=register):
                self.assert_exception(self.write(register, 1))
                self.assertEqual([DEFAULTS[register]], self.values(register))

    def test_fc06_gap_rejected(self):
        for register in (7, 222, 226, 300, 402, 763, 767, 1285, 1288):
            with self.subTest(register=register):
                self.assert_exception(self.write(register, 1))

    def test_read_gap_rejected(self):
        self.assert_exception(self.read(300))
        self.assert_exception(self.read(1, 7))

    def test_fc16_223_225_write_and_restore(self):
        self.assertFalse(self.write_many(223, [20, 30, 40]).isError())
        self.assertEqual([20, 30, 40], self.values(223, 3))
        self.assertFalse(self.write_many(223, [15, 65511, 150]).isError())
        self.assertEqual([15, 65511, 150], self.values(223, 3))

    def test_fc16_crossing_into_a_gap_writes_nothing(self):
        self.assert_exception(self.write_many(223, [20, 30, 40, 50]))     # 223-226, 226 does not exist
        self.assertEqual([15, 65511, 150], self.values(223, 3))           # no partial write
        self.assert_exception(self.write_many(222, [1, 2]))               # 222 does not exist, 223 does
        self.assertEqual([15], self.values(223))

    def test_fc16_read_only_rejected_whole_request(self):
        self.assert_exception(self.write_many(1, [1, 2]))
        self.assertEqual([2534, 0], self.values(1, 2))
        self.assert_exception(self.write_many(1286, [1, 2]))
        self.assertEqual([258, 123], self.values(1286, 2))

    def test_communication_registers_are_plain_writable_registers(self):
        self.assertFalse(self.write_many(764, [246, 6, 3]).isError())    # 246 has no special meaning here
        self.assertEqual([246, 6, 3], self.values(764, 3))
        # Unit ID 1 still answers (writing 764 does not change the simulator's Unit ID)
        self.assertEqual([2534], self.values(1))
        self.assertFalse(self.write_many(764, [1, 1, 0]).isError())
        self.assertEqual([1, 1, 0], self.values(764, 3))
        self.assertFalse(self.write(765, 2).isError())
        self.assertFalse(self.write(765, 1).isError())
        self.assertEqual([1, 1, 0], self.values(764, 3))


class WriteLogTests(SimulatorTestCase):
    def test_fc06_one_write_line_no_fake_read(self):
        before = len(self.log_lines())
        self.assertFalse(self.write(223, 20).isError())
        self.assertFalse(self.write(223, 15).isError())
        lines = self.log_lines()[before:]
        fc06 = [line for line in lines if "FC06" in line]
        self.assertEqual(2, len(fc06), lines)
        self.assertTrue(fc06[0].endswith("FC06 Write PDU 222 (Register 223) Value 20"), fc06[0])
        self.assertTrue(fc06[1].endswith("FC06 Write PDU 222 (Register 223) Value 15"), fc06[1])
        self.assertFalse(any("Read" in line for line in lines), lines)

    def test_fc16_one_write_line(self):
        before = len(self.log_lines())
        self.assertFalse(self.write_many(223, [20, 30, 40]).isError())
        self.assertFalse(self.write_many(223, [15, 65511, 150]).isError())
        lines = self.log_lines()[before:]
        self.assertEqual(2, len(lines), lines)
        self.assertTrue(lines[0].endswith("FC16 Write PDU 222..224 (Registers 223-225) Values 20, 30, 40"), lines[0])

    def test_read_line(self):
        before = len(self.log_lines())
        self.values(223, 3)
        lines = self.log_lines()[before:]
        self.assertEqual(1, len(lines), lines)
        self.assertTrue(lines[0].endswith("FC03 Read  PDU 222..224 (Registers 223-225) -> [15, 65511, 150]"), lines[0])

    def test_rejected_writes_are_logged_as_warnings(self):
        before = len(self.log_lines())
        self.write(1, 1)
        self.write(300, 1)
        self.write_many(223, [1, 2, 3, 4])
        lines = self.log_lines()[before:]
        self.assertEqual(3, len(lines), lines)
        self.assertIn("WARNING", lines[0])
        self.assertTrue(lines[0].endswith(
            "FC06 Write PDU 0 (Register 1) Value 1 -> Exception 02 Illegal Data Address (read-only register in this simulator), nothing written"),
            lines[0])
        self.assertIn("(no register at this address), nothing written", lines[1])
        self.assertIn("FC16 Write PDU 222..225 (Registers 223-226) Values 1, 2, 3, 4 -> Exception 02", lines[2])


class RestartTests(unittest.TestCase):
    def test_restart_restores_defaults(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
            log_file = os.path.join(tmp, "server.log")
            port = free_port()
            server = Server(port, log_file)
            client = ModbusTcpClient("127.0.0.1", port=port, timeout=2)
            try:
                self.assertTrue(client.connect())
                self.assertFalse(client.write_registers(222, [20, 30, 40], device_id=1).isError())
                self.assertFalse(client.write_register(400, 1, device_id=1).isError())
                self.assertFalse(client.write_registers(763, [5, 6, 3], device_id=1).isError())
            finally:
                client.close()
                server.stop()

            server = Server(port, log_file)
            client = ModbusTcpClient("127.0.0.1", port=port, timeout=2)
            try:
                self.assertTrue(client.connect())
                for register, value in DEFAULTS.items():
                    rr = client.read_holding_registers(register - 1, count=1, device_id=1)
                    self.assertEqual([value], rr.registers, f"Register {register}")
            finally:
                client.close()
                server.stop()


def decode32(words, datatype, order):
    """Independent of the server: the value of two wire words (letters = value bytes in wire order, A = MSB)."""
    wire = struct.pack(">HH", *words)
    value_bytes = bytes(wire[order.index(letter)] for letter in "ABCD")
    if datatype == "UInt32":
        return struct.unpack(">I", value_bytes)[0]
    if datatype == "Int32":
        return struct.unpack(">i", value_bytes)[0]
    f = struct.unpack(">f", value_bytes)[0]
    if math.isnan(f):
        return "NaN"
    if math.isinf(f):
        return "+Infinity" if f > 0 else "-Infinity"
    return f


class GenericUnitTests(SimulatorTestCase):
    """Unit 2: Generic 32-bit test device (simulator fixture, Phase 5C)."""

    def read2(self, register: int, count: int = 1, unit: int = GENERIC_UNIT):
        return self.client.read_holding_registers(register - 1, count=count, device_id=unit)

    def values2(self, register: int, count: int = 1) -> list[int]:
        rr = self.read2(register, count)
        self.assertFalse(rr.isError(), rr)
        return rr.registers

    def test_fixture_table_decodes_as_documented(self):
        # The fixture table itself is consistent (checked with struct, not with the tester's decoder)
        for f in server_module.GENERIC32_FIXTURES:
            with self.subTest(register=f.register):
                self.assertEqual(f.expected, decode32(f.words, f.datatype, f.order))

    def test_fixtures_are_served(self):
        for f in server_module.GENERIC32_FIXTURES:
            with self.subTest(register=f.register):
                self.assertEqual(list(f.words), self.values2(f.register, 2))
        self.assertEqual(34, len(self.values2(1, 34)))          # one contiguous read-only block 1-34

    def test_uint32_four_orders_same_value(self):
        for register, order in ((1, "ABCD"), (3, "BADC"), (5, "CDAB"), (7, "DCBA")):
            with self.subTest(order=order):
                self.assertEqual(305419896, decode32(self.values2(register, 2), "UInt32", order))

    def test_fc04_reads_the_same_values(self):
        rr = self.client.read_input_registers(0, count=2, device_id=GENERIC_UNIT)
        self.assertFalse(rr.isError(), rr)
        self.assertEqual([0x1234, 0x5678], rr.registers)

    def test_writable_area_defaults(self):
        self.assertEqual([0x0001, 0x0002], self.values2(101, 2))

    def test_fc06_writable_area(self):
        self.assertFalse(self.client.write_register(100, 0x1234, device_id=GENERIC_UNIT).isError())
        self.assertEqual([0x1234, 0x0002], self.values2(101, 2))
        self.assertFalse(self.client.write_register(100, 0x0001, device_id=GENERIC_UNIT).isError())
        self.assertEqual([0x0001, 0x0002], self.values2(101, 2))

    def test_fc16_writable_area(self):
        self.assertFalse(self.client.write_registers(100, [0x1234, 0x5678], device_id=GENERIC_UNIT).isError())
        self.assertEqual(305419896, decode32(self.values2(101, 2), "UInt32", "ABCD"))
        self.assertFalse(self.client.write_registers(100, [0x0001, 0x0002], device_id=GENERIC_UNIT).isError())
        self.assertEqual([0x0001, 0x0002], self.values2(101, 2))

    def test_fixtures_read_only(self):
        self.assert_exception(self.client.write_register(0, 0, device_id=GENERIC_UNIT))
        self.assert_exception(self.client.write_registers(16, [0, 0], device_id=GENERIC_UNIT))
        self.assertEqual([0x1234], self.values2(1))
        self.assertEqual([0x3F80, 0x0000], self.values2(17, 2))

    def test_gaps(self):
        self.assert_exception(self.read2(35))                   # right after the fixtures
        self.assert_exception(self.read2(100))                  # right before the writable area
        self.assert_exception(self.read2(101, 3))               # past the writable area
        self.assert_exception(self.client.write_registers(100, [1, 2, 3], device_id=GENERIC_UNIT))
        self.assertEqual([0x0001, 0x0002], self.values2(101, 2))   # nothing written

    def test_units_isolated(self):
        # The same addresses hold different devices; writing one unit never changes the other
        self.assertEqual([2534], self.values(1))
        self.assertEqual([0x1234], self.values2(1))
        self.assert_exception(self.read(101))                   # unit 1 has no register 101
        self.assert_exception(self.read2(223))                  # unit 2 has no register 223
        self.assertFalse(self.client.write_register(222, 20, device_id=1).isError())
        self.assertEqual([0x1234, 0x5678], self.values2(1, 2))
        self.assertFalse(self.client.write_register(222, 15, device_id=1).isError())
        self.assert_exception(self.client.read_holding_registers(0, count=1, device_id=3), SLAVE_DEVICE_FAILURE)

    def test_unit2_log_lines(self):
        before = len(self.log_lines())
        self.values2(1, 2)
        self.client.write_register(100, 0x0001, device_id=GENERIC_UNIT)
        lines = self.log_lines()[before:]
        self.assertTrue(lines[0].endswith("Unit 2 FC03 Read  PDU 0..1 (Registers 1-2) -> [4660, 22136]"), lines[0])
        self.assertTrue(lines[1].endswith("Unit 2 FC06 Write PDU 100 (Register 101) Value 1"), lines[1])


class GenericUnitRestartTests(unittest.TestCase):
    def test_restart_restores_unit2(self):
        with tempfile.TemporaryDirectory(ignore_cleanup_errors=True) as tmp:
            log_file = os.path.join(tmp, "server.log")
            port = free_port()
            server = Server(port, log_file)
            client = ModbusTcpClient("127.0.0.1", port=port, timeout=2)
            try:
                self.assertTrue(client.connect())
                self.assertFalse(client.write_registers(100, [0xAAAA, 0xBBBB], device_id=GENERIC_UNIT).isError())
            finally:
                client.close()
                server.stop()

            server = Server(port, log_file)
            client = ModbusTcpClient("127.0.0.1", port=port, timeout=2)
            try:
                self.assertTrue(client.connect())
                self.assertEqual([0x0001, 0x0002], client.read_holding_registers(100, count=2, device_id=GENERIC_UNIT).registers)
                self.assertEqual([0x1234, 0x5678], client.read_holding_registers(0, count=2, device_id=GENERIC_UNIT).registers)
                self.assertEqual([2534], client.read_holding_registers(0, count=1, device_id=1).registers)
            finally:
                client.close()
                server.stop()


if __name__ == "__main__":
    unittest.main()
