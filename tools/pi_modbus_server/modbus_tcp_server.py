#!/usr/bin/env python3
"""Modbus TCP test server for Modbus Tester (Phase 1.5).

Runs on the Raspberry Pi (Temperature, 192.168.137.189) and serves 6 holding
registers for Unit ID 1 on port 5020.

Addressing: the tester UI uses 1-based register numbers, the wire (PDU) is 0-based.

    Register 1 -> PDU 0 = 2534   (25.34 with scale 0.01)
    Register 2 -> PDU 1 = 0
    Register 3 -> PDU 2 = 4856   (48.56 with scale 0.01)
    Register 4 -> PDU 3 = 0
    Register 5 -> PDU 4 = 650
    Register 6 -> PDU 5 = 0

Reading outside PDU 0..5 returns Modbus exception 02 (Illegal Data Address);
other Unit IDs get exception 04 (Slave Device Failure), which is pymodbus 3.15 behaviour.
FC04 reads the same block as FC03 (shared register layout).

Requires pymodbus 3.15 (SimData/SimDevice API; SimData.address is the 0-based PDU address).
"""

import argparse
import asyncio
import logging

from pymodbus import __version__ as pymodbus_version
from pymodbus.server import StartAsyncTcpServer
from pymodbus.simulator import DataType, SimData, SimDevice

HOLDING_REGISTERS = [2534, 0, 4856, 0, 650, 0]
FUNCTION_NAMES = {1: "FC01", 2: "FC02", 3: "FC03", 4: "FC04", 5: "FC05", 6: "FC06", 15: "FC15", 16: "FC16"}

log = logging.getLogger("modbus_tcp_server")


async def log_access(function_code, start_address, address, count, current_registers, set_values):
    """Called by pymodbus on every register access; prints the request in UI (1-based) and PDU terms."""
    name = FUNCTION_NAMES.get(function_code, f"FC{function_code:02d}")
    span = f"PDU {address}..{address + count - 1} (Register {address + 1}..{address + count})"
    if address + count > len(HOLDING_REGISTERS):
        # pymodbus answers this with exception 02 after the action returns.
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
    holding = SimData(address=0, values=list(HOLDING_REGISTERS), datatype=DataType.REGISTERS)
    # Shared layout: one register block serves all tables, so FC04 returns the same values as FC03.
    # (pymodbus 3.15 does not accept empty blocks in the 4-table non-shared layout.)
    return SimDevice(id=unit_id, simdata=[holding], action=log_access)


async def main() -> None:
    parser = argparse.ArgumentParser(description="Modbus TCP test server for Modbus Tester")
    parser.add_argument("--host", default="0.0.0.0", help="listen address (default 0.0.0.0)")
    parser.add_argument("--port", type=int, default=5020, help="listen port (default 5020)")
    parser.add_argument("--unit", type=int, default=1, help="Unit ID (default 1)")
    parser.add_argument("--debug", action="store_true", help="show pymodbus frame-level debug log")
    args = parser.parse_args()

    logging.basicConfig(format="%(asctime)s %(levelname)-7s %(message)s", level=logging.INFO)
    logging.getLogger("pymodbus").setLevel(logging.DEBUG if args.debug else logging.WARNING)
    for handler in logging.getLogger().handlers:
        handler.addFilter(UnknownUnitFilter())

    log.info("pymodbus %s, listening on %s:%d, Unit ID %d", pymodbus_version, args.host, args.port, args.unit)
    for i, value in enumerate(HOLDING_REGISTERS):
        log.info("  Register %d (PDU %d) = %d (0x%04X)", i + 1, i, value, value)

    await StartAsyncTcpServer(context=build_device(args.unit), address=(args.host, args.port))


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
