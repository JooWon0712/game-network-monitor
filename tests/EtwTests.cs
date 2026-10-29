using System;
using PcInternetMonitor;

internal static class EtwTests
{
    private static int Main()
    {
        KernelNetworkMonitor.SelfTest();
        Console.WriteLine("ETW decoder/layout/health fixtures passed.");
        return 0;
    }
}
