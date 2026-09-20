#!/usr/bin/env python3
"""Keep a loopback listener for WSL's Windows forwarding across the UI handoff."""

import os
import socket

# ServiceLB routes the node's address through iptables, which Windows cannot
# discover as a listening process. systemd owns a stable localhost socket and
# proxies raw TCP (including WebSockets) to that node address on port 80.
# Determine the address each time the service starts, including after a reboot.
with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as route:
    route.connect(("192.0.2.1", 9))  # Select the default route; no packet is sent.
    address = route.getsockname()[0]
os.execv(
    "/usr/lib/systemd/systemd-socket-proxyd", ["systemd-socket-proxyd", f"{address}:80"]
)
