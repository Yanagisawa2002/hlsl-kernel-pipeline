"""Safety and evidence rejection tests. No GPU/device is created."""
import copy
import contextlib
from datetime import datetime, timedelta, timezone
import io
import json
import math
from pathlib import Path
import tempfile
import unittest
from run_ultra_scan import ARMS, COUNTS, GIB, POLICY, analyze, check_resources, counts, parse_log, save, schedule, sha


class UltraScanTests(unittest.TestCase):
    def test_refuse_unsafe_resource_forecasts(self):
        host=dict(availablePhysical=12*GIB,availableCommit=12*GIB)
        gpu=dict(freeBytes=20*GIB,usedBytes=2*GIB,temperatureC=45)
        check_resources(host,gpu,COUNTS[-1])
        for field,value in [('availablePhysical',7*GIB),('availableCommit',7*GIB)]:
            bad=dict(host);bad[field]=value
            with self.assertRaises(RuntimeError):check_resources(bad,gpu,COUNTS[-1])
        for field,value in [('freeBytes',10*GIB),('usedBytes',10*GIB),('temperatureC',80)]:
            bad=dict(gpu);bad[field]=value
            with self.assertRaises(RuntimeError):check_resources(host,bad,COUNTS[-1])

    def test_no_escalation_beyond_cap_or_duplicate_sizes(self):
        self.assertEqual(counts(','.join(map(str,COUNTS))),COUNTS)
        for text in ['',str(1<<29),str((1<<26)-4),str((1<<26)+1),f'{1<<27},{1<<26}',f'{1<<26},{1<<26}']:
            with self.assertRaises(ValueError):counts(text)

    def test_fresh_balanced_pairs_and_ascending_memory(self):
        cases=schedule('confirm',COUNTS)
        self.assertEqual(len(cases),96)
        self.assertEqual(sorted(set(c['tag'] for c in cases)),sorted(c['tag'] for c in cases))
        self.assertEqual([c['count'] for c in cases],sorted(c['count'] for c in cases))
        for n in COUNTS:
            for op in ['inclusive','exclusive']:
                for arm in ARMS:
                    rows=[c for c in cases if c['count']==n and c['operation']==op and c['arm']==arm]
                    self.assertEqual([c['repeat'] for c in rows],list(range(1,7)))
                    self.assertEqual(sum(c['position']==1 for c in rows),3)

    def fixture(self):
        device=dict(kind='device',adapter='synthetic',luid=123,driver='test')
        case=schedule('confirm',COUNTS[:1])[0]
        events=[device,dict(kind='ultraArm',backend=case['arm'],count=case['count'],operation=case['operation']),
                dict(kind='memory',phase='allocated',usage=GIB),
                dict(kind='ultraValidation',phase='before',count=case['count'],operation=case['operation'],passed=True),
                dict(kind='ultraBatch',count=case['count'],operation=case['operation'],warmup=8,measured=100,input='upstream-init-one')]
        events += [dict(kind='ultraSample',index=i,milliseconds=2.) for i in range(100)]
        events += [dict(kind='ultraValidation',phase='after',count=case['count'],operation=case['operation'],passed=True),
                   dict(kind='ultraTotal',count=case['count'],operation=case['operation'],measured=100,meanMs=2.)]
        return device,case,events

    def test_reject_incomplete_corrupt_or_wrong_operation_evidence(self):
        device,case,events=self.fixture()
        text=lambda es:'\n'.join('HPJSON '+json.dumps(e) for e in es)
        self.assertEqual(parse_log(text(events),case,device)['meanMs'],2.)
        for problem in ['device','semantics','postcheck','sample','arithmetic','nan','huge','warmup']:
            bad=copy.deepcopy(events)
            if problem=='device':bad[0]['driver']='wrong'
            elif problem=='semantics':bad[1]['operation']='exclusive'
            elif problem=='postcheck':bad.pop(-2)
            elif problem=='sample':bad.pop(6)
            elif problem=='arithmetic':bad[-1]['meanMs']=3.
            elif problem=='nan':bad[5]['milliseconds']=math.nan
            elif problem=='huge':bad[5]['milliseconds']=101.
            elif problem=='warmup':bad[4]['warmup']=1
            with self.subTest(problem=problem),self.assertRaises(ValueError):parse_log(text(bad),case,device)

    def analysis_fixture(self,folder,sizes=COUNTS[:1]):
        device,_,events=self.fixture();cases=schedule('confirm',sizes)
        plan=dict(stage='confirm',counts=list(sizes),schedule=cases,policy=POLICY,device=device,
                  identity={'runtimeFiles':{'scan/scan.exe':'a'*64}})
        save(folder/'plan.json',plan)
        start=datetime(2026,1,1,tzinfo=timezone.utc)
        for index,case in enumerate(cases):
            es=copy.deepcopy(events)
            mean=(1.25+case['repeat']*.001 if case['arm']=='rts' else 1.)*(2+case['repeat']*.01)
            for e in es:
                if 'count' in e:e['count']=case['count']
                if 'operation' in e:e['operation']=case['operation']
                if e['kind']=='ultraArm':e['backend']=case['arm']
                if e['kind']=='ultraSample':e['milliseconds']=mean
                if e['kind']=='ultraTotal':e['meanMs']=mean
            log=folder/(case['tag']+'.log');log.write_text('\n'.join('HPJSON '+json.dumps(e) for e in es))
            host=dict(availablePhysical=12*GIB,availableCommit=12*GIB)
            gpu=dict(freeBytes=20*GIB,usedBytes=2*GIB,temperatureC=45)
            row=dict(**case,exitCode=0,executableSha256='a'*64,logSha256=sha(log),
                     startedUtc=(start+timedelta(seconds=index*2)).isoformat(),
                     finishedUtc=(start+timedelta(seconds=index*2+1)).isoformat(),
                     telemetry=[dict(phase=phase,host=host,gpu=gpu) for phase in ['before','after']],
                     **parse_log(log.read_text(),case,device))
            save(folder/(case['tag']+'.json'),row)
        save(folder/'completion.json',dict(complete=True,processes=len(cases),planSha256=sha(folder/'plan.json')))
        return cases

    def test_process_level_intervals_and_resource_rejection(self):
        with tempfile.TemporaryDirectory() as directory:
            folder=Path(directory);cases=self.analysis_fixture(folder)
            with contextlib.redirect_stdout(io.StringIO()):analyze(folder,folder/'analysis.json')
            result=json.loads((folder/'analysis.json').read_text())
            self.assertEqual(result['processes'],24)
            self.assertEqual(len(result['cells']),2)
            for cell in result['cells']:
                self.assertEqual(cell['independentRounds'],6)
                self.assertGreater(cell['ratio95Ci'][0],1.)
            path=folder/(cases[0]['tag']+'.json');row=json.loads(path.read_text())
            row['telemetry'][0]['host']['availableCommit']=3*GIB;save(path,row)
            with self.assertRaises(RuntimeError):analyze(folder,folder/'rejected.json')

    def test_partial_audit_preserves_resource_stop_and_only_complete_cells(self):
        with tempfile.TemporaryDirectory() as directory:
            folder=Path(directory);cases=self.analysis_fixture(folder,COUNTS[:2]);rejected=cases[30]
            for case in cases[30:]:
                for suffix in ['.json','.log']:(folder/(case['tag']+suffix)).unlink()
            (folder/'completion.json').unlink()
            reason='Stop: host commit reserve would fall below 4 GiB.'
            save(folder/(rejected['tag']+'.json'),dict(**rejected,preflightRejected=True,stopReason=reason,
                 telemetry=[dict(phase='before',host=dict(availablePhysical=12*GIB,availableCommit=5*GIB),
                                 gpu=dict(freeBytes=20*GIB,usedBytes=2*GIB,temperatureC=45))]))
            save(folder/'failure.json',dict(complete=False,error=f'RuntimeError({reason})'))
            with self.assertRaises(ValueError):analyze(folder,folder/'strict.json')
            with contextlib.redirect_stdout(io.StringIO()):analyze(folder,folder/'partial.json',True)
            result=json.loads((folder/'partial.json').read_text())
            self.assertFalse(result['complete'])
            self.assertEqual(len(result['cells']),2)
            self.assertTrue(all(c['count']==COUNTS[0] for c in result['cells']))
            self.assertEqual(len(result['incompleteCells']),2)
            self.assertEqual(len(result['rejectedPreflights']),1)
            self.assertEqual(result['processes'],30)


if __name__=='__main__':unittest.main()
