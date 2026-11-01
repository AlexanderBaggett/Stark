#!/usr/bin/env python3
"""Measure existing native executables in balanced, randomized process rounds."""

import argparse
from contextlib import ExitStack
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import platform
import random
import statistics
import subprocess
import tempfile
import time


def executable_hashes(variants):
    return {
        name: hashlib.sha256(Path(command[0]).read_bytes()).hexdigest()
        for name, command in variants.items()
    }


def select_affinity(cpu, disabled):
    if disabled:
        return {"mode": "disabled", "cpu": None}
    if not hasattr(os, "sched_getaffinity") or not hasattr(os, "sched_setaffinity"):
        if cpu is not None:
            raise ValueError("Explicit CPU affinity is unsupported on this platform.")
        return {"mode": "unavailable", "cpu": None}
    allowed = os.sched_getaffinity(0)
    selected = min(allowed) if cpu is None else cpu
    if selected not in allowed:
        raise ValueError(f"CPU {selected} is outside the process's allowed affinity.")
    os.sched_setaffinity(0, {selected})
    return {"mode": "pinned", "cpu": selected, "allowed_cpus_before": sorted(allowed)}


def measure(config_path, output_path, rounds, warmups, seed, cpu, no_affinity):
    config = json.loads(config_path.read_text())
    variants = config["variants"]
    baseline = config["baseline"]
    if not isinstance(variants, dict) or not variants or baseline not in variants:
        raise ValueError("Provide variants and a baseline naming one of them.")
    for name, command in variants.items():
        if not isinstance(command, list) or not command or not all(isinstance(a, str) for a in command):
            raise ValueError(f"Variant {name!r} needs a nonempty command array of strings.")
        if not Path(command[0]).is_absolute() or not Path(command[0]).is_file():
            raise ValueError(f"Variant {name!r} needs an existing absolute executable path.")
    cwd = Path(config.get("cwd", str(config_path.parent))).resolve()
    if not cwd.is_dir():
        raise ValueError(f"Working directory does not exist: {cwd}")
    if output_path.resolve() in {Path(command[0]).resolve() for command in variants.values()}:
        raise ValueError("The result file must not replace a measured executable.")

    hashes_before = executable_hashes(variants)
    affinity = select_affinity(cpu, no_affinity)
    started = datetime.now(timezone.utc).isoformat()
    samples = {name: [] for name in variants}
    rng = random.Random(seed)
    with tempfile.TemporaryDirectory(prefix="stark-interleaved-") as temporary, ExitStack() as stack:
        stdout = stack.enter_context(open(os.devnull, "wb"))
        errors = {
            name: stack.enter_context(open(Path(temporary) / str(index), "w+b"))
            for index, name in enumerate(variants)
        }

        def execute(name, measured):
            stderr = errors[name]
            stderr.truncate(0)
            stderr.seek(0)
            start = time.perf_counter_ns()
            child = subprocess.run(variants[name], cwd=cwd, stdout=stdout, stderr=stderr, check=False)
            elapsed_us = (time.perf_counter_ns() - start) / 1000
            if child.returncode != 0:
                stderr.seek(0)
                detail = stderr.read(2000).decode(errors="replace")
                raise RuntimeError(f"{name}: exit {child.returncode}; run incomplete.\n{detail}")
            if measured:
                samples[name].append(elapsed_us)

        for _ in range(warmups):
            for name in variants:
                execute(name, False)
        for _ in range(rounds):
            order = list(variants)
            rng.shuffle(order)
            for name in order:
                execute(name, True)

    hashes_after = executable_hashes(variants)
    if hashes_before != hashes_after:
        raise RuntimeError("A measured executable changed; this run is invalid.")
    means = {name: statistics.mean(values) for name, values in samples.items()}
    summary = {
        name: {
            "samples": len(values),
            "mean_us": means[name],
            "median_us": statistics.median(values),
            "cv_percent": 100 * statistics.stdev(values) / means[name] if len(values) > 1 else 0.0,
            "ratio_to_baseline": means[name] / means[baseline],
        }
        for name, values in samples.items()
    }
    result = {
        "started_utc": started,
        "finished_utc": datetime.now(timezone.utc).isoformat(),
        "platform": platform.platform(),
        "baseline": baseline,
        "commands": variants,
        "cwd": str(cwd),
        "rounds": rounds,
        "warmups_per_variant": warmups,
        "seed": seed,
        "affinity": affinity,
        "startup_included": True,
        "stdout": "null device",
        "stderr": "per-variant regular file, truncated outside timing",
        "sha256_before": hashes_before,
        "sha256_after": hashes_after,
        "samples_us": samples,
        "summary": summary,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(result, indent=2) + "\n")
    return {"baseline": baseline, "rounds": rounds, "summary": summary}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("config", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--rounds", type=int, default=1000)
    parser.add_argument("--warmups", type=int, default=1)
    parser.add_argument("--seed", type=int, default=0)
    affinity = parser.add_mutually_exclusive_group()
    affinity.add_argument("--cpu", type=int)
    affinity.add_argument("--no-affinity", action="store_true")
    args = parser.parse_args()
    if args.rounds <= 0 or args.warmups < 0:
        parser.error("Rounds must be positive and warmups nonnegative.")
    try:
        result = measure(args.config.resolve(), args.output, args.rounds, args.warmups,
                         args.seed, args.cpu, args.no_affinity)
    except (OSError, ValueError, KeyError, RuntimeError) as error:
        parser.exit(1, f"{error}\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
