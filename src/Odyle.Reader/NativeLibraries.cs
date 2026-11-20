using System.Runtime.InteropServices;
// Windows libraries the app calls (user32, dwmapi, crypt32, iphlpapi, kernel32) load from System32 only,
// never from the application folder: the portable ZIP is often unpacked among other downloads, where a
// planted dwmapi.dll would otherwise load first. Npcap's wpcap.dll has its own resolver (PacketCapture).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
