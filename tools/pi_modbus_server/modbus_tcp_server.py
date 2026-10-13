#!/usr/bin/env python3
r"""Modbus TCP test server for Modbus Tester (Phase 1.5, write behaviour Phase 4D, Unit 2 Phase 5C).

Runs on Windows or the Raspberry Pi (Temperature, 192.168.137.189) on port 5020 with two devices, chosen by
the request's Unit ID:

    Unit ID 1  A6V12258321 simulator: holding registers 1-6, 223-225, 401, 764-766 and 1286-1287 (below).
    Unit ID 2  Generic 32-bit test device: a SIMULATOR TEST FIXTURE of UInt32 / Int32 / Float32 values in the four
               byte orders plus a writable raw area (see GENERIC32_FIXTURES). Not a real device, not a profile.

Unit 1 (A6V12258321 simulator):

Addressing: the tester UI uses 1-based register numbers, the wire (PDU) is 0-based.

    Register 1   -> PDU 0   = 2534   (25.34 degC)                      R
    Register 2   -> PDU 1   = 0                                         R
    Register 3   -> PDU 2   = 4856   (48.56 %)                          R
    Register 4   -> PDU 3   = 0                                         R
    Register 5   -> PDU 4   = 650    (650 ppm)                          R
    Register 6   -> PDU 5   = 0                                         R
    Register 223 -> PDU 222 = 15     (+1.5 degC offset)                 RW
    Register 224 -> PDU 223 = 0xFFE7 (-25 -> -2.5 % offset, two's complement)  RW
    Register 225 -> PDU 224 = 150    (+150 ppm offset)                  RW
    Register 401 -> PDU 400 = 0      (System unit: Celsius)             RW
    Register 764 -> PDU 763 = 1      (Modbus address 1)                 RW
    Register 765 -> PDU 764 = 1      (Baud rate: 9600bps)               RW
    Register 766 -> PDU 765 = 0      (Transmission format: 1-8-E-1)     RW
    Register 1286 -> PDU 1285 = 258  (SW version: Major & Minor, raw 0x0102)  R
    Register 1287 -> PDU 1286 = 123  (SW version: Build, raw 0x007B)          R
    (A6V12045847 version format: Major = high byte, Minor = low byte, Build = 16 bit -> these read as 1.02.123)

Register 765 = 1 (9600bps) is simulator test data; the document default is 0 = Auto.

Access follows the document's R / RW column. Writes (FC06 / FC16) change memory only: a restart brings back
the defaults above (no persistence). This is a protocol test tool, not a device model:
  * A write to a read-only register is answered with exception 02 (Illegal Data Address). That is what the
    pymodbus readonly flag does; it is NOT a statement about how the real Siemens sensor answers.
  * 401 / 764 / 765 / 766 are plain writable registers: writing them does not convert other registers, change
    the Unit ID, the port, a baud rate or a serial format, and 246 has no special meaning here.
The register blocks are separate: addresses in between (and any access crossing a gap or past the end)
return exception 02. pymodbus checks every register of a request before it writes any, so a rejected
FC16 writes nothing (no partial write). Other Unit IDs get exception 04 (Slave Device Failure), which is
pymodbus 3.15 behaviour. FC04 reads the same registers as FC03 (pymodbus shared layout); that is a
simulator artefact only - A6V12045847 says the sensor "Supports only the holding register" (FC03).

Requires pymodbus 3.15 (SimData/SimDevice API; SimData.address is the 0-based PDU address).

Foreground:  .venv\Scripts\python.exe modbus_tcp_server.py
Background:  Start_ModbusTCP.ps1 / Stop_ModbusTCP.ps1 / Status_ModbusTCP.ps1 (Windows), which pass
             --log-file / --error-log-file so the log keeps going after the console is closed.
Tests:       .venv\Scripts\python.exe -m unittest test_modbus_tcp_server -v
"""

import argparse
import asyncio
import logging
import os
import socket
import sys
from typing import NamedTuple

from pymodbus import __version__ as pymodbus_version
from pymodbus.server import StartAsyncTcpServer
from pymodbus.simulator import DataType, SimData, SimDevice

# Default data = normal values per Siemens A6V12258321 section 4.3 (registers 1-6, all within the document range).
# Keep out-of-range values (e.g. 0xDEAD = 57005) out of the defaults; the automated tests cover those.
HOLDING_REGISTERS = [
    2534,   # Reg 1 Temperature           = 25.34 degC  (Int16, scale 0.01, -327...327)
    0,      # Reg 2 Temperature reliability = 0 No error  (1 = bad reliability, unavailable)
    4856,   # Reg 3 Relative humidity     = 48.56 %     (scale 0.01, 0...100)
    0,      # Reg 4 Humidity reliability  = 0 No error
    650,    # Reg 5 CO2                   = 650 ppm     (no scaling, 0...2000)
    0,      # Reg 6 CO2 reliability       = 0 No error
]


def int16(value: int) -> int:
    """Signed value -> 16-bit register pattern (two's complement), e.g. -25 -> 0xFFE7 = 65511."""
    return value & 0xFFFF


# Offsets (registers 223-225, RW). Signed Int16 on the wire.
OFFSET_REGISTERS = [
    int16(15),     # Reg 223 Temperature offset       = 15 x 0.1  = +1.5 degC (-100...100)
    int16(-25),    # Reg 224 Relative humidity offset = -25 x 0.1 = -2.5 %    (-100...100)
    int16(150),    # Reg 225 CO2 offset               = 150 x 1.0 = 150 ppm   (-2000...2000)
]

# Configuration (registers 401 and 764-766, RW). Enum-like, no scaling, no side effects in this simulator.
SYSTEM_UNIT_REGISTER = [
    0,      # Reg 401 System unit         = 0 Celsius (1 = Fahrenheit)
]
COMMUNICATION_REGISTERS = [
    1,      # Reg 764 Modbus address      = 1         (1...247)
    1,      # Reg 765 Baud rate           = 1 9600bps (test data; document default 0 Auto. 2 19200 ... 6 115200)
    0,      # Reg 766 Transmission format = 0 1-8-E-1 (1 1-8-O-1, 2 1-8-N-1, 3 1-8-N-2)
]

# Version (registers 1286-1287, R). Easy-to-spot test values; the tester's A6V profile decodes them as 1.02.123.
VERSION_REGISTERS = [
    258,    # Reg 1286 SW version: Major & Minor versions = 258 (0x0102) -> Major 1, Minor 02
    123,    # Reg 1287 SW version: Build version          = 123 (0x007B) -> Build 123; version 1.02.123
]


class RegisterBlock(NamedTuple):
    """One SimData block: consecutive registers with the same access."""
    start: int            # 0-based PDU address of the first register
    values: list[int]     # default values (restored on every start)
    readonly: bool        # True = R, False = RW


# ---------------------------------------------------------------------------------------------------------------
# Unit 1 (default --unit 1): A6V12258321 simulator. Each entry is one SimData block; pymodbus treats the gaps as
# invalid (exception 02). Unchanged since Phase 4D.
# ---------------------------------------------------------------------------------------------------------------
REGISTER_BLOCKS = [
    RegisterBlock(0, HOLDING_REGISTERS, readonly=True),          # registers 1-6        R
    RegisterBlock(222, OFFSET_REGISTERS, readonly=False),        # registers 223-225    RW
    RegisterBlock(400, SYSTEM_UNIT_REGISTER, readonly=False),    # register 401         RW
    RegisterBlock(763, COMMUNICATION_REGISTERS, readonly=False), # registers 764-766    RW (plain registers)
    RegisterBlock(1285, VERSION_REGISTERS, readonly=True),       # registers 1286-1287  R
]


# ---------------------------------------------------------------------------------------------------------------
# Unit 2 (--generic-unit 2): Generic 16/32-bit TEST DEVICE (Phase 5C). A simulator test fixture for the tester's
# Generic 32-bit read - NOT a real device and NOT a device profile. Register = human 1-based number,
# PDU = pymodbus 0-based address (Register - 1). Easy to check by hand:
#
#   Register  PDU      Word 1  Word 2  Type     Order  Decoded value
#   1-2       0-1      1234    5678    UInt32   ABCD   0x12345678 = 305419896
#   3-4       2-3      3412    7856    UInt32   BADC   0x12345678 = 305419896
#   5-6       4-5      5678    1234    UInt32   CDAB   0x12345678 = 305419896
#   7-8       6-7      7856    3412    UInt32   DCBA   0x12345678 = 305419896
#   9-10      8-9      FFFF    FFFF    Int32    ABCD   -1
#   11-12     10-11    FF85    FFFF    Int32    CDAB   -123        (0xFFFFFF85)
#   13-14     12-13    8000    0000    Int32    ABCD   -2147483648 (Int32 min)
#   15-16     14-15    FFFF    FF7F    Int32    DCBA   2147483647  (Int32 max, 0x7FFFFFFF)
#   17-18     16-17    3F80    0000    Float32  ABCD   1.0
#   19-20     18-19    803F    0000    Float32  BADC   1.0
#   21-22     20-21    0000    3F80    Float32  CDAB   1.0
#   23-24     22-23    0000    803F    Float32  DCBA   1.0
#   25-26     24-25    BF80    0000    Float32  ABCD   -1.0
#   27-28     26-27    3FC0    0000    Float32  ABCD   1.5
#   29-30     28-29    7FC0    0000    Float32  ABCD   NaN
#   31-32     30-31    7F80    0000    Float32  ABCD   +Infinity
#   33-34     32-33    FF80    0000    Float32  ABCD   -Infinity
#   101-102   100-101  0001    0002    writable raw area (any type; UInt32 ABCD = 0x00010002 = 65538)
#
# Registers 1-34 are read-only (a write gets exception 02, the simulator's read-only rule - not a statement about
# any real device); 101-102 are RW for raw FC06 / FC16 tests; everything else is a gap (exception 02).
# A restart restores all values.
# ---------------------------------------------------------------------------------------------------------------
class Fixture(NamedTuple):
    """One 32-bit test value of Unit 2: where it is, its two wire words, and how it must decode."""
    register: int                 # human register number of the first word (1-based)
    words: tuple[int, int]        # wire order: first register, second register
    datatype: str                 # "UInt32" | "Int32" | "Float32"
    order: str                    # "ABCD" | "BADC" | "CDAB" | "DCBA"
    expected: object              # int / float, or "NaN" / "+Infinity" / "-Infinity"


GENERIC32_FIXTURES = [
    Fixture(1, (0x1234, 0x5678), "UInt32", "ABCD", 305419896),
    Fixture(3, (0x3412, 0x7856), "UInt32", "BADC", 305419896),
    Fixture(5, (0x5678, 0x1234), "UInt32", "CDAB", 305419896),
    Fixture(7, (0x7856, 0x3412), "UInt32", "DCBA", 305419896),
    Fixture(9, (0xFFFF, 0xFFFF), "Int32", "ABCD", -1),
    Fixture(11, (0xFF85, 0xFFFF), "Int32", "CDAB", -123),
    Fixture(13, (0x8000, 0x0000), "Int32", "ABCD", -2147483648),
    Fixture(15, (0xFFFF, 0xFF7F), "Int32", "DCBA", 2147483647),
    Fixture(17, (0x3F80, 0x0000), "Float32", "ABCD", 1.0),
    Fixture(19, (0x803F, 0x0000), "Float32", "BADC", 1.0),
    Fixture(21, (0x0000, 0x3F80), "Float32", "CDAB", 1.0),
    Fixture(23, (0x0000, 0x803F), "Float32", "DCBA", 1.0),
    Fixture(25, (0xBF80, 0x0000), "Float32", "ABCD", -1.0),
    Fixture(27, (0x3FC0, 0x0000), "Float32", "ABCD", 1.5),
    Fixture(29, (0x7FC0, 0x0000), "Float32", "ABCD", "NaN"),
    Fixture(31, (0x7F80, 0x0000), "Float32", "ABCD", "+Infinity"),
    Fixture(33, (0xFF80, 0x0000), "Float32", "ABCD", "-Infinity"),
]
GENERIC32_WRITABLE_REGISTER = 101          # human register number of the writable raw area (PDU 100)
GENERIC32_WRITABLE = [0x0001, 0x0002]      # registers 101-102 defaults

GENERIC32_BLOCKS = [
    RegisterBlock(0, [w for f in GENERIC32_FIXTURES for w in f.words], readonly=True),                    # 1-34    R
    RegisterBlock(GENERIC32_WRITABLE_REGISTER - 1, GENERIC32_WRITABLE, readonly=False),                    # 101-102 RW
]

FUNCTION_NAMES = {1: "FC01", 2: "FC02", 3: "FC03", 4: "FC04", 5: "FC05", 6: "FC06", 15: "FC15", 16: "FC16"}
WRITE_FUNCTIONS = {6, 16}

log = logging.getLogger("modbus_tcp_server")


def describe_span(address: int, count: int) -> str:
    """'PDU 222 (Register 223)' or 'PDU 222..224 (Registers 223-225)'."""
    if count == 1:
        return f"PDU {address} (Register {address + 1})"
    return f"PDU {address}..{address + count - 1} (Registers {address + 1}-{address + count})"


class DeviceSpec:
    """One simulated Modbus device (Unit ID): its blocks and the access log callback that knows which unit it is."""

    def __init__(self, name: str, blocks: list[RegisterBlock]):
        self.name = name
        self.blocks = blocks
        self.valid = {b.start + i for b in blocks for i in range(len(b.values))}
        self.readonly = {b.start + i for b in blocks if b.readonly for i in range(len(b.values))}

    def write_outcome(self, address: int, count: int) -> str | None:
        """Why pymodbus will reject this write (it checks every register before writing any), or None."""
        span = range(address, address + count)
        if any(a not in self.valid for a in span):
            return "Exception 02 Illegal Data Address (no register at this address)"
        if any(a in self.readonly for a in span):
            return "Exception 02 Illegal Data Address (read-only register in this simulator)"
        return None

    def action(self, unit_id: int):
        """The pymodbus action for this device (pymodbus does not pass the unit to the action, so it is bound here)."""
        prefix = f"Unit {unit_id} "

        async def log_access(function_code, start_address, address, count, current_registers, set_values):
            """Called by pymodbus before every register access; logs it as one Read or one Write line.

            pymodbus 3.15 answers FC06 by writing (set_values = [value]) and then reading the register back to build
            the echo response (set_values = None, function code still 6). That internal read is not a client request
            and is not logged, so one FC06 gives exactly one Write line. Returning None leaves the decision to pymodbus.
            """
            name = FUNCTION_NAMES.get(function_code, f"FC{function_code:02d}")
            if function_code in WRITE_FUNCTIONS:
                if set_values is None:
                    return None   # FC06 echo read-back inside pymodbus
                values = ", ".join(str(v) for v in set_values)
                label = "Value" if count == 1 else "Values"
                line = f"{prefix}{name} Write {describe_span(address, count)} {label} {values}"
                if (rejected := self.write_outcome(address, count)) is not None:
                    log.warning("%s -> %s, nothing written", line, rejected)
                else:
                    log.info("%s", line)
                return None

            span = describe_span(address, count)
            if any(a not in self.valid for a in range(address, address + count)):
                # Gap or past the end: pymodbus answers this with exception 02 after the action returns.
                log.warning("%s%s Read  %s -> Exception 02 Illegal Data Address", prefix, name, span)
            else:
                offset = address - start_address
                log.info("%s%s Read  %s -> %s", prefix, name, span, current_registers[offset:offset + count])
            return None

        return log_access

    def build(self, unit_id: int) -> SimDevice:
        # Fresh lists from the defaults on every start: writes live in memory only.
        data = [SimData(address=b.start, values=list(b.values), datatype=DataType.REGISTERS, readonly=b.readonly)
                for b in self.blocks]
        # Shared layout: the blocks serve all tables, so FC04 returns the same values as FC03.
        # (pymodbus 3.15 does not accept empty blocks in the 4-table non-shared layout.)
        return SimDevice(id=unit_id, simdata=data, action=self.action(unit_id))


A6V_DEVICE = DeviceSpec("A6V12258321 simulator", REGISTER_BLOCKS)
GENERIC32_DEVICE = DeviceSpec("Generic 32-bit test device (fixture)", GENERIC32_BLOCKS)

# Unit 1 sets, kept under their previous names
VALID_PDU_ADDRESSES = A6V_DEVICE.valid
READONLY_PDU_ADDRESSES = A6V_DEVICE.readonly


class UnknownUnitFilter(logging.Filter):
    """pymodbus 3.15 logs a KeyError traceback for an unknown Unit ID (and replies exception 04); shorten it."""

    def filter(self, record: logging.LogRecord) -> bool:
        message = record.getMessage()
        if "Datastore unable to fulfill request" in message and "KeyError" in message:
            record.msg = "Request for unknown Unit ID -> Exception 04 Slave Device Failure"
            record.args = ()
            record.exc_info = None
            record.levelno, record.levelname = logging.WARNING, "WARNING"
        return True


def build_devices(unit_id: int, generic_unit_id: int) -> list[SimDevice]:
    """Both devices on one server: requests are routed by Unit ID (unknown IDs: exception 04)."""
    if unit_id == generic_unit_id:
        raise SystemExit(f"--unit and --generic-unit must differ (both {unit_id})")
    return [A6V_DEVICE.build(unit_id), GENERIC32_DEVICE.build(generic_unit_id)]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Modbus TCP test server for Modbus Tester")
    parser.add_argument("--host", default="0.0.0.0", help="listen address (default 0.0.0.0)")
    parser.add_argument("--port", type=int, default=5020, help="listen port (default 5020)")
    parser.add_argument("--unit", type=int, default=1, help="Unit ID of the A6V12258321 simulator (default 1)")
    parser.add_argument("--generic-unit", type=int, default=2, help="Unit ID of the Generic 32-bit test device (default 2)")
    parser.add_argument("--debug", action="store_true", help="show pymodbus frame-level debug log")
    parser.add_argument("--log-file", help="also append the log to this file")
    parser.add_argument("--error-log-file", help="also append warnings and errors to this file")
    return parser.parse_args()


def setup_logging(args: argparse.Namespace) -> None:
    handlers: list[logging.Handler] = [logging.StreamHandler()]
    if args.log_file:
        handlers.append(logging.FileHandler(args.log_file, mode="a", encoding="utf-8"))
    if args.error_log_file:
        error_handler = logging.FileHandler(args.error_log_file, mode="a", encoding="utf-8")
        error_handler.setLevel(logging.WARNING)
        handlers.append(error_handler)
    for handler in handlers:
        handler.addFilter(UnknownUnitFilter())
    logging.basicConfig(format="%(asctime)s %(levelname)-7s %(message)s", level=logging.INFO, handlers=handlers)
    logging.getLogger("pymodbus").setLevel(logging.DEBUG if args.debug else logging.WARNING)


def ensure_port_free(host: str, port: int) -> None:
    """Fail fast if the port is taken.

    pymodbus listens with SO_REUSEADDR; on Windows that lets a second server bind a port that is already
    listening and silently take over new connections. A plain exclusive bind first makes that an error.
    """
    probe = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):  # Windows
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        probe.bind((host, port))
    except OSError as ex:
        raise SystemExit(f"Port {port} on {host} is already in use ({ex}); is another server running?") from ex
    finally:
        probe.close()


async def main(args: argparse.Namespace) -> None:
    ensure_port_free(args.host, args.port)
    devices = build_devices(args.unit, args.generic_unit)
    log.info("Server Start: PID %d, pymodbus %s, listening on %s:%d, Unit ID %d (A6V12258321) + Unit ID %d (Generic 32-bit test)",
             os.getpid(), pymodbus_version, args.host, args.port, args.unit, args.generic_unit)
    for block in REGISTER_BLOCKS:
        access = "R " if block.readonly else "RW"
        for i, value in enumerate(block.values):
            pdu = block.start + i
            signed = value - 0x10000 if value >= 0x8000 else value
            note = f", signed {signed}" if signed != value else ""
            log.info("  %s Register %d (PDU %d) = %d (0x%04X%s)", access, pdu + 1, pdu, value, value, note)

    log.info("  Unit %d: %d read-only 32-bit fixtures at registers 1-34, writable raw registers %d-%d = %s",
             args.generic_unit, len(GENERIC32_FIXTURES), GENERIC32_WRITABLE_REGISTER,
             GENERIC32_WRITABLE_REGISTER + len(GENERIC32_WRITABLE) - 1, ", ".join(f"0x{v:04X}" for v in GENERIC32_WRITABLE))

    # Runs until the process is stopped (Ctrl+C in the foreground, Stop_ModbusTCP.ps1 in the background).
    await StartAsyncTcpServer(context=devices, address=(args.host, args.port))


if __name__ == "__main__":
    arguments = parse_args()
    setup_logging(arguments)
    try:
        asyncio.run(main(arguments))
        log.warning("Server Stop: server loop ended unexpectedly")
    except KeyboardInterrupt:
        log.info("Server Stop: Ctrl+C")
    except SystemExit as stop:
        log.error("Server Stop: %s", stop)
        sys.exit(1)
    except Exception:
        log.exception("Server Stop: crashed")
        sys.exit(1)
