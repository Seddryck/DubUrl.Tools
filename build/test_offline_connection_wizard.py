"""Drive the installed Linux CLI through a terminal with Docker networking disabled."""

import errno
import fcntl
import os
from pathlib import Path
import pty
import re
import select
import shutil
import struct
import subprocess
import sys
import termios
import time


ANSI = re.compile(rb"\x1b\[[0-?]*[ -/]*[@-~]")


def run_wizard(tool_path, steps, expected_exit):
    docker = shutil.which("docker")
    if docker is None:
        raise RuntimeError("Docker is required for the offline wizard smoke test.")
    command = [
        docker, "run", "--pull", "never", "--rm", "--network", "none", "-it",
        "-e", "TERM=xterm", "-e", "NO_COLOR=1", "-e", "DUBURL_ProviderPacks__StorePath=/dev/null/store",
        "-v", f"{tool_path}:/tool:ro", "mcr.microsoft.com/dotnet/runtime:10.0",
        "/tool/duburl", "connection", "new",
    ]
    pid, fd = pty.fork()
    if pid == 0:
        fcntl.ioctl(sys.stdin.fileno(), termios.TIOCSWINSZ, struct.pack("HHHH", 32, 120, 0, 0))
        # Docker is a fixed executable. The tool path is passed as one argument to the PTY child.
        os.execv(docker, command)  # nosec B606

    transcript = b""
    deadline = time.monotonic() + 45

    def read():
        remaining = max(0, deadline - time.monotonic())
        if not select.select([fd], [], [], remaining)[0]:
            raise TimeoutError("Timed out waiting for the installed wizard.")
        try:
            return os.read(fd, 16384)
        except OSError as error:
            if error.errno == errno.EIO:  # The terminal reports EIO after the slave closes.
                return b""
            raise

    try:
        pending = b""
        for prompt, keys in steps:
            while prompt not in ANSI.sub(b"", pending):
                chunk = read()
                if not chunk:
                    raise AssertionError(f"The wizard exited before {prompt!r}.")
                pending += chunk
                transcript += chunk
            os.write(fd, keys)
            pending = b""
        while chunk := read():
            transcript += chunk
        _, status = os.waitpid(pid, 0)
        pid = None
        assert os.waitstatus_to_exitcode(status) == expected_exit, f"Unexpected process status: {status}"
    except Exception:
        print(ANSI.sub(b"", transcript).decode(errors="replace"), file=sys.stderr)
        raise
    finally:
        os.close(fd)
        if pid is not None:
            os.kill(pid, 9)
            os.waitpid(pid, 0)
    return ANSI.sub(b"", transcript)


if __name__ == "__main__":
    tool_path = Path(sys.argv[1]).resolve()
    subprocess.run(["docker", "pull", "mcr.microsoft.com/dotnet/runtime:10.0"], check=True)
    output = run_wizard(tool_path, [
        (b"Database engine", b"mysql\r"),
        (b"Driver", b"\r"),
        (b"Host:", b"db.example.com\r"),
        (b"Port [3306]:", b"70000\r"),
        (b"Enter a port from 1 to 65535.", b"\r"),
        (b"Database:", b"sales\r"),
        (b"Authentication", b"\r"),
        (b"Username:", b"alice\r"),
        (b"Require verified TLS", b"\x1b[B\r"),
    ], expected_exit=0)
    assert b"mysql+tls://alice@db.example.com/sales" in output.splitlines(), output
    assert b"Supply the password when you connect." in output.splitlines(), output

    cancelled = run_wizard(tool_path, [(b"Database engine", b"\x03")], expected_exit=130)
    assert b"Operation cancelled." in cancelled and b"Connection URL" not in cancelled, cancelled
    print("Verified the installed connection wizard and Ctrl+C with networking disabled.")
