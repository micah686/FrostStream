import importlib.util
import unittest
import tempfile
from pathlib import Path

spec = importlib.util.spec_from_file_location('start_packaged', Path(__file__).with_name('start-packaged.py'))
startup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(startup)


class DependencyOrderingTests(unittest.TestCase):
    def test_fresh_install_isolates_bind_roots_and_refuses_reuse(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project = 'froststream-install-full-repeat'
            overrides = startup.fresh_environment(project, [], [], root)
            self.assertEqual(len(overrides), 2)
            for value in overrides.values():
                self.assertTrue(Path(value).is_dir())
                self.assertTrue(Path(value).is_relative_to(root / '.artifacts/installations' / project))
            with self.assertRaises(FileExistsError):
                startup.fresh_environment(project, [], [], root)

    def test_fresh_install_rejects_existing_container_or_volume_before_writing(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            project = 'froststream-install-full-repeat'
            for containers, volumes in [([project + '_postgres_1'], []),
                                        ([], [project + '_froststream-postgres-data'])]:
                with self.assertRaisesRegex(ValueError, 'existing containers or volumes'):
                    startup.fresh_environment(project, containers, volumes, root)
            self.assertFalse((root / '.artifacts').exists())

    def test_fresh_install_rejects_path_traversal(self):
        with self.assertRaisesRegex(ValueError, 'Invalid'):
            startup.fresh_environment('froststream-install-../../escape', [], [])

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
