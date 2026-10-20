#!/usr/bin/env bash
# Builds Orange's own small LGPL FFmpeg for Windows x64 (cross-compiled with MinGW on Ubuntu).
# Native FFmpeg codecs + Media Foundation (H.264/HEVC/AAC/MP3 hardware/OS encoders) + LAME (mp3), Opus,
# libvpx (WebM VP8/VP9) and dav1d (AV1 decoding), zlib (PNG). No GPL, no nonfree, no network, no devices.
set -euo pipefail
HOST=x86_64-w64-mingw32
P=/opt/w64
J=$(nproc)
export PKG_CONFIG_PATH=$P/lib/pkgconfig PKG_CONFIG_LIBDIR=$P/lib/pkgconfig
mkdir -p $P src && cd src

fetch() { curl -fsSL "$1" -o "$2"; echo "$3  $2" | sha256sum -c -; }

# zlib
fetch https://github.com/madler/zlib/releases/download/v1.3.1/zlib-1.3.1.tar.gz zlib.tgz 9a93b2b7dfdac77ceba5a558a580e74667dd6fede4585b91eefb60f03b72df23
tar xf zlib.tgz && (cd zlib-1.3.1 && make -f win32/Makefile.gcc PREFIX=$HOST- BINARY_PATH=$P/bin INCLUDE_PATH=$P/include LIBRARY_PATH=$P/lib install -j$J >/dev/null)
mkdir -p $P/lib/pkgconfig && cat > $P/lib/pkgconfig/zlib.pc <<PC
prefix=$P
libdir=\${prefix}/lib
includedir=\${prefix}/include
Name: zlib
Description: zlib
Version: 1.3.1
Libs: -L\${libdir} -lz
Cflags: -I\${includedir}
PC

# LAME (LGPL) — mp3 encoding
fetch https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz lame.tgz ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e
tar xf lame.tgz && (cd lame-3.100 && ./configure --host=$HOST --prefix=$P --disable-shared --enable-static --disable-frontend --disable-decoder --enable-nasm >/dev/null && make -j$J >/dev/null && make install >/dev/null)

# Opus (BSD)
fetch https://downloads.xiph.org/releases/opus/opus-1.5.2.tar.gz opus.tgz 65c1d2f78b9f2fb20082c38cbe47c951ad5839345876e46941612ee87f9a7ce1
tar xf opus.tgz && (cd opus-1.5.2 && ./configure --host=$HOST --prefix=$P --disable-shared --enable-static --disable-doc --disable-extra-programs >/dev/null && make -j$J >/dev/null && make install >/dev/null)

# libvpx (BSD) — WebM VP8/VP9
git clone -q --depth 1 -b v1.14.1 https://chromium.googlesource.com/webm/libvpx
(cd libvpx && CROSS=$HOST- ./configure --target=x86_64-win64-gcc --prefix=$P --enable-static --disable-shared --disable-examples --disable-tools --disable-docs --disable-unit-tests --enable-vp9-highbitdepth --as=nasm >/dev/null && make -j$J >/dev/null && make install >/dev/null)

# dav1d (BSD) — AV1 decoding
git clone -q --depth 1 -b 1.4.3 https://code.videolan.org/videolan/dav1d.git
cat > cross.txt <<X
[binaries]
c = '$HOST-gcc'
ar = '$HOST-ar'
strip = '$HOST-strip'
windres = '$HOST-windres'
pkg-config = 'pkg-config'
[host_machine]
system = 'windows'
cpu_family = 'x86_64'
cpu = 'x86_64'
endian = 'little'
X
(cd dav1d && meson setup build --cross-file ../cross.txt --prefix=$P --libdir=lib -Ddefault_library=static -Denable_tools=false -Denable_tests=false >/dev/null && ninja -C build install >/dev/null)

# FFmpeg 7.1 (LGPL-2.1+)
fetch https://ffmpeg.org/releases/ffmpeg-7.1.tar.xz ffmpeg.txz 40973d44970dbc83ef302b0609f2e74982be2d85916dd2ee7472d30678a7abe6
tar xf ffmpeg.txz && cd ffmpeg-7.1
./configure --prefix=$P/ff --target-os=mingw32 --arch=x86_64 --cross-prefix=$HOST- --pkg-config=pkg-config --pkg-config-flags=--static \
  --enable-shared --disable-static --disable-doc --disable-debug --disable-ffplay --disable-network --disable-devices --disable-indevs --disable-outdevs \
  --disable-autodetect --enable-w32threads --enable-mediafoundation --enable-zlib \
  --enable-libmp3lame --enable-libopus --enable-libvpx --enable-libdav1d \
  --extra-cflags="-I$P/include -O2" --extra-ldflags="-L$P/lib -static-libgcc" \
  --disable-encoder=vorbis --disable-decoder=libvpx_vp8 --disable-decoder=libvpx_vp9
make -j$J >/dev/null
make install >/dev/null
mkdir -p /tmp/out/bin
cp $P/ff/bin/*.dll $P/ff/bin/ffmpeg.exe $P/ff/bin/ffprobe.exe /tmp/out/bin/
cp COPYING.LGPLv2.1 /tmp/out/LICENSE.txt
$HOST-strip --strip-unneeded /tmp/out/bin/*
# Any MinGW runtime DLL the binaries need goes along (checked, not guessed).
for dll in libwinpthread-1.dll libstdc++-6.dll libgcc_s_seh-1.dll; do
  if $HOST-objdump -p /tmp/out/bin/*.dll /tmp/out/bin/*.exe | grep -qi "DLL Name: $dll"; then
    cp "$(find /usr/lib/gcc/$HOST /usr/$HOST -name $dll | head -1)" /tmp/out/bin/
  fi
done
./ffbuild/version.sh . /dev/stdout > /tmp/out/VERSION.txt || true
grep -E "^(FFMPEG_CONFIGURATION|CONFIG_GPL|CONFIG_NONFREE)" config.h ffbuild/config.mak > /tmp/out/CONFIG.txt || true
du -sh /tmp/out/bin; ls -la /tmp/out/bin
