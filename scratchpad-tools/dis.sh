#!/bin/zsh
# dis.sh <hexaddr> [bytes] - disassemble at an address in the pinned binary
SP=${0:A:h}
A=$1
N=${2:-0x40}
objdump -d --start-address=0x$A --stop-address=$((0x$A + N)) $SP/bin/libil2cpp.so | sed -n '4,60p'
