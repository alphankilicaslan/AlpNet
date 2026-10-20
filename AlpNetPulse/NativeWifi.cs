using System;
using System.Runtime.InteropServices;

namespace AlpNetPulse
{
    public static class NativeWifi
    {
        [DllImport("Wlanapi.dll", SetLastError = true)]
        public static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("Wlanapi.dll", SetLastError = true)]
        public static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("Wlanapi.dll", SetLastError = true)]
        public static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

        [DllImport("Wlanapi.dll", SetLastError = true)]
        public static extern void WlanFreeMemory(IntPtr pMemory);

        [DllImport("Wlanapi.dll", SetLastError = true)]
        public static extern uint WlanScan(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pDot11Ssid, IntPtr pIeData, IntPtr pReserved);

        [DllImport("Wlanapi.dll", SetLastError = true)]
        public static extern uint WlanGetAvailableNetworkList(IntPtr hClientHandle, ref Guid pInterfaceGuid, uint dwFlags, IntPtr pReserved, out IntPtr ppAvailableNetworkList);

        [StructLayout(LayoutKind.Sequential)]
        public struct DOT11_SSID
        {
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] ucSSID;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_AVAILABLE_NETWORK
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strProfileName;
            public DOT11_SSID dot11Ssid;
            public uint dot11BssType;
            public uint uNumberOfBssids;
            public bool bNetworkConnectable;
            public uint wlanNotConnectableReason;
            public uint uNumberOfPhyTypes;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public uint[] dot11PhyTypes;
            public bool bMorePhyTypes;
            public uint wlanSignalQuality;
            public bool bSecurityEnabled;
            public uint dot11DefaultAuthAlgorithm;
            public uint dot11DefaultCipherAlgorithm;
            public uint dwFlags;
            public uint dwReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_AVAILABLE_NETWORK_LIST
        {
            public uint dwNumberOfItems;
            public uint dwIndex;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strInterfaceDescription;
            public int isState;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_INTERFACE_INFO_LIST
        {
            public uint dwNumberOfItems;
            public uint dwIndex;
            // Following is an array of WLAN_INTERFACE_INFO items
        }

        public static void TriggerScan()
        {
            IntPtr clientHandle = IntPtr.Zero;
            IntPtr interfaceListPtr = IntPtr.Zero;
            try
            {
                uint negotiatedVersion;
                if (WlanOpenHandle(2, IntPtr.Zero, out negotiatedVersion, out clientHandle) == 0)
                {
                    if (WlanEnumInterfaces(clientHandle, IntPtr.Zero, out interfaceListPtr) == 0)
                    {
                        var interfaceList = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(interfaceListPtr);
                        IntPtr currentInterfacePtr = new IntPtr(interfaceListPtr.ToInt64() + Marshal.SizeOf(typeof(uint)) * 2);
                        for (int i = 0; i < interfaceList.dwNumberOfItems; i++)
                        {
                            var interfaceInfo = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(currentInterfacePtr);
                            WlanScan(clientHandle, ref interfaceInfo.InterfaceGuid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                            currentInterfacePtr = new IntPtr(currentInterfacePtr.ToInt64() + Marshal.SizeOf(typeof(WLAN_INTERFACE_INFO)));
                        }
                    }
                }
            }
            catch { }
            finally
            {
                if (interfaceListPtr != IntPtr.Zero) WlanFreeMemory(interfaceListPtr);
                if (clientHandle != IntPtr.Zero) WlanCloseHandle(clientHandle, IntPtr.Zero);
            }
        }
        public static string GetConnectedWifiSignal()
        {
            IntPtr clientHandle = IntPtr.Zero;
            IntPtr interfaceListPtr = IntPtr.Zero;
            IntPtr networkListPtr = IntPtr.Zero;
            try
            {
                uint negotiatedVersion;
                if (WlanOpenHandle(2, IntPtr.Zero, out negotiatedVersion, out clientHandle) == 0)
                {
                    if (WlanEnumInterfaces(clientHandle, IntPtr.Zero, out interfaceListPtr) == 0)
                    {
                        var interfaceList = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(interfaceListPtr);
                        IntPtr currentInterfacePtr = new IntPtr(interfaceListPtr.ToInt64() + Marshal.SizeOf(typeof(uint)) * 2);
                        for (int i = 0; i < interfaceList.dwNumberOfItems; i++)
                        {
                            var interfaceInfo = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(currentInterfacePtr);
                            if (WlanGetAvailableNetworkList(clientHandle, ref interfaceInfo.InterfaceGuid, 0, IntPtr.Zero, out networkListPtr) == 0)
                            {
                                var networkList = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK_LIST>(networkListPtr);
                                IntPtr currentNetworkPtr = new IntPtr(networkListPtr.ToInt64() + Marshal.SizeOf(typeof(WLAN_AVAILABLE_NETWORK_LIST)));
                                for (int j = 0; j < networkList.dwNumberOfItems; j++)
                                {
                                    var network = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK>(currentNetworkPtr);
                                    if ((network.dwFlags & 0x00000001) == 0x00000001) // WLAN_AVAILABLE_NETWORK_CONNECTED
                                    {
                                        return $"Wi-Fi Sinyal: %{network.wlanSignalQuality}";
                                    }
                                    currentNetworkPtr = new IntPtr(currentNetworkPtr.ToInt64() + Marshal.SizeOf(typeof(WLAN_AVAILABLE_NETWORK)));
                                }
                                WlanFreeMemory(networkListPtr);
                                networkListPtr = IntPtr.Zero;
                            }
                            currentInterfacePtr = new IntPtr(currentInterfacePtr.ToInt64() + Marshal.SizeOf(typeof(WLAN_INTERFACE_INFO)));
                        }
                    }
                }
            }
            catch { }
            finally
            {
                if (networkListPtr != IntPtr.Zero) WlanFreeMemory(networkListPtr);
                if (interfaceListPtr != IntPtr.Zero) WlanFreeMemory(interfaceListPtr);
                if (clientHandle != IntPtr.Zero) WlanCloseHandle(clientHandle, IntPtr.Zero);
            }
            return "Wi-Fi Sinyal: Bilinmiyor";
        }

        [DllImport("Wlanapi.dll", SetLastError = true)]
        public static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, int OpCode, IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

        public static bool IsWifiAvailableAndEnabled()
        {
            IntPtr clientHandle = IntPtr.Zero;
            IntPtr interfaceListPtr = IntPtr.Zero;
            try
            {
                uint negotiatedVersion;
                if (WlanOpenHandle(2, IntPtr.Zero, out negotiatedVersion, out clientHandle) == 0)
                {
                    if (WlanEnumInterfaces(clientHandle, IntPtr.Zero, out interfaceListPtr) == 0 && interfaceListPtr != IntPtr.Zero)
                    {
                        var interfaceList = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(interfaceListPtr);
                        if (interfaceList.dwNumberOfItems == 0)
                        {
                            // Sistemde donanımsal Wi-Fi arayüzü yok
                            return false;
                        }

                        IntPtr currentInterfacePtr = new IntPtr(interfaceListPtr.ToInt64() + Marshal.SizeOf(typeof(uint)) * 2);
                        bool anyInterfaceActive = false;

                        for (int i = 0; i < interfaceList.dwNumberOfItems; i++)
                        {
                            var interfaceInfo = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(currentInterfacePtr);

                            // State 0 = wlan_interface_state_not_ready (donanım/yazılım radyo kapalı, uçak modu vb.)
                            if (interfaceInfo.isState >= 1 && interfaceInfo.isState <= 7)
                            {
                                // OpCode 4 = wlan_intf_opcode_radio_state
                                uint dataSize;
                                IntPtr pRadioData = IntPtr.Zero;
                                bool radioIsOn = true;

                                if (WlanQueryInterface(clientHandle, ref interfaceInfo.InterfaceGuid, 4, IntPtr.Zero, out dataSize, out pRadioData, IntPtr.Zero) == 0 && pRadioData != IntPtr.Zero)
                                {
                                    try
                                    {
                                        int numPhys = Marshal.ReadInt32(pRadioData);
                                        if (numPhys > 0)
                                        {
                                            bool anyPhyOn = false;
                                            for (int p = 0; p < numPhys; p++)
                                            {
                                                int offset = 4 + p * 12;
                                                int sw = Marshal.ReadInt32(pRadioData, offset + 4); // 1 = on, 2 = off
                                                int hw = Marshal.ReadInt32(pRadioData, offset + 8); // 1 = on, 2 = off
                                                if (sw == 1 && hw == 1)
                                                {
                                                    anyPhyOn = true;
                                                    break;
                                                }
                                            }
                                            radioIsOn = anyPhyOn;
                                        }
                                    }
                                    finally
                                    {
                                        WlanFreeMemory(pRadioData);
                                    }
                                }

                                if (radioIsOn)
                                {
                                    anyInterfaceActive = true;
                                    break;
                                }
                            }

                            currentInterfacePtr = new IntPtr(currentInterfacePtr.ToInt64() + Marshal.SizeOf(typeof(WLAN_INTERFACE_INFO)));
                        }

                        return anyInterfaceActive;
                    }
                }
            }
            catch { }
            finally
            {
                if (interfaceListPtr != IntPtr.Zero) WlanFreeMemory(interfaceListPtr);
                if (clientHandle != IntPtr.Zero) WlanCloseHandle(clientHandle, IntPtr.Zero);
            }

            // Fallback netsh kontrolü (WlanAPI erişilemezse veya beklenmedik bir durumda)
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh", "wlan show interfaces")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        string lower = output.ToLowerInvariant();
                        if (lower.Contains("there is no wireless interface") ||
                            lower.Contains("sistemde kablosuz arabirim yok") ||
                            lower.Contains("software off") ||
                            lower.Contains("yazılım kapalı") ||
                            lower.Contains("yazilim kapali") ||
                            lower.Contains("hardware off") ||
                            lower.Contains("donanım kapalı") ||
                            lower.Contains("donanim kapali") ||
                            lower.Contains("not ready") ||
                            lower.Contains("hazır değil") ||
                            lower.Contains("hazir degil"))
                        {
                            return false;
                        }

                        if (lower.Contains("connected") || lower.Contains("bağlandı") || lower.Contains("baglandi") ||
                            lower.Contains("disconnected") || lower.Contains("bağlantı kesildi") || lower.Contains("baglanti kesildi"))
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }
    }
}
