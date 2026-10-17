"""Regression checks for callback ABI cases that previously produced false seeds."""
import json
from pathlib import Path
import unittest

ROOT=Path(__file__).resolve().parents[2]


class CallbackContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.report=json.loads((ROOT/'evidence/engine-extension/native-callback-contracts.json').read_text())
        cls.bindings={b['name']:c for c in cls.report['contracts'] for b in c['callbackBindings']}

    def test_large_return_shifts_receiver_but_vec2_does_not(self):
        for name in ('IMBMission.CreateAgent','IMBMission.GetCameraFrame','IMBMission.GetStraightPathToTarget'):
            self.assertEqual(self.bindings[name]['entryRegister'],'rdx')
            self.assertEqual(self.bindings[name]['pointerKind'],'mission')
        self.assertEqual(self.bindings['IMBAgent.GetCurrentVelocity']['entryRegister'],'rcx')
        self.assertEqual(self.bindings['IMBAgent.GetCurrentVelocity']['pointerKind'],'agent')

    def test_static_and_constructor_callbacks_never_seed_receivers(self):
        for name in ('IMBAgent.GetMaximumNumberOfAgents','IMBAgent.GetActionDirection',
                     'IMBMission.CreateMission','IMBMission.SetCameraIsFirstPerson','IMBMission.StartRecording'):
            self.assertNotIn(name,self.bindings)

    def test_scene_named_argument_has_mission_pointer_caller_proof(self):
        row=self.bindings['IMBMission.GetStraightPathToTarget']
        self.assertEqual(row['signature']['parameters'][0]['name'],'scenePointer')
        self.assertEqual(row['pointerKind'],'mission')
        self.assertTrue(any('Mission::get_Pointer()' in ' '.join(c['context']) for c in row['managedCallerContexts']))

    def test_shared_entry_bindings_agree(self):
        row=self.bindings['IMBAgent.GetCurrentVelocity']
        self.assertEqual(row['entryAddress'],self.bindings['IMBAgent.GetMovementVelocity']['entryAddress'])
        self.assertEqual({b['name'] for b in row['callbackBindings']},{'IMBAgent.GetCurrentVelocity','IMBAgent.GetMovementVelocity'})


if __name__=='__main__':unittest.main()
