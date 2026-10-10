import importlib.util
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('start_packaged', Path(__file__).with_name('start-packaged.py'))
startup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(startup)


class DependencyOrderingTests(unittest.TestCase):
    def test_initializers_precede_consumers(self):
        services = {'app': {'depends_on': {'init': {'condition': 'service_completed_successfully'}}},
                    'init': {'depends_on': {'db': {'condition': 'service_healthy'}}}, 'db': {}}
        self.assertEqual(startup.ordered_services(services), ['db', 'init', 'app'])

    def test_rejects_missing_dependency(self):
        with self.assertRaises(ValueError):
            startup.ordered_services({'app': {'depends_on': {'missing': {}}}})

    def test_rejects_cycle(self):
        with self.assertRaises(ValueError):
            startup.ordered_services({'a': {'depends_on': {'b': {}}}, 'b': {'depends_on': {'a': {}}}})
