"""Records what a program tries to reach when there is no network.

    netwatch.py LOG

A DNS server on 127.0.0.1:53 that answers every A query with 127.0.0.1 (and any other type with no
records), and listeners on 127.0.0.1:443 and :80 that record the TLS server name or the HTTP Host
of whatever connects, then close. With /etc/resolv.conf pointing at 127.0.0.1 in a container that
has no network, every host a program looks up or connects to by name shows up in LOG, one JSON
object per line, and nothing leaves the container.
"""
import json
import socket
import struct
import sys
import threading
import time

LOG = sys.argv[1]
lock = threading.Lock()


def record(entry):
    with lock, open(LOG, "a") as log:
        log.write(json.dumps(entry) + "\n")


def question_name(data, offset=12):
    labels = []
    while data[offset]:
        length = data[offset]
        labels.append(data[offset + 1:offset + 1 + length].decode("latin1"))
        offset += length + 1
    return ".".join(labels), offset + 1


def answer(data):
    name, offset = question_name(data)
    qtype = struct.unpack(">H", data[offset:offset + 2])[0]
    record({"dns": name, "type": {1: "A", 28: "AAAA"}.get(qtype, qtype)})
    question = data[12:offset + 4]
    if qtype == 1:
        return (data[:2] + b"\x81\x80\x00\x01\x00\x01\x00\x00\x00\x00" + question
                + b"\xc0\x0c\x00\x01\x00\x01\x00\x00\x00\x3c\x00\x04\x7f\x00\x00\x01")
    return data[:2] + b"\x81\x80\x00\x01\x00\x00\x00\x00\x00\x00" + question


def serve_dns():
    server = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    server.bind(("127.0.0.1", 53))
    while True:
        data, address = server.recvfrom(4096)
        try:
            server.sendto(answer(data), address)
        except (IndexError, struct.error, OSError):
            record({"dns": "(unreadable query)"})


def server_name(hello):
    """The SNI of a TLS ClientHello, or None."""
    try:
        if hello[0] != 0x16:
            return None
        at = 5 + 4 + 2 + 32
        at += 1 + hello[at]
        at += 2 + struct.unpack(">H", hello[at:at + 2])[0]
        at += 1 + hello[at]
        end = at + 2 + struct.unpack(">H", hello[at:at + 2])[0]
        at += 2
        while at < end:
            kind, length = struct.unpack(">HH", hello[at:at + 4])
            at += 4
            if kind == 0:
                size = struct.unpack(">H", hello[at + 3:at + 5])[0]
                return hello[at + 5:at + 5 + size].decode()
            at += length
    except (IndexError, struct.error, UnicodeDecodeError):
        pass
    return None


def serve_tcp(port):
    server = socket.socket()
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("127.0.0.1", port))
    server.listen(50)
    while True:
        connection, _ = server.accept()
        try:
            connection.settimeout(3)
            data = connection.recv(4096)
            if port == 443:
                record({"tls": server_name(data) or "(no server name)"})
            else:
                host = next((line[5:].strip().decode("latin1") for line in data.split(b"\r\n")
                             if line.lower().startswith(b"host:")), "(no host)")
                record({"http": host})
        except OSError:
            record({"tcp": port})
        finally:
            connection.close()


for target, arguments in ((serve_dns, ()), (serve_tcp, (443,)), (serve_tcp, (80,))):
    threading.Thread(target=target, args=arguments, daemon=True).start()
while True:
    time.sleep(60)
