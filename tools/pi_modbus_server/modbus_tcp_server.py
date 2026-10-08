#!/usr/bin/env python3
r"""Modbus TCP test server for Modbus Tester (Phase 1.5).

Runs on Windows or the Raspberry Pi (Temperature, 192.168.137.189) and serves the A6V12258321
holding registers 1-6, 223-225, 401 and 764-766 for Unit ID 1 on port 5020.

Addressing: the tester UI uses 1-based register numbers, the wire (PDU) is 0-based.

    Register 1   -> PDU 0   = 2534   (25.34 degC)
    Register 2   -> PDU 1   = 0
    Register 3   -> PDU 2   = 4856   (48.56 %)
    Register 4   -> PDU 3   = 0
    Register 5   -> PDU 4   = 650    (650 ppm)
    Register 6   -> PDU 5   = 0
    Register 223 -> PDU 222 = 15     (+1.5 degC offset)
    Register 224 -> PDU 223 = 0xFFE7 (-25 -> -2.5 % offset, 16-bit two's complement)
    Register 225 -> PDU 224 = 150    (+150 ppm offset)
    Register 401 -> PDU 400 = 0      (System unit: Celsius)
    Register 764 -> PDU 763 = 1      (Modbus address 1)
    Register 765 -> PDU 764 = 1      (Baud rate: 9600bps)
    Register 766 -> PDU 765 = 0      (Transmission format: 1-8-E-1)

The register blocks are separate: addresses in between (and any read crossing a gap or past the end)
return Modbus exception 02 (Illegal Data Address). Other Unit IDs get exception 04 (Slave Device
Failure), which is pymodbus 3.15 behaviour. FC04 reads the same registers as FC03 (shared layout).

Requires pymodbus 3.15 (SimData/SimDevice API; SimData.address is the 0-based PDU address).

Foreground:  .venv\Scripts\python.exe modbus_tcp_server.py
Background:  Start_ModbusTCP.ps1 / Stop_ModbusTCP.ps1 / Status_ModbusTCP.ps1 (Windows), which pass
             --log-file / --error-log-file so the log keeps going after the console is closed.
"""

import argparse
import asyncio
import logging
import os
import socket
import sys

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


# Offsets (registers 223-225, RW in the document, read-only here). Signed Int16 on the wire.
OFFSET_REGISTERS = [
    int16(15),     # Reg 223 Temperature offset       = 15 x 0.1  = +1.5 degC (-100...100)
    int16(-25),    # Reg 224 Relative humidity offset = -25 x 0.1 = -2.5 %    (-100...100)
    int16(150),    # Reg 225 CO2 offset               = 150 x 1.0 = 150 ppm   (-2000...2000)
]

# Configuration (registers 401 and 764-766, RW in the document, read-only here). Enum-like, no scaling.
SYSTEM_UNIT_REGISTER = [
    0,      # Reg 401 System unit         = 0 Celsius (1 = Fahrenheit)
]
COMMUNICATION_REGISTERS = [
    1,      # Reg 764 Modbus address      = 1         (1...247)
    1,      # Reg 765 Baud rate           = 1 9600bps (0 Auto, 2 19200, 3 38400, 4 57600, 5 76800, 6 115200)
    0,      # Reg 766 Transmission format = 0 1-8-E-1 (1 1-8-O-1, 2 1-8-N-1, 3 1-8-N-2)
]

# PDU start address -> values. Each entry is one SimData block; pymodbus treats the gaps as invalid.
REGISTER_BLOCKS = {
    0: HOLDING_REGISTERS,          # registers 1-6
    222: OFFSET_REGISTERS,         # registers 223-225
    400: SYSTEM_UNIT_REGISTER,     # register 401
    763: COMMUNICATION_REGISTERS,  # registers 764-766
}
VALID_PDU_ADDRESSES = {start + i for start, values in REGISTER_BLOCKS.items() for i in range(len(values))}

FUNCTION_NAMES = {1: "FC01", 2: "FC02", 3: "FC03", 4: "FC04", 5: "FC05", 6: "FC06", 15: "FC15", 16: "FC16"}

log = logging.getLogger("modbus_tcp_server")


async def log_access(function_code, start_address, address, count, current_registers, set_values):
    """Called by pymodbus on every register access; prints the request in UI (1-based) and PDU terms."""
    name = FUNCTION_NAMES.get(function_code, f"FC{function_code:02d}")
    span = f"PDU {address}..{address + count - 1} (Register {address + 1}..{address + count})"
    if any(a not in VALID_PDU_ADDRESSES for a in range(address, address + count)):
        # Gap or past the end: pymodbus answers this with exception 02 after the action returns.
        log.warning("%s %s -> Exception 02 Illegal Data Address", name, span)
    elif set_values is None:
        offset = address - start_address
        log.info("%s read  %s -> %s", name, span, current_registers[offset:offset + count])
    else:
        log.info("%s write %s <- %s", name, span, set_values)
    return None


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


def build_device(unit_id: int) -> SimDevice:
    blocks = [SimData(address=start, values=list(values), datatype=DataType.REGISTERS)
              for start, values in REGISTER_BLOCKS.items()]
    # Shared layout: the register blocks serve all tables, so FC04 returns the same values as FC03.
    # (pymodbus 3.15 does not accept empty blocks in the 4-table non-shared layout.)
    return SimDevice(id=unit_id, simdata=blocks, action=log_access)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Modbus TCP test server for Modbus Tester")
    parser.add_argument("--host", default="0.0.0.0", help="listen address (default 0.0.0.0)")
    parser.add_argument("--port", type=int, default=5020, help="listen port (default 5020)")
    parser.add_argument("--unit", type=int, default=1, help="Unit ID (default 1)")
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
    log.info("Server Start: PID %d, pymodbus %s, listening on %s:%d, Unit ID %d",
             os.getpid(), pymodbus_version, args.host, args.port, args.unit)
    for start, values in REGISTER_BLOCKS.items():
        for i, value in enumerate(values):
            pdu = start + i
            signed = value - 0x10000 if value >= 0x8000 else value
            note = f", signed {signed}" if signed != value else ""
            log.info("  Register %d (PDU %d) = %d (0x%04X%s)", pdu + 1, pdu, value, value, note)

    # Runs until the process is stopped (Ctrl+C in the foreground, Stop_ModbusTCP.ps1 in the background).
    await StartAsyncTcpServer(context=build_device(args.unit), address=(args.host, args.port))


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
