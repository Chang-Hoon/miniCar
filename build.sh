#!/usr/bin/env bash
# Linux에서 net48용 miniCar.exe 빌드 (dotnet SDK의 Roslyn + Mono의 .NET Framework 4.8 참조 어셈블리)
# 필요: apt install dotnet-sdk-8.0 mono-devel
set -euo pipefail
cd "$(dirname "$0")"

REF=/usr/lib/mono/4.8-api
CSC=$(ls -d /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | head -1)
OUT=bin
mkdir -p "$OUT"

dotnet "$CSC" -nologo -noconfig -nostdlib+ \
  -target:winexe -platform:anycpu -optimize+ -deterministic+ \
  -langversion:latest -nullable:enable -warnaserror- -nowarn:CS8632 \
  -win32manifest:app.manifest \
  -win32icon:miniCar.ico \
  -r:"$REF/mscorlib.dll" -r:"$REF/System.dll" -r:"$REF/System.Core.dll" \
  -r:"$REF/System.Drawing.dll" -r:"$REF/System.Windows.Forms.dll" \
  -r:"$REF/System.Xml.dll" \
  -resource:App.config,miniCar.App.config \
  -out:"$OUT/miniCar.exe" \
  *.cs

cp App.config "$OUT/miniCar.exe.config"
echo "OK -> $OUT/miniCar.exe"
