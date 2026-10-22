#!/bin/sh
# Build the C++ port, check it against the Python reference and print per-stage latency.
# Needs: cmake, OpenCV 4+ (brew install cmake opencv), and the repo's Python venv for the fixtures.
set -e
cd "$(dirname "$0")"
PY=${PY:-../.venv/bin/python}
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j
"$PY" ../scripts/make_native_fixtures.py build/fixtures
./build/parity_test build/fixtures
./build/bench ../tests/golden/photos/p04_dark_skin.png "${RUNS:-20}"
