#!/usr/bin/env python3
"""Install Telegram bot settings from JSON on stdin without printing secrets."""

import json
import os
from pathlib import Path
import re
import sys
import tempfile


def main():
    destination = Path(sys.argv[1]).resolve()
    values = json.load(sys.stdin)
    token = str(values.get("token", "")).strip()
    channel = str(values.get("channel", "")).strip()
    if not re.fullmatch(r"\d{5,15}:[A-Za-z0-9_-]{20,}", token):
        raise SystemExit("Invalid Telegram token format")
    if not re.fullmatch(r"(?:@[A-Za-z0-9_]{5,}|-?\d{5,20})", channel):
        raise SystemExit("Invalid channel format")
    allowed = values.get("allowedUserIds")
    if allowed is not None:
        if not isinstance(allowed, list) or not all(re.fullmatch(r"\d{4,20}", str(value)) for value in allowed):
            raise SystemExit("Invalid allowed user IDs")
    existing = destination.read_text(encoding="utf-8").splitlines()
    replaced = ("ORDER_BOT_TOKEN=", "ORDER_BOT_CHANNEL=")
    if allowed is not None:
        replaced += ("ORDER_BOT_ALLOWED_USER_IDS=",)
    keep = [line for line in existing if not line.startswith(replaced)]
    keep.extend((f"ORDER_BOT_TOKEN={token}", f"ORDER_BOT_CHANNEL={channel}"))
    if allowed is not None:
        keep.append("ORDER_BOT_ALLOWED_USER_IDS=" + ",".join(map(str, allowed)))
    fd, temporary = tempfile.mkstemp(prefix=".order-bot-", dir=destination.parent, text=True)
    try:
        os.fchmod(fd, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as output:
            output.write("\n".join(keep) + "\n")
        os.replace(temporary, destination)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    print("Order bot settings installed.")


if __name__ == "__main__":
    main()
