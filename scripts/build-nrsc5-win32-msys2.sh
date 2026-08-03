#!/usr/bin/env bash
set -euo pipefail

export PATH="/mingw32/bin:/usr/bin:$PATH"
src="$(cygpath -u "$SDRSHARP_NRSC5_SOURCE")"
out="$(cygpath -u "$SDRSHARP_NRSC5_BUILD")"

cmake -S "$src" -B "$out" -G "MSYS Makefiles" \
  -D BUILD_CLI=OFF \
  -D USE_STATIC=OFF \
  -D USE_SYSTEM_LIBUSB=OFF \
  -D USE_SYSTEM_RTLSDR=OFF \
  -D USE_SYSTEM_FFTW=OFF \
  -D USE_SSE=ON \
  -D CMAKE_BUILD_TYPE=Release \
  -D CMAKE_INSTALL_PREFIX=/mingw32

cmake --build "$out" --target nrsc5 --parallel 4
cmake --install "$out"
