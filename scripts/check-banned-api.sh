#!/usr/bin/env bash
# Proves the network ban is wired: code that uses a banned type must fail the Later.Core build with RS0030.
set -euo pipefail
probe=windows/Later.Core/BannedApiProbe.cs
trap 'rm -f "$probe"' EXIT
for type in System.Net.Http.HttpClient System.Net.Sockets.Socket System.Net.WebClient; do
  cat > "$probe" <<EOT
namespace Later.Core;
internal static class BannedApiProbe { internal static object Make() => new $type(); }
EOT
  if out=$(dotnet build windows/Later.Core -c Release --no-incremental 2>&1); then
    echo "probe using $type compiled: the network ban is not active" >&2; exit 1
  fi
  echo "$out" | grep -q 'RS0030' || { echo "$out" | tail -20; echo "probe using $type failed, but not with RS0030" >&2; exit 1; }
  echo "banned API probe correctly rejected $type (RS0030)"
done
