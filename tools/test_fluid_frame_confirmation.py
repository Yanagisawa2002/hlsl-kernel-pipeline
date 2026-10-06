"""CPU controls for bounded fluid loads and independent paired inference."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from analyze_fluid_frame_confirmation import audit_stage, paired, thermal_stop_audit
from run_fluid_frame_confirmation import ARMS, GIB, POLICY, SIZES, resources, schedule
from run_ultra_scan import save, sha


class FrameChecks(unittest.TestCase):
    def test_full_fluid_forecast_is_stricter_than_scan_only(self):
        host=dict(availablePhysical=7*GIB,availableCommit=20*GIB)
        gpu=dict(freeBytes=20*GIB,usedBytes=2*GIB,temperatureC=50)
        resources(host,gpu)
        with self.assertRaisesRegex(RuntimeError,'physical-memory'):resources(host,gpu,SIZES[23000])
        resources(dict(availablePhysical=12*GIB,availableCommit=20*GIB),gpu,SIZES[23000])
        with self.assertRaises(ValueError):resources(host,gpu,67108864)

    def test_six_balanced_pairs_and_one_child_schedule(self):
        cases=schedule('confirm')
        self.assertEqual(len(cases),24)
        for density in SIZES:
            for arm in ARMS:
                rows=[c for c in cases if c['density']==density and c['arm']==arm]
                self.assertEqual([c['repeat'] for c in rows],list(range(1,7)))
                self.assertEqual(sum(c['position']==1 for c in rows),3)

    def test_inference_keeps_negative_and_uncertain_results(self):
        self.assertFalse(paired([1]*6,[1.1]*6)['favorablePointwiseInterval'])
        self.assertTrue(paired([1.2]*6,[1]*6)['practicalOnePercentSupported'])
        tiny=paired([1.005]*6,[1]*6)
        self.assertTrue(tiny['favorablePointwiseInterval'])
        self.assertFalse(tiny['practicalOnePercentSupported'])
        uncertain=paired([.95,1.05,.96,1.04,.97,1.03],[1]*6)
        self.assertFalse(uncertain['favorablePointwiseInterval'])

    def test_correlated_frames_cannot_replace_six_processes(self):
        for a,b in (([1]*120,[1]*120),([1]*5,[1]*5),([float('nan')]*6,[1]*6),([0]*6,[1]*6)):
            with self.assertRaises(ValueError):paired(a,b)

    def stop_fixture(self):
        host=dict(availablePhysical=12*GIB,availableCommit=20*GIB)
        gpu=dict(freeBytes=20*GIB,usedBytes=2*GIB,temperatureC=78)
        expected="RuntimeError('Stop: GPU temperature reached 80 C.')"
        row=dict(**schedule('confirm')[0],pid=123,exitCode=1,stopReason=expected,
                 telemetry=[dict(phase='running',host=host,gpu=gpu)])
        return row,dict(complete=False,error=expected)

    def test_missing_rejected_legacy_point_is_not_invented(self):
        row,failure=self.stop_fixture()
        result=thermal_stop_audit(row,failure)
        self.assertFalse(result['stopTemperatureIndependentlyReproduced'])
        self.assertEqual(result['lastRetainedTemperatureC'],78)
        row['telemetry'][-1]['gpu']['temperatureC']=80
        self.assertTrue(thermal_stop_audit(row,failure)['rejectedPointRetained'])

    def test_other_failures_cannot_be_relabelled_thermal(self):
        row,failure=self.stop_fixture();row['stopReason']='correctness failure'
        with self.assertRaises(ValueError):thermal_stop_audit(row,failure)

    def test_partial_audit_never_accepts_a_process_after_stop(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);cases=schedule('confirm')[:2];row,failure=self.stop_fixture()
            plan=dict(stage='confirm',policy=POLICY,cases=cases,identity=dict(playerFilesSha256={'FluidScan.exe':'e'*64}))
            save(root/'plan.json',plan);save(root/'failure.json',failure)
            folder=root/row['name'];folder.mkdir();log=root/(row['name']+'.log');log.write_text('retained thermal attempt')
            row.update(playerSha256='e'*64,logSha256=sha(log),startedUtc='2026-10-06T00:00:00+00:00',endedUtc='2026-10-06T00:00:01+00:00')
            save(root/(row['name']+'.process.json'),row)
            with patch('analyze_fluid_frame_confirmation.schedule',return_value=cases):
                with self.assertRaises(ValueError):audit_stage(root,'confirm')
                _,rows=audit_stage(root,'confirm',partial=True);self.assertEqual(rows,[])
                save(root/(cases[1]['name']+'.process.json'),{})
                with self.assertRaisesRegex(ValueError,'after the thermal stop'):audit_stage(root,'confirm',partial=True)


if __name__=='__main__':unittest.main()
