@echo off
rmdir /S /Q obj
rmdir /S /Q bin
dotnet build
dotnet publish Destinet.HttpProxyClient -r net6 -c Debug
dotnet pack
cd bin
cd Debug
FOR %%F IN (*.nupkg) DO (
 set filename=%%F
 goto filefound
)
echo "nuget File not found!
goto quithere
:filefound
dotnet nuget push %filename% -k oy2alf6iutpsufju2okqxsgdjrhqr2kepkntdcjxgty7xe -s https://api.nuget.org/v3/index.json
goto quithere
:quithere
pause