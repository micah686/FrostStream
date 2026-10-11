import json, runpy, tempfile, unittest
from pathlib import Path
check = runpy.run_path(str(Path(__file__).with_name('verify-boundaries.py')))['check']

def project(refs=(), packages=()):
    return {'references': list(refs), 'packages': list(packages), 'executable': False}

class EditionBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.root = Path(self.temp.name)
    def tearDown(self): self.temp.cleanup()
    def test_shared_cannot_reference_full(self):
        graph = {'Shared/Modules/Api/Api.csproj': project(['Full/Security/Security.csproj']), 'Full/Security/Security.csproj': project()}
        with self.assertRaisesRegex(ValueError, 'forbidden Full'): check(graph, self.root)
    def test_shared_cannot_hide_sdk_in_transitive_library(self):
        graph = {'Shared/Persistence/Persistence.csproj': project(['Adapters/Adapter.csproj']), 'Adapters/Adapter.csproj': project(packages=['Npgsql'])}
        with self.assertRaisesRegex(ValueError, 'forbidden SDK'): check(graph, self.root)
    def test_neutral_persistence_cannot_depend_on_sqlite_sdk(self):
        with self.assertRaisesRegex(ValueError, 'forbidden SDK'):
            check({'Shared/Persistence/Persistence.csproj': project(packages=['Microsoft.EntityFrameworkCore.Sqlite'])}, self.root)
    def test_lite_cannot_depend_on_full_sdk(self):
        with self.assertRaisesRegex(ValueError, 'forbidden SDK'):
            check({'Lite/Infrastructure/Infrastructure.csproj': project(packages=['VaultSharp'])}, self.root)
    def test_security_is_only_for_api_host(self):
        graph = {'Full/Hosts/Worker/WorkerHost.csproj': project(['Full/Security/Security.csproj']), 'Full/Security/Security.csproj': project()}
        with self.assertRaisesRegex(ValueError, 'only the Full WebAPI'): check(graph, self.root)
    def test_typesense_exception_is_isolated_and_allowed_for_lite(self):
        graph = {'Lite/Backend/Lite.csproj': project(['Shared/Integrations/Typesense/Typesense.csproj']), 'Shared/Integrations/Typesense/Typesense.csproj': project(packages=['Typesense'])}
        check(graph, self.root)
    def test_restored_transitive_sdk_fails_even_without_declared_reference(self):
        assets = self.root/'Lite/Backend/obj/project.assets.json'; assets.parent.mkdir(parents=True)
        assets.write_text(json.dumps({'libraries': {'NATS.Client.Core/2.0': {}}}))
        with self.assertRaisesRegex(ValueError, 'restored graph'): check({}, self.root, True)
    def test_required_restore_cannot_be_silently_skipped(self):
        with self.assertRaisesRegex(ValueError, 'Restore Lite'): check({}, self.root, True)

if __name__ == '__main__': unittest.main()
