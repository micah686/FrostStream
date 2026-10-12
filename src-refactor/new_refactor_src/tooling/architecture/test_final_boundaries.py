import runpy
import tempfile
import unittest
from pathlib import Path
check = runpy.run_path(str(Path(__file__).with_name('verify-final-boundaries.py')))['check']

def project(refs=(), packages=()):
    return {'references': list(refs), 'packages': list(packages), 'executable': False}

class FinalBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
    def tearDown(self):
        self.temp.cleanup()
    def reject(self, graph, message):
        with self.assertRaisesRegex(ValueError, message):
            check(graph, self.root)
    def test_shared_cannot_hide_lite_dependency_transitively(self):
        self.reject({'Shared/Core/Core.csproj': project(['Adapter.csproj']), 'Adapter.csproj': project(['Lite/Adapter.csproj']), 'Lite/Adapter.csproj': project()}, 'shared code depends on edition')
    def test_full_cannot_reference_lite(self):
        self.reject({'Full/Hosts/Worker/WorkerHost.csproj': project(['Lite/Adapter.csproj']), 'Lite/Adapter.csproj': project()}, 'Full depends on Lite')
    def test_core_cannot_reference_application_module(self):
        self.reject({'Shared/Core/Core.csproj': project(['Shared/Modules/Worker/Worker.csproj']), 'Shared/Modules/Worker/Worker.csproj': project()}, 'lower layer')
    def test_contracts_cannot_reference_implementation(self):
        self.reject({'Shared/Contracts/Contracts.csproj': project(['Shared/Core/Core.csproj']), 'Shared/Core/Core.csproj': project()}, 'contracts must not')
    def test_shared_cannot_reference_lucene(self):
        self.reject({'Shared/Core/Core.csproj': project(packages=['Lucene.Net'])}, 'forbidden SDK')
    def test_sdk_source_leak_is_rejected(self):
        source = self.root/'Shared/Contracts/Leaked.cs'
        source.parent.mkdir(parents=True)
        source.write_text('class Leaked { Lucene.Net.Index.IndexWriter Writer; }')
        self.reject({}, 'implementation in shared')
    def test_migration_runner_is_rejected(self):
        self.reject({'Full/Adapter.csproj': project(packages=['FluentMigrator.Runner.Core'])}, 'historical migration')
    def test_typesense_cannot_be_referenced_by_other_full_host(self):
        self.reject({'Full/Hosts/Worker/WorkerHost.csproj': project(['Shared/Integrations/Typesense/Typesense.csproj']), 'Shared/Integrations/Typesense/Typesense.csproj': project(packages=['Typesense'])}, 'Typesense is owned')
    def test_edition_owned_search_adapters_are_allowed(self):
        check({'Lite/Infrastructure/Infrastructure.csproj': project(packages=['Lucene.Net']), 'Full/Hosts/DataBridge/DataBridgeHost.csproj': project(['Shared/Integrations/Typesense/Typesense.csproj']), 'Shared/Integrations/Typesense/Typesense.csproj': project(packages=['Typesense'])}, self.root)

if __name__ == '__main__':
    unittest.main()
