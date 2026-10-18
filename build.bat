@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo  .NET SDK nao encontrado. Instale com: winget install Microsoft.DotNet.SDK.8
  pause
  exit /b 1
)

echo Compilando Radiodex...
dotnet publish src\Radiodex\Radiodex.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o out
if errorlevel 1 (
  echo.
  echo  A compilacao falhou. Veja a mensagem acima.
  pause
  exit /b 1
)

echo.
echo  Pronto: out\Radiodex.exe
start "" "out\Radiodex.exe"
endlocal
