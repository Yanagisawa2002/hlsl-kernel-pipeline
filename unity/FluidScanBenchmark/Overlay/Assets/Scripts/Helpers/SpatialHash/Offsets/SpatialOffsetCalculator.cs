using Seb.Helpers;
using UnityEngine;
using UnityEngine.Rendering;

namespace Seb.Helpers.Internal
{
	public class SpatialOffsetCalculator
	{
		readonly ComputeShader cs = ComputeHelper.LoadComputeShader("SpatialOffsets");
		static readonly int NumInputs = Shader.PropertyToID("numInputs");
		static readonly int Offsets = Shader.PropertyToID("Offsets");
		static readonly int SortedKeys = Shader.PropertyToID("SortedKeys");

		const int initKernel = 0;
		const int offsetsKernel = 1;


		// needsInit: Set to true if offsets buffer has not already been initialized with values >= its length.
		public void Run(bool needsInit, ComputeBuffer sortedKeys, ComputeBuffer offsets)
		{
			if (sortedKeys.count != offsets.count) throw new System.Exception("Count mismatch");
			cs.SetInt(NumInputs, sortedKeys.count);

			if (needsInit)
			{
				cs.SetBuffer(initKernel, Offsets, offsets);
				ComputeHelper.Dispatch(cs, sortedKeys.count, kernelIndex: initKernel);
			}

			cs.SetBuffer(offsetsKernel, Offsets, offsets);
			cs.SetBuffer(offsetsKernel, SortedKeys, sortedKeys);
			ComputeHelper.Dispatch(cs, sortedKeys.count, kernelIndex: offsetsKernel);
		}
        public void Record(CommandBuffer cmd, ComputeBuffer sortedKeys, ComputeBuffer offsets)
        {
            if (sortedKeys.count != offsets.count) throw new System.ArgumentException("Count mismatch");
            cmd.SetComputeIntParam(cs, NumInputs, sortedKeys.count);
            cmd.SetComputeBufferParam(cs, initKernel, Offsets, offsets);
            cmd.SetComputeBufferParam(cs, offsetsKernel, Offsets, offsets);
            cmd.SetComputeBufferParam(cs, offsetsKernel, SortedKeys, sortedKeys);
            int groups = (sortedKeys.count + 255) / 256;
            cmd.DispatchCompute(cs, initKernel, groups, 1, 1);
            cmd.DispatchCompute(cs, offsetsKernel, groups, 1, 1);
        }

	}
}