using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using HlslPerf.FluidBenchmark;
using Seb.Helpers;

namespace Seb.GPUSorting
{
	public class GPUCountSort
	{
		static readonly int ID_InputItems = Shader.PropertyToID("InputItems");
		static readonly int ID_InputSortKeys = Shader.PropertyToID("InputKeys");
		static readonly int ID_SortedItems = Shader.PropertyToID("SortedItems");
		static readonly int ID_SortedKeys = Shader.PropertyToID("SortedKeys");
		static readonly int ID_Counts = Shader.PropertyToID("Counts");
		static readonly int ID_NumInputs = Shader.PropertyToID("numInputs");

		readonly Scan scan = new();
		IExclusiveScan benchmarkScan;
		ScanArm? benchmarkArm;
		readonly ComputeShader cs = ComputeHelper.LoadComputeShader("CountSort");

		ComputeBuffer sortedItemsBuffer;
		ComputeBuffer sortedValuesBuffer;
		ComputeBuffer countsBuffer;

		const int ClearCountsKernel = 0;
		const int CountKernel = 1;
		const int ScatterOutputsKernel = 2;
		const int CopyBackKernel = 3;

		// Sorts a buffer of indices based on a buffer of keys (note that the keys will also be sorted in the process).
		// Note: the maximum possible key value must be known ahead of time for this algorithm (and preferably not be too large), as memory is allocated for all possible keys.
		// Both buffers expected to be of type <uint>
		// Note: index buffer is initialized here to values 0...n before sorting

		public void Run(ComputeBuffer itemsBuffer, ComputeBuffer keysBuffer, uint maxValue)
		{
			// ---- Init ----
			int count = itemsBuffer.count;
			if (ComputeHelper.CreateStructuredBuffer<uint>(ref sortedItemsBuffer, count))
			{
				cs.SetBuffer(ScatterOutputsKernel, ID_SortedItems, sortedItemsBuffer);
				cs.SetBuffer(CopyBackKernel, ID_SortedItems, sortedItemsBuffer);
			}

			if (ComputeHelper.CreateStructuredBuffer<uint>(ref sortedValuesBuffer, count))
			{
				cs.SetBuffer(ScatterOutputsKernel, ID_SortedKeys, sortedValuesBuffer);
				cs.SetBuffer(CopyBackKernel, ID_SortedKeys, sortedValuesBuffer);
			}

			if (ComputeHelper.CreateStructuredBuffer<uint>(ref countsBuffer, (int)maxValue + 1))
			{
				cs.SetBuffer(ClearCountsKernel, ID_Counts, countsBuffer);
				cs.SetBuffer(CountKernel, ID_Counts, countsBuffer);
				cs.SetBuffer(ScatterOutputsKernel, ID_Counts, countsBuffer);
			}

			cs.SetBuffer(ClearCountsKernel, ID_InputItems, itemsBuffer);
			cs.SetBuffer(CountKernel, ID_InputSortKeys, keysBuffer);
			cs.SetBuffer(ScatterOutputsKernel, ID_InputItems, itemsBuffer);
			cs.SetBuffer(CopyBackKernel, ID_InputItems, itemsBuffer);

			cs.SetBuffer(ScatterOutputsKernel, ID_InputSortKeys, keysBuffer);
			cs.SetBuffer(CopyBackKernel, ID_InputSortKeys, keysBuffer);

			cs.SetInt(ID_NumInputs, count);

			// ---- Run ----
			ComputeHelper.Dispatch(cs, count, kernelIndex: ClearCountsKernel);
			ComputeHelper.Dispatch(cs, count, kernelIndex: CountKernel);

			scan.Run(countsBuffer);
			ComputeHelper.Dispatch(cs, count, kernelIndex: ScatterOutputsKernel);
			ComputeHelper.Dispatch(cs, count, kernelIndex: CopyBackKernel);
		}

        public void Record(CommandBuffer cmd, ComputeBuffer items, ComputeBuffer keys, uint maxValue, ScanArm arm)
        {
            int count = items.count;
            if (keys.count != count || items.stride != 4 || keys.stride != 4 || maxValue != (uint)(count - 1))
                throw new System.ArgumentException("The fluid benchmark requires N uint keys in [0,N), and N indices.");
            ScanBackends.Check(keys, count);
            if (benchmarkArm.HasValue && benchmarkArm.Value != arm)
                throw new System.InvalidOperationException("Create a fresh sorter when changing arms.");
            if (benchmarkScan == null) { benchmarkScan = ScanBackends.Create(arm); benchmarkArm = arm; }
            ComputeHelper.CreateStructuredBuffer<uint>(ref sortedItemsBuffer, count);
            ComputeHelper.CreateStructuredBuffer<uint>(ref sortedValuesBuffer, count);
            ComputeHelper.CreateStructuredBuffer<uint>(ref countsBuffer, count);

            FluidGpuSamples.Begin(cmd, FluidGpuSamples.Sort);
            cmd.SetComputeIntParam(cs, ID_NumInputs, count);
            cmd.SetComputeBufferParam(cs, ClearCountsKernel, ID_Counts, countsBuffer);
            cmd.SetComputeBufferParam(cs, ClearCountsKernel, ID_InputItems, items);
            cmd.SetComputeBufferParam(cs, CountKernel, ID_Counts, countsBuffer);
            cmd.SetComputeBufferParam(cs, CountKernel, ID_InputSortKeys, keys);
            int groups = (count + 255) / 256;
            cmd.DispatchCompute(cs, ClearCountsKernel, groups, 1, 1);
            cmd.DispatchCompute(cs, CountKernel, groups, 1, 1);

            FluidGpuSamples.Begin(cmd, FluidGpuSamples.Scan);
            benchmarkScan.Record(cmd, countsBuffer, count);
            FluidGpuSamples.End(cmd, FluidGpuSamples.Scan);

            cmd.SetComputeBufferParam(cs, ScatterOutputsKernel, ID_Counts, countsBuffer);
            cmd.SetComputeBufferParam(cs, ScatterOutputsKernel, ID_InputItems, items);
            cmd.SetComputeBufferParam(cs, ScatterOutputsKernel, ID_InputSortKeys, keys);
            cmd.SetComputeBufferParam(cs, ScatterOutputsKernel, ID_SortedItems, sortedItemsBuffer);
            cmd.SetComputeBufferParam(cs, ScatterOutputsKernel, ID_SortedKeys, sortedValuesBuffer);
            cmd.DispatchCompute(cs, ScatterOutputsKernel, groups, 1, 1);
            cmd.SetComputeBufferParam(cs, CopyBackKernel, ID_InputItems, items);
            cmd.SetComputeBufferParam(cs, CopyBackKernel, ID_InputSortKeys, keys);
            cmd.SetComputeBufferParam(cs, CopyBackKernel, ID_SortedItems, sortedItemsBuffer);
            cmd.SetComputeBufferParam(cs, CopyBackKernel, ID_SortedKeys, sortedValuesBuffer);
            cmd.DispatchCompute(cs, CopyBackKernel, groups, 1, 1);
            FluidGpuSamples.End(cmd, FluidGpuSamples.Sort);
        }

        public void Release()
		{
			ComputeHelper.Release(sortedItemsBuffer, sortedValuesBuffer, countsBuffer);
			scan.Release();
			benchmarkScan?.Dispose();
		}
	}
}