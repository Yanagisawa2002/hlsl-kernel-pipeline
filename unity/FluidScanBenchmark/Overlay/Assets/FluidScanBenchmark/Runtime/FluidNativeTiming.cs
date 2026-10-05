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
        private readonly IntPtr callback;
        private readonly int baseEvent;
        public readonly double[] ms = new double[4];
        public readonly int[] blocks = new int[4];

        public FluidNativeTiming()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
                throw new NotSupportedException("Native GPU timestamps require D3D12.");
            callback = FgpEvent(); baseEvent = FgpBase();
            using (var cmd = new CommandBuffer())
            {
                cmd.IssuePluginEventAndData(callback, baseEvent + 8, IntPtr.Zero);
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
