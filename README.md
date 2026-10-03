# ELK-BLEDOM & BJ_LED Controller — Windows (WinUI 3)

Native Windows port of [Heitezy/LEDStripController](https://github.com/Heitezy/LEDStripController),
built with **WinUI 3 / Windows App SDK 1.8** and **.NET 8**.

## Build & run

Requirements: Windows 10 1809+ or Windows 11, a Bluetooth LE adapter, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
(or Visual Studio 2022 with the ".NET desktop development" workload).

```powershell
dotnet run -c Release -p:Platform=x64
```
or open the folder in Visual Studio, pick **x64 / Release**, press F5.
The app is unpackaged + self-contained, so no MSIX signing or runtime install is needed.
