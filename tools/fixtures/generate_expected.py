#!/usr/bin/env python3
"""
Produces the parser oracle files in tests/fixtures/expected/ from the fixture logs.

pyulog (BSD-3-Clause) reads .ulg files and pymavlink's DFReader (LGPL-3.0) reads .bin files.
They are used only here, to produce test expectations; the application never runs Python.

For every message series the oracle stores the record count, first and last timestamp and, for each
numeric field, count/sum/min/max of the decoded values (NaN and infinite values are counted separately).
Comparing these aggregates checks every decoded value without storing the whole data set.

Usage (from the repository root):
    python3 -m venv .venv && .venv/bin/pip install -r tools/fixtures/requirements.txt
    .venv/bin/python tools/fixtures/generate_expected.py
"""
import hashlib
import json
import math
import pathlib
import sys

import numpy as np

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests" / "fixtures"
EXPECTED = FIXTURES / "expected"


def aggregate(values):
    arr = np.asarray(values, dtype=np.float64)
    mask = np.isfinite(arr)
    nonfinite = int((~mask).sum())
    finite = arr[mask]
    if finite.size == 0:
        return {"count": int(arr.size), "nonfinite": nonfinite, "sum": None, "min": None, "max": None}
    return {
        "count": int(arr.size),
        "nonfinite": nonfinite,
        "sum": float(np.sum(finite)),
        "min": float(np.min(finite)),
        "max": float(np.max(finite)),
    }


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def info_value(value):
    if isinstance(value, (bytes, bytearray, list, tuple)):
        return None
    if isinstance(value, float):
        return repr(value)
    return str(value)


def ulog_expected(path):
    from pyulog import ULog

    log = ULog(str(path))
    series = []
    for d in log.data_list:
        fields = {}
        types = {f.field_name: f.type_str for f in d.field_data}
        for name, values in d.data.items():
            # Padding (also inside nested types) and char fields are not signals.
            if name.split(".")[-1].startswith("_padding") or types.get(name) == "char":
                continue
            fields[name] = aggregate(values)
        ts = d.data["timestamp"]
        series.append({
            "message": d.name,
            "instance": d.multi_id,
            "records": int(len(ts)),
            "first_timestamp": int(ts[0]),
            "last_timestamp": int(ts[-1]),
            "fields": fields,
        })
    series.sort(key=lambda s: (s["message"], s["instance"]))

    info = {}
    for key, value in log.msg_info_dict.items():
        v = info_value(value)
        if v is not None:
            info[key] = v

    return {
        "format": "ulog",
        "file": path.name,
        "sha256": sha256(path),
        "header_timestamp": int(log.start_timestamp),
        "last_timestamp": int(log.last_timestamp),
        "corrupt": bool(log.file_corruption),
        "info": info,
        "initial_parameters": {k: float(v) for k, v in sorted(log.initial_parameters.items())},
        "changed_parameters": [[int(t), k, float(v)] for t, k, v in log.changed_parameters],
        "logged_messages": [[int(m.timestamp), m.log_level - ord("0"), m.message] for m in log.logged_messages],
        "tagged_logged_messages": sum(len(v) for v in log.logged_messages_tagged.values()),
        "dropouts": [[int(d.timestamp), int(d.duration)] for d in log.dropouts],
        "series": series,
    }


def dataflash_expected(path):
    from pymavlink import DFReader

    reader = DFReader.DFReader_binary(str(path))
    series = {}
    params = {}
    messages = []
    modes = []
    version = None
    while True:
        m = reader.recv_msg()
        if m is None:
            break
        mtype = m.get_type()
        fmt = m.fmt
        instance = 0
        if getattr(fmt, "instance_field", None) is not None:
            instance = int(getattr(m, fmt.instance_field))
        key = (mtype, instance)
        entry = series.get(key)
        if entry is None:
            entry = {"message": mtype, "instance": instance, "records": 0, "first_timestamp": None,
                     "last_timestamp": None, "values": {}}
            series[key] = entry
        entry["records"] += 1
        time_us = getattr(m, "TimeUS", None)
        if time_us is not None:
            if entry["first_timestamp"] is None:
                entry["first_timestamp"] = int(time_us)
            entry["last_timestamp"] = int(time_us)
        for i, col in enumerate(fmt.columns):
            if fmt.format[i] in "anNZ":
                continue
            entry["values"].setdefault(col, []).append(float(getattr(m, col)))
        if mtype == "PARM":
            params.setdefault(m.Name, float(m.Value))
        elif mtype == "MSG":
            messages.append([int(m.TimeUS), m.Message])
        elif mtype == "MODE":
            modes.append([int(m.TimeUS), int(m.Mode)])
        elif mtype == "VER" and version is None:
            version = m.FWS

    out_series = []
    for entry in sorted(series.values(), key=lambda e: (e["message"], e["instance"])):
        out_series.append({
            "message": entry["message"],
            "instance": entry["instance"],
            "records": entry["records"],
            "first_timestamp": entry["first_timestamp"],
            "last_timestamp": entry["last_timestamp"],
            "fields": {k: aggregate(v) for k, v in entry["values"].items()},
        })

    return {
        "format": "dataflash",
        "file": path.name,
        "sha256": sha256(path),
        "firmware": version,
        "parameters": dict(sorted(params.items())),
        "messages": messages,
        "modes": modes,
        "series": out_series,
    }


def main():
    EXPECTED.mkdir(exist_ok=True)
    for path in sorted(FIXTURES.rglob("*")):
        if path.suffix.lower() == ".ulg":
            data = ulog_expected(path)
        elif path.suffix.lower() == ".bin":
            data = dataflash_expected(path)
        else:
            continue
        target = EXPECTED / (path.name + ".json")
        with open(target, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=1, ensure_ascii=False, allow_nan=False)
            f.write("\n")
        print(f"{path.relative_to(ROOT)} -> {target.relative_to(ROOT)}", file=sys.stderr)


if __name__ == "__main__":
    main()
