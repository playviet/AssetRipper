#!/bin/zsh
# pb.sh - rebuild probe2 against the live Cpp2IL source, no version bump
SP=${0:A:h}
export DOTNET_ROOT=$HOME/.dotnet
export PATH=$HOME/.dotnet:$PATH
dotnet build $SP/probe2/probe.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -20
