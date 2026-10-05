using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace HlslPerf.FluidBenchmark
{
    public sealed class FluidNativeTiming
    {
        public static FluidNativeTiming Active;
        [DllImport("FluidGpuTiming", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr FgpEvent();
        [DllImport("FluidGpuTiming", CallingConvention = CallingConvention.Cdecl)] private static extern int FgpBase();
        [DllImport("FluidGpuTiming", CallingConvention = CallingConvention.Cdecl)] public static extern ulong FgpFrequency();
        [DllImport("FluidGpuTiming", CallingConvention = CallingConvention.Cdecl)] private static extern int FgpRead(ulong frame, [Out] double[] ms, [Out] int[] blocks);
        [DllImport("FluidGpuTiming", CallingConvention = CallingConvention.Cdecl)] private static extern int FgpConfigure(int calls, int mask);
        private readonly IntPtr callback;
        private readonly int baseEvent;
        public readonly double[] ms = new double[5];
        public readonly int[] blocks = new int[5];

        public FluidNativeTiming(int calls = 3, int mask = 31)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
                throw new NotSupportedException("Native GPU timestamps require D3D12.");
            callback = FgpEvent(); baseEvent = FgpBase();
            if (FgpConfigure(calls, mask) != 1) throw new InvalidOperationException("Native timestamp configuration rejected.");
            using (var cmd = new CommandBuffer())
            {
                cmd.IssuePluginEventAndData(callback, baseEvent + 10, IntPtr.Zero);
                Graphics.ExecuteCommandBuffer(cmd);
            }
        }
        public void Record(CommandBuffer cmd, int metric, bool end)
        {
            cmd.IssuePluginEventAndData(callback, baseEvent + metric * 2 + (end ? 1 : 0), new IntPtr(Time.frameCount));
        }
        public bool Read(int frame)
        {
            int state = FgpRead((ulong)frame, ms, blocks);
            if (state < 0) throw new InvalidOperationException("Native GPU timestamp error " + state);
            return state == 1;
        }
    }
}
