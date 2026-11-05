#!/bin/sh
# Compile the whole plugin (UI included) against Mono's WinForms with the .NET SDK's C# compiler.
# Catches C# and API mistakes on Linux; the real build is done on Windows against .NET 9/10.
set -e
HERE=$(cd "$(dirname "$0")" && pwd)
SRC="$HERE/../../src"
CSC=$(ls -d /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | tail -1)
M=/usr/lib/mono/4.5
dotnet "$CSC" -nologo -langversion:latest -unsafe -nullable:disable -warnaserror- -target:library \
  -out:/tmp/m17_compilecheck.dll -nostdlib \
  -r:$M/mscorlib.dll -r:$M/System.dll -r:$M/System.Core.dll -r:$M/System.Drawing.dll \
  -r:$M/System.Windows.Forms.dll -r:$M/Facades/System.Runtime.InteropServices.RuntimeInformation.dll \
  -r:$M/Facades/System.Runtime.dll -r:$M/Facades/netstandard.dll \
  $(find "$SRC" -name '*.cs') "$HERE/SdrSharpStandIn.cs" "$HERE/JsonStandIn.cs"
echo "compile check passed"
