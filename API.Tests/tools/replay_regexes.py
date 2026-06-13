#!/usr/bin/env python3
"""Replay InvoiceParser.cs text-layer regexes against the fixtures.

Reads the `private static readonly Regex ...` declarations straight out of
API/Services/InvoiceParser.cs (the C# file is the source of truth; nothing is
duplicated by hand here), translates them to Python syntax ((?<x>) -> (?P<x>),
RegexOptions -> re flags) and replays them against the four text fixtures.
Reproduces the verified regex matrix from INVOICE_SCANNING_V2_CONTEXT.md.

Usage:
    python3 replay_regexes.py [path-to-InvoiceParser.cs] [--no-assert]

Default parser path: ../../API/Services/InvoiceParser.cs relative to this
script. Fixtures are read from ../fixtures. With --no-assert the matrix is
printed but not checked (useful for replaying an older parser version).
Exit code 1 when an expectation fails.
"""
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
FIXTURES = HERE.parent / "fixtures"

DECL_RX = re.compile(
    r'private static readonly Regex\s+(\w+)\s*=\s*new\(\s*@"((?:[^"]|"")*)"\s*,\s*([^;]*?)\);',
    re.S)


def load_patterns(cs_path: Path) -> "dict[str, re.Pattern]":
    src = cs_path.read_text(encoding="utf-8")
    patterns = {}
    for name, body, opts in DECL_RX.findall(src):
        body = body.replace('""', '"')           # C# verbatim-string quote escape
        body = body.replace("(?<", "(?P<")       # .NET named group -> Python
        flags = 0
        if "IgnoreCase" in opts:
            flags |= re.I
        if "Multiline" in opts:
            flags |= re.M
        if "Singleline" in opts:
            flags |= re.S
        patterns[name] = re.compile(body, flags)
    return patterns


def sk_number(s: str):
    s = s.replace("EUR", "").replace("€", "").replace(" ", " ").strip()
    s = s.replace(" ", "").replace("\n", "")
    if "," in s:
        s = s.replace(".", "").replace(",", ".")
    try:
        return float(s)
    except ValueError:
        return None


def first_group(rx, text):
    m = rx.search(text)
    return m.group(1) if m else None


def replay(rxs, text):
    row = {}
    inv = first_group(rxs["InvoiceNumberRx"], text)
    if inv is None and "InvoiceNumberNearRx" in rxs:
        inv = first_group(rxs["InvoiceNumberNearRx"], text)
    row["InvoiceNumber"] = inv
    row["IcoRx"] = first_group(rxs["IcoRx"], text)
    row["IcDphRx"] = first_group(rxs["IcDphRx"], text)
    row["IbanRx"] = first_group(rxs["IbanRx"], text) is not None
    row["IssueDateRx"] = first_group(rxs["IssueDateRx"], text)
    row["DueDateRx"] = first_group(rxs["DueDateRx"], text) is not None
    row["HeaderDelDateRx"] = first_group(rxs["HeaderDelDateRx"], text) is not None
    row["PeriodRx"] = rxs["PeriodRx"].search(text) is not None
    row["DeliveryListRx"] = len(rxs["DeliveryListRx"].findall(text))
    row["AkciaRx"] = rxs["AkciaRx"].search(text) is not None
    row["SubtotalRx"] = len(rxs["SubtotalRx"].findall(text))
    row["TotalExclVatRx"] = sk_number(first_group(rxs["TotalExclVatRx"], text) or "")
    row["TotalInclVatRx"] = sk_number(first_group(rxs["TotalInclVatRx"], text) or "")
    row["LinePricesRx"] = len(rxs["LinePricesRx"].findall(text))
    return row


FIXTURE_FILES = [
    ("master", "FA_2600141367.txt"),
    ("150614", "FA_2600150614.txt"),
    ("132372", "FA_2600132372.txt"),
    ("HEKTRANS", "HEKTRANS_20260470.txt"),
]

# Expected matrix for the FIXED parser (V2). Cells: exact string, float
# (amount), int (match count), True (must match something), False (must not).
EXPECTED = {
    "InvoiceNumber":   ("2600141367", "2600150614", "2600132372", "20260470"),
    "IcoRx":           ("43821103", "43821103", "43821103", "36055140"),
    "IcDphRx":         ("SK2022484849", "SK2022484849", "SK2022484849", "SK2020072648"),
    "IbanRx":          (True, True, True, True),
    "IssueDateRx":     (True, True, True, "31.5.2026"),
    "DueDateRx":       (True, True, True, True),
    "HeaderDelDateRx": (True, True, True, True),
    "PeriodRx":        (True, True, True, False),
    "DeliveryListRx":  (13, 9, 8, 0),
    "AkciaRx":         (True, True, True, False),
    "SubtotalRx":      (14, 9, 9, 0),
    "TotalExclVatRx":  (1507.63, 559.50, 329.33, 300.00),
    "TotalInclVatRx":  (1788.43, 657.16, 402.37, 369.00),
    "LinePricesRx":    (24, 24, 21, 0),
}


def cell_ok(expected, actual):
    if expected is True:
        return actual not in (None, False, 0)
    if expected is False:
        return actual is None or actual is False or actual == 0
    if isinstance(expected, float):
        return actual is not None and abs(actual - expected) < 0.005
    return actual == expected


def main():
    args = [a for a in sys.argv[1:] if a != "--no-assert"]
    do_assert = "--no-assert" not in sys.argv
    cs_path = Path(args[0]) if args else HERE.parent.parent / "API" / "Services" / "InvoiceParser.cs"
    rxs = load_patterns(cs_path)

    rows = {}
    for label, fname in FIXTURE_FILES:
        rows[label] = replay(rxs, (FIXTURES / fname).read_text(encoding="utf-8"))

    cols = [label for label, _ in FIXTURE_FILES]
    print("Parser: %s" % cs_path)
    print("%-17s" % "Regex" + "".join("%15s" % c for c in cols))
    failures = []
    for key in EXPECTED:
        line = "%-17s" % key
        for i, c in enumerate(cols):
            actual = rows[c][key]
            if actual is True:
                shown = "y"
            elif actual is False or actual is None:
                shown = "-"
            else:
                shown = str(actual)
            ok = cell_ok(EXPECTED[key][i], actual)
            if do_assert and not ok:
                failures.append("%s[%s]: expected %r, got %r" % (key, c, EXPECTED[key][i], actual))
                shown += "*"
            line += "%15s" % shown
        print(line)

    if do_assert:
        if failures:
            print("\nFAILURES:")
            for f in failures:
                print("  " + f)
            sys.exit(1)
        print("\nAll expectations met.")


if __name__ == "__main__":
    main()
