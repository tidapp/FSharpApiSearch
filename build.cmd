@echo off
cls


dotnet tool restore
dotnet paket restore
dotnet restore
dotnet fake build -t %*
