#!/bin/sh
# Build the native decoder for Linux (tests) and Windows x86/x64 (the plugin), regenerate the
# C# interop structs from the real C layout, and run the tests.
#
#   ./build.sh            native builds + interop + engine tests + compile check
#   ./build.sh --full     also the signal test suite (needs GNU Radio + gr-m17, see README)
#
# Needs: cmake, gcc, mingw-w64 (x86_64 and i686), the .NET SDK; Mono for the compile check.
# SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
set -e
ROOT=$(cd "$(dirname "$0")" && pwd)
CORE="$ROOT/core"
PLUGIN="$ROOT/plugin"

echo "== native: Linux"
cmake -S "$CORE" -B "$CORE/build" -DCMAKE_BUILD_TYPE=Release >/dev/null
cmake --build "$CORE/build" -j

for arch in x86_64 i686; do
    echo "== native: Windows $arch"
    cmake -S "$CORE" -B "$CORE/build-$arch" -DCMAKE_TOOLCHAIN_FILE="$CORE/cmake/mingw-$arch.cmake" -DCMAKE_BUILD_TYPE=Release >/dev/null
    cmake --build "$CORE/build-$arch" -j
done
mkdir -p "$PLUGIN/native/x64" "$PLUGIN/native/x86"
cp "$CORE/build-x86_64/m17rx.dll" "$PLUGIN/native/x64/m17rx.dll"
cp "$CORE/build-i686/m17rx.dll" "$PLUGIN/native/x86/m17rx.dll"

echo "== interop structs from the C layout"
gcc -I"$CORE/include" "$ROOT/tools/layout_dump.c" -o "$CORE/build/layout_dump"
for cc in x86_64-w64-mingw32-gcc i686-w64-mingw32-gcc; do   # the layout must be the same on every ABI
    $cc -I"$CORE/include" -fsyntax-only "$ROOT/tools/layout_dump.c"
done
"$CORE/build/layout_dump" > "$CORE/build/layout.txt"
python3 "$ROOT/tools/gen_interop.py" "$CORE/build/layout.txt" "$PLUGIN/src/Native/M17RxStructs.g.cs"

if [ "$1" = "--full" ]; then
    echo "== signal tests (gr-m17)"
    C2="$CORE/third_party/codec2-mod"
    gcc -O2 -I"$C2/inc" "$CORE/tests/c2gen.c" "$C2"/src/*.c -lm -o "$CORE/build/c2gen"
    python3 -u "$CORE/tests/run_tests.py" "$CORE/build" /tmp/m17rx_tests
fi

if [ -f /tmp/m17rx_tests/rate32000.cf32 ]; then
    echo "== engine tests (C# against the native library)"
    dotnet build -c Release -o "$PLUGIN/tests/EngineTests/out" "$PLUGIN/tests/EngineTests" >/dev/null
    M17RX_LIBRARY="$CORE/build/libm17rx.so" dotnet "$PLUGIN/tests/EngineTests/out/EngineTests.dll" /tmp/m17rx_tests
else
    echo "== engine tests skipped (run ./build.sh --full once to make the test signals)"
fi

if [ -d /usr/lib/mono/4.5 ]; then
    echo "== compile check of the whole plugin (Mono WinForms)"
    "$PLUGIN/tests/CompileCheck/check.sh"
fi
echo "done"
