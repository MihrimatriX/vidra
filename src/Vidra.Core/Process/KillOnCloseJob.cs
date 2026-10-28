using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vidra.Core.Process;

/// <summary>
/// Windows Job Object (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE): uygulama çökse ya da görev yöneticisinden
/// kapatılsa bile, işe atanmış yt-dlp ve onun başlattığı ffmpeg süreçleri işletim sistemi tarafından sonlandırılır.
/// Diğer platformlarda hiçbir şey yapmaz.
/// </summary>
internal static class KillOnCloseJob
{
    private static readonly Lazy<IntPtr> Handle = new(Create);

    public static void Assign(System.Diagnostics.Process p)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var h = Handle.Value;
            if (h != IntPtr.Zero) AssignProcessToJobObject(h, p.Handle);
        }
        catch { /* en kötü ihtimalle eski davranış: süreç ağacını Kill ile öldürürüz */ }
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr CreateWin()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ },
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)size);
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return job; // süreç boyunca açık kalır; uygulama kapanınca tanıtıcı kapanır → alt süreçler ölür
    }

    private static IntPtr Create() => OperatingSystem.IsWindows() ? CreateWin() : IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
