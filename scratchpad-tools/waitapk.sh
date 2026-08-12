#!/bin/zsh
# Waits for a FluffyField APK to reappear, then records its path in apkpath.
SP=${0:A:h}
while true; do
  F=$(ls -t /Users/playviet/Documents/_Release/GameHub/*.apk 2>/dev/null | head -1)
  [ -n "$F" ] && [ -s "$F" ] && sleep 20 && [ -s "$F" ] && { printf '%s' "$F" > $SP/apkpath; echo "$F"; exit 0; }
  sleep 30
done
