#!/bin/zsh
# catchdiff.sh <exportA> <exportB> <file...> - did these files gain a `catch`, or only markers?
#
# The question mdiff.py leaves open: a file that gained markers either gained a handler that was not there
# before (the good trade) or is collateral. This answers it per file.
A="/Users/playviet/Documents/_APK/FluffyField/Fluffy Field_1.0.3_608101946_$1/ExportedProject/Assets/Scripts"
B="/Users/playviet/Documents/_APK/FluffyField/Fluffy Field_1.0.3_608101946_$2/ExportedProject/Assets/Scripts"
shift 2
for f in "$@"; do
  a=$(grep -c "catch (" "$A/$f" 2>/dev/null)
  b=$(grep -c "catch (" "$B/$f" 2>/dev/null)
  echo "  ${a:-0} -> ${b:-0}   $f"
done
