#!/usr/bin/env python3
"""Requests a path over h2c (HTTP/2 cleartext, prior knowledge) the way a gateway does.

A gateway that terminated TLS forwards the client's `:scheme` - `https` - on its plain HTTP/2
connection to the pod (Envoy, App Routing's Istio, does). Kestrel refuses a `:scheme` that differs
from the connection's unless AllowAlternateSchemes is on: it resets the stream with PROTOCOL_ERROR,
which the gateway answers with 502. Usage: h2c_gateway.py <host> <port> <path> <scheme>
"""
import socket
import sys

import h2.config
import h2.connection
import h2.events

host, port, path, scheme = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]
sock = socket.create_connection((host, port), timeout=10)
conn = h2.connection.H2Connection(config=h2.config.H2Configuration(client_side=True))
conn.initiate_connection()
conn.send_headers(1, [(":method", "GET"), (":scheme", scheme), (":authority", "hello.example"),
                      (":path", path), ("user-agent", "h2c-gateway-test")], end_stream=True)
sock.sendall(conn.data_to_send())
status, done = None, False
while not done:
    data = sock.recv(65535)
    if not data:
        break
    for event in conn.receive_data(data):
        if isinstance(event, h2.events.ResponseReceived):
            status = dict(event.headers).get(b":status", b"").decode()
        elif isinstance(event, h2.events.StreamReset):
            print(f"{scheme} {path}: stream reset, error code {event.error_code!r}")
            sys.exit(1)
        elif isinstance(event, h2.events.ConnectionTerminated):
            print(f"{scheme} {path}: connection terminated, error code {event.error_code!r}")
            sys.exit(1)
        elif isinstance(event, h2.events.StreamEnded):
            done = True
    sock.sendall(conn.data_to_send())
print(f"{scheme} {path}: {status}")
sys.exit(0 if status == "200" else 1)
