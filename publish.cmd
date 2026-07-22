@echo off
rem Builds a single self-contained exe (no .NET install needed to run it).
dotnet publish src\LazerCollectionImporter -c Release -r win-x64 --self-contained ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish
if %errorlevel% neq 0 exit /b %errorlevel%
echo.
echo Done: publish\LazerCollectionImporter.exe
